using System.Security.Cryptography;
using System.Text;

namespace Keygate.Lab;

public static class LabUtil
{
    public static string RandomHex(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string FormatUnix(long unix)
    {
        if (unix <= 0) return "(none)";
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                .ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"(invalid unix {unix})";
        }
    }

    public static string FormatDate(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "(none)";

    public static string MaskKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(none)";
        key = key.Trim();
        if (key.Length <= 10) return key[..2] + "..." + key[^2..];
        return key[..6] + "..." + key[^4..];
    }

    public static string SafeName(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > 32 ||
            !char.IsAsciiLetterOrDigit(trimmed[0]) ||
            trimmed.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
        {
            throw new LabException(
                "INVALID_NAME",
                "install names must be 1-32 chars: letters, digits, '.', '_' or '-' (start with a letter/digit).");
        }
        return trimmed;
    }

    /// <summary>
    /// Delete <paramref name="path"/> only when it is strictly inside
    /// <paramref name="parent"/>. Guards every destructive lab command.
    /// </summary>
    public static void SafeDelete(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        if (full == root || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new LabException(
                "REFUSING_TO_DELETE",
                $"refusing to delete '{full}' because it is not inside '{root}'.");
        }
        if (Directory.Exists(full)) Directory.Delete(full, true);
        else if (File.Exists(full)) File.Delete(full);
    }

    public static bool IsInside(string path, string parent)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static int? ParseInt(string? value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!int.TryParse(value, out var parsed))
            throw new LabException("INVALID_USAGE", $"--{optionName} expects an integer, got '{value}'.");
        return parsed;
    }
}

/// <summary>
/// Mirrors pkg/deviceid: the same domain-separated component hashing, the same
/// composite digest, and the same kgid:v1 format. That matters because a strict
/// server rejects identifiers that are not hardware-derived, so a lab that used
/// a random UUID would never reach the interesting code paths.
/// </summary>
public static class DeviceIdentity
{
    public const string ComponentDomain = "keygate:device:v1:";
    public const string CompositeDomain = "keygate:device:v1:composite:";

    /// <summary>Same five components (and weights) as pkg/deviceid.</summary>
    public static readonly string[] Components = { "board", "cpu", "disk", "nic", "os" };

    public static string CurrentPlatform()
    {
        if (OperatingSystem.IsMacOS()) return "darwin";
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsIOS()) return "ios";
        return "other";
    }

    /// <summary>The schemes a strict server accepts for a new binding.</summary>
    public static bool IsHardwareScheme(string identifier)
    {
        var value = identifier.Trim();
        if (value.StartsWith("kgid:v1:", StringComparison.Ordinal))
        {
            var parts = value.Split(':');
            return parts.Length == 4 &&
                   parts[1] == "v1" &&
                   IsPlatform(parts[2]) &&
                   IsHex64(parts[3]);
        }

        return value.StartsWith("sha256:", StringComparison.Ordinal) && IsHex64(value["sha256:".Length..]);
    }

    public static bool IsPlatform(string value) =>
        value is "darwin" or "windows" or "linux" or "android" or "ios" or "browser" or "other";

    private static bool IsHex64(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    /// <summary>
    /// Simulated hardware characteristics. A real client reads serials and
    /// MACs; the lab derives five stable values from machineId so rotating
    /// machineId is exactly a hardware change.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ComponentHashes(string machineId)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in Components)
            hashes[component] = HashComponent(component, $"lab-{component}:{machineId}");
        return hashes;
    }

    public static string HashComponent(string componentType, string rawValue)
    {
        var normalized = Normalize(rawValue);
        if (normalized.Length == 0) return "";

        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(ComponentDomain + componentType + ":" + normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CompositeDigest(string platform, IReadOnlyDictionary<string, string> componentHashes)
    {
        var keys = componentHashes
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key.Trim().ToLowerInvariant())
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0)
            throw new LabException("STATE_CORRUPT", "cannot build an identifier: no hardware components.");

        var builder = new StringBuilder();
        builder.Append(CompositeDomain);
        builder.Append(NormalizePlatform(platform));
        builder.Append(':');
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0) builder.Append(';');
            builder.Append(keys[i]).Append('=').Append(componentHashes[keys[i]].Trim().ToLowerInvariant());
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    public static string FormatKgid(string platform, string digest) =>
        $"kgid:v1:{NormalizePlatform(platform)}:{digest.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Builds the identifier a real client would send for the given scheme.
    /// A uuid identifier is deliberately stable across hardware changes:
    /// that is why strict mode refuses it (it cannot detect a new machine).
    /// </summary>
    public static string Build(string scheme, string platform, string machineId)
    {
        platform = NormalizePlatform(platform);

        return scheme switch
        {
            "kgid" => FormatKgid(platform, CompositeDigest(platform, ComponentHashes(machineId))),
            "sha256" => "sha256:" + CompositeDigest(platform, ComponentHashes(machineId)),
            "uuid" => "uuid:" + Guid.NewGuid().ToString("D"),
            _ => throw new LabException(
                "INVALID_USAGE",
                "--scheme must be one of: kgid (default), sha256, uuid."),
        };
    }

    public static string NormalizeScheme(string? scheme) =>
        (scheme ?? "kgid").Trim().ToLowerInvariant() switch
        {
            "kgid" or "kgid:v1" or "kgid-v1" => "kgid",
            "sha256" or "sha256:v1" or "sha256-v1" => "sha256",
            "uuid" or "opaque" => "uuid",
            var other => throw new LabException(
                "INVALID_USAGE", $"--scheme must be one of: kgid, sha256, uuid (got '{other}')."),
        };

    public static string NormalizePlatform(string platform)
    {
        var value = (platform ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return CurrentPlatform();
        if (!IsPlatform(value))
            throw new LabException(
                "INVALID_USAGE",
                "--platform must be one of: darwin, windows, linux, android, ios, browser, other.");
        return value;
    }

    private static string Normalize(string raw) =>
        new(raw.Trim()
            .Where(c => !char.IsControl(c))
            .Select(char.ToLowerInvariant)
            .ToArray());
}
