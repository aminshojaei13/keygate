using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Keygate.Protection.Device
{
    /// <summary>
    /// A derived device identity. <see cref="Id"/> is the value sent to the
    /// server as the activation identifier.
    /// </summary>
    public sealed class DeviceIdentity
    {
        public string Id { get; internal set; } = "";
        public string Platform { get; internal set; } = "other";
        /// <summary>
        /// False only when the machine exposed no usable hardware or OS
        /// component and the SDK fell back to a persisted random value.
        /// </summary>
        public bool IsHardwareDerived { get; internal set; }
        /// <summary>
        /// Names of the components that contributed, never their raw values.
        /// </summary>
        public IReadOnlyList<string> Components { get; internal set; } = new List<string>();

        public override string ToString() => Id;
    }

    /// <summary>
    /// Syntactic helpers for the versioned Keygate device identifier.
    /// Mirrors pkg/deviceid in the Go server.
    /// </summary>
    public static class DeviceIdFormats
    {
        public const string ComponentDomain = "keygate:device:v1:";
        public const string CompositeDomain = "keygate:device:v1:composite:";

        private static readonly HashSet<string> Platforms = new HashSet<string>(StringComparer.Ordinal)
        {
            "darwin", "windows", "linux", "android", "ios", "browser", "other"
        };

        public static bool IsValidKgid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 256) return false;
            if (!value.StartsWith("kgid:v1:", StringComparison.Ordinal)) return false;
            string[] parts = value.Substring("kgid:v1:".Length).Split(':');
            if (parts.Length != 2) return false;
            if (!Platforms.Contains(parts[0])) return false;
            if (parts[1].Length != 64) return false;
            return parts[1].All(IsLowerHex);
        }

        private static bool IsLowerHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

        /// <summary>
        /// Hashes one normalized hardware/OS characteristic with the same
        /// domain separation the Go server uses, so digests are comparable
        /// across implementations.
        /// </summary>
        public static string HashComponent(string component, string rawValue)
        {
            string normalized = NormalizeValue(rawValue);
            if (normalized.Length == 0) return "";
            using (var sha = SHA256.Create())
            {
                byte[] input = Encoding.UTF8.GetBytes(
                    ComponentDomain + component.ToLowerInvariant() + ":" + normalized);
                return ToLowerHex(sha.ComputeHash(input));
            }
        }

        /// <summary>
        /// Computes the canonical 64-hex composite digest from component
        /// hashes. Keys are sorted and lowercased; empty values are skipped.
        /// </summary>
        public static string ComputeCompositeDigest(
            string platform, IReadOnlyDictionary<string, string> componentHashes)
        {
            string normalizedPlatform = NormalizePlatform(platform);
            var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in componentHashes)
            {
                string key = pair.Key.ToLowerInvariant();
                string value = (pair.Value ?? "").Trim().ToLowerInvariant();
                if (value.Length > 0 && !normalized.ContainsKey(key)) normalized[key] = value;
            }
            var keys = normalized.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

            if (keys.Count == 0)
                throw new ArgumentException("At least one component hash is required.", nameof(componentHashes));

            var sb = new StringBuilder();
            sb.Append(CompositeDomain).Append(normalizedPlatform).Append(':');
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(keys[i]).Append('=').Append(normalized[keys[i]]);
            }

            using (var sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            }
        }

        public static string Format(string platform, string compositeDigest)
        {
            return "kgid:v1:" + NormalizePlatform(platform) + ":" +
                   compositeDigest.Trim().ToLowerInvariant();
        }

        public static string NormalizePlatform(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return "other";
            string p = platform!.Trim().ToLowerInvariant();
            return Platforms.Contains(p) ? p : "other";
        }

        /// <summary>
        /// Trims, drops control characters and lowercases. Matches
        /// deviceid.NormalizeValue in the Go server.
        /// </summary>
        public static string NormalizeValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (!char.IsControl(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    internal static class DeviceIdentityFactory
    {
        public static DeviceIdentity Create(
            string? overrideId, string? storedFallbackId, bool includeHostname)
        {
            if (!string.IsNullOrWhiteSpace(overrideId))
            {
                return new DeviceIdentity
                {
                    Id = overrideId!.Trim(),
                    Platform = PlatformName(),
                    IsHardwareDerived = true,
                    Components = new[] { "override" }
                };
            }

            string platform = PlatformName();
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in DeviceComponentCollector.Collect())
            {
                if (!string.IsNullOrWhiteSpace(pair.Value)) raw[pair.Key] = pair.Value;
            }
            if (includeHostname)
            {
                string host = SafeHostname();
                if (!string.IsNullOrEmpty(host)) raw["hostname"] = host;
            }

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in raw)
            {
                string h = DeviceIdFormats.HashComponent(pair.Key, pair.Value);
                if (!string.IsNullOrEmpty(h)) hashes[pair.Key] = h;
            }

            if (hashes.Count == 0)
            {
                // No hardware identifiers were readable (locked-down
                // container, unusual OS). A random, locally persisted value
                // keeps the identifier stable for this installation. It is
                // explicitly not hardware-derived; callers can surface that.
                string fallback = storedFallbackId ?? GenerateRandomDigest();
                return new DeviceIdentity
                {
                    Id = DeviceIdFormats.Format(platform, fallback),
                    Platform = platform,
                    IsHardwareDerived = false,
                    Components = Array.Empty<string>()
                };
            }

            string digest = DeviceIdFormats.ComputeCompositeDigest(platform, hashes);
            return new DeviceIdentity
            {
                Id = DeviceIdFormats.Format(platform, digest),
                Platform = platform,
                IsHardwareDerived = true,
                Components = hashes.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList()
            };
        }

        public static string PlatformName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "darwin";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
            return "other";
        }

        private static string GenerateRandomDigest()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string SafeHostname()
        {
            try { return Environment.MachineName ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Collects stable, non-personal machine characteristics. Every probe is
    /// optional: a missing value simply contributes no component. Raw
    /// serials never leave the process — only their domain-separated hashes
    /// go into the identifier.
    /// </summary>
    internal static class DeviceComponentCollector
    {
        private const int ShellTimeoutMs = 3000;

        public static IEnumerable<KeyValuePair<string, string>> Collect()
        {
            string platform = DeviceIdentityFactory.PlatformName();
            switch (platform)
            {
                case "darwin":
                    return CollectDarwin();
                case "windows":
                    return CollectWindows();
                case "linux":
                    return CollectLinux();
                default:
                    return Array.Empty<KeyValuePair<string, string>>();
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> CollectDarwin()
        {
            var values = new List<KeyValuePair<string, string>>();

            string ioreg = Run("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice");
            string? platformUuid = ExtractIoregValue(ioreg, "IOPlatformUUID");
            Add(values, "board", platformUuid);

            string? volumeUuid = ExtractDiskUtilUuid(
                Run("/usr/sbin/diskutil", "info /"));
            Add(values, "disk", volumeUuid);

            string? cpu = NullIfEmpty(Run("/usr/sbin/sysctl", "-n machdep.cpu.brand_string"));
            if (cpu == null) cpu = NullIfEmpty(Run("/usr/sbin/sysctl", "-n hw.model"));
            Add(values, "cpu", cpu);

            Add(values, "nic", PrimaryMacAddress());
            return values;
        }

        private static IEnumerable<KeyValuePair<string, string>> CollectWindows()
        {
            var values = new List<KeyValuePair<string, string>>();

            Add(values, "os", ReadRegistry(
                @"SOFTWARE\Microsoft\Cryptography", "MachineGuid", "HKLM"));
            Add(values, "cpu", ReadRegistry(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "HKLM"));

            string? board = NullIfEmpty(Run("powershell",
                "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_BaseBoard).SerialNumber\""));
            if (board == null || IsPlaceholder(board))
                board = NullIfEmpty(Run("powershell",
                    "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_ComputerSystemProduct).UUID\""));
            Add(values, "board", IsPlaceholder(board) ? null : board);

            Add(values, "disk", SystemVolumeSerial());
            Add(values, "nic", PrimaryMacAddress());
            return values;
        }

        private static IEnumerable<KeyValuePair<string, string>> CollectLinux()
        {
            var values = new List<KeyValuePair<string, string>>();

            Add(values, "board",
                NullIfEmpty(ReadFirstLine("/sys/class/dmi/id/product_uuid")) ??
                NullIfEmpty(ReadFirstLine("/sys/class/dmi/id/board_serial")));

            Add(values, "os",
                NullIfEmpty(ReadFirstLine("/etc/machine-id")) ??
                NullIfEmpty(ReadFirstLine("/var/lib/dbus/machine-id")));

            Add(values, "disk", NullIfEmpty(Run("findmnt", "-no UUID /")));
            Add(values, "cpu", LinuxCpuModel());
            Add(values, "nic", PrimaryMacAddress());
            return values;
        }

        private static string? LinuxCpuModel()
        {
            try
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string key = line.Substring(0, colon).Trim();
                    if (key.Equals("model name", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("Hardware", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("Processor", StringComparison.OrdinalIgnoreCase))
                    {
                        string value = line.Substring(colon + 1).Trim();
                        if (value.Length > 0) return value;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? PrimaryMacAddress()
        {
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                                n.GetPhysicalAddress() != null &&
                                n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                    .Where(n => !IsVirtualAdapter(n))
                    .OrderBy(n => n.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(n => n.Name, StringComparer.Ordinal)
                    .ToList();
                if (candidates.Count == 0) return null;
                return string.Join(":", candidates[0].GetPhysicalAddress()
                    .GetAddressBytes().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            }
            catch { return null; }
        }

        private static bool IsVirtualAdapter(NetworkInterface n)
        {
            string text = ((n.Name ?? "") + " " + (n.Description ?? "")).ToLowerInvariant();
            string[] markers =
            {
                "virtual", "vmware", "hyper-v", "vbox", "docker", "veth", "br-",
                "virbr", "tun", "tap", "utun", "tailscale", "loopback", "bridge",
                "vpn", "wireguard", "zero", "meta"
            };
            return markers.Any(m => text.Contains(m));
        }

        private static string? SystemVolumeSerial()
        {
            try
            {
                string root = Path.GetPathRoot(Environment.SystemDirectory ?? "C:\\") ?? "C:\\";
                uint serial;
                if (GetVolumeInformation(root, null, 0, out serial, out _, out _, null, 0))
                    return serial.ToString("x8", CultureInfo.InvariantCulture);
            }
            catch { }
            return null;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder? fileSystemNameBuffer,
            int fileSystemNameSize);

        private static string? ReadRegistry(string subKey, string valueName, string hive)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
                Microsoft.Win32.RegistryKey? root = hive == "HKLM"
                    ? Microsoft.Win32.Registry.LocalMachine
                    : Microsoft.Win32.Registry.CurrentUser;
                using (Microsoft.Win32.RegistryKey? key = root?.OpenSubKey(subKey))
                {
                    return key?.GetValue(valueName) as string;
                }
            }
            catch { return null; }
        }

        private static string? ExtractIoregValue(string output, string key)
        {
            foreach (string line in output.Split('\n'))
            {
                if (!line.Contains(key)) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string value = line.Substring(eq + 1).Trim().Trim('"');
                if (value.Length > 0) return value;
            }
            return null;
        }

        private static string? ExtractDiskUtilUuid(string output)
        {
            foreach (string line in output.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("Volume UUID:", StringComparison.OrdinalIgnoreCase))
                    continue;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string value = line.Substring(colon + 1).Trim();
                if (value.Length > 0) return value;
            }
            return null;
        }

        private static void Add(List<KeyValuePair<string, string>> list, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                list.Add(new KeyValuePair<string, string>(key, value!.Trim()));
        }

        private static string? ReadFirstLine(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string line in File.ReadLines(path))
                {
                    string value = line.Trim();
                    if (value.Length > 0) return value;
                }
            }
            catch { }
            return null;
        }

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

        private static bool IsPlaceholder(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string v = value!.Trim().ToLowerInvariant();
            return v == "to be filled by o.e.m." || v == "default string" ||
                   v == "none" || v == "unknown" || v == "00000000-0000-0000-0000-000000000000" ||
                   v.All(c => c == '0' || c == ' ' || c == 'f' || c == '-');
        }

        private static string Run(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null) return "";
                    if (!process.WaitForExit(ShellTimeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return "";
                    }
                    // Reading after exit is safe: these commands emit a few
                    // hundred bytes, far below the pipe buffer.
                    return process.StandardOutput.ReadToEnd();
                }
            }
            catch { return ""; }
        }
    }
}
