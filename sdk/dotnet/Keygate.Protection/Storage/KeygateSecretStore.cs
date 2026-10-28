using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Keygate.Protection.Storage
{
    /// <summary>
    /// Pluggable persistence for the SDK's small secret state: the device's
    /// Ed25519 seed, the license key (when persisted) and the cached signed
    /// token. Implement this to keep the material in an OS keychain, a
    /// credential manager, or your own encrypted store.
    /// </summary>
    public interface IKeygateSecretStore
    {
        Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default);
        Task WriteAsync(string name, string value, CancellationToken cancellationToken = default);
        Task DeleteAsync(string name, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Default file-backed store.
    ///
    /// On Windows the blob is encrypted with DPAPI for the current user, so
    /// another account on the same machine cannot read it. On macOS and Linux
    /// there is no equivalent dependency-free per-user secret service, so the
    /// file is written with mode 0600 and permissions 0700 on its directory.
    /// That protects against other users and casual inspection, not against
    /// an attacker who already runs code as this user. Supply your own
    /// <see cref="IKeygateSecretStore"/> for stronger isolation.
    /// </summary>
    public sealed class FileSecretStore : IKeygateSecretStore
    {
        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("keygate.protection.v1");
        private const string DpapiPrefix = "dpapi:";

        public string DirectoryPath { get; }
        public string FileExtension { get; }
        public bool UseDpapiOnWindows { get; }

        public FileSecretStore(
            string? directory = null,
            string fileExtension = ".json",
            bool useDpapiOnWindows = true)
        {
            DirectoryPath = string.IsNullOrWhiteSpace(directory)
                ? DefaultDirectory()
                : Path.GetFullPath(directory!);
            FileExtension = fileExtension;
            UseDpapiOnWindows = useDpapiOnWindows;
        }

        public static string DefaultDirectory()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(baseDir, "Keygate");
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", "Keygate");
            }
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
            if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg, "keygate");
            string home2 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home2, ".local", "share", "keygate");
        }

        public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathFor(name);
            if (!File.Exists(path)) return Task.FromResult<string?>(null);

            byte[] raw = File.ReadAllBytes(path);
            string text = Encoding.UTF8.GetString(raw);
            if (text.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    throw new KeygateException(
                        "The local state was encrypted with Windows DPAPI and cannot be read on this platform.");
                byte[] cipher = Convert.FromBase64String(text.Substring(DpapiPrefix.Length));
                byte[] plain = ProtectedData.Unprotect(cipher, DpapiEntropy, DataProtectionScope.CurrentUser);
                return Task.FromResult<string?>(Encoding.UTF8.GetString(plain));
            }
            return Task.FromResult<string?>(text);
        }

        public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(DirectoryPath);
            RestrictDirectory(DirectoryPath);

            string payload = value ?? "";
            if (UseDpapiOnWindows && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                byte[] cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(payload), DpapiEntropy, DataProtectionScope.CurrentUser);
                payload = DpapiPrefix + Convert.ToBase64String(cipher);
            }

            string path = PathFor(name);
            string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            RestrictFile(tmp);

            // File.Replace needs an existing destination, which the first
            // run does not have. Deleting first leaves a tiny window where
            // the state is absent; a crash there means re-activation, not
            // corruption.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            RestrictFile(path);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        private string PathFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("State name is required.", nameof(name));
            foreach (char c in name)
            {
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.'))
                    throw new ArgumentException("State name contains invalid characters.", nameof(name));
            }
            return Path.Combine(DirectoryPath, name + FileExtension);
        }

        private static void RestrictDirectory(string path)
        {
            if (Path.DirectorySeparatorChar == '/')
            {
                try { chmod(path, Convert.ToUInt32("700", 8)); } catch { }
            }
        }

        private static void RestrictFile(string path)
        {
            if (Path.DirectorySeparatorChar == '/')
            {
                try { chmod(path, Convert.ToUInt32("600", 8)); } catch { }
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int chmod(string path, uint mode);
    }
}
