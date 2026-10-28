using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keygate.Protection.Internal;

namespace Keygate.Protection.Integrity
{
    internal sealed class SignedIntegrityEnvelope
    {
        [JsonPropertyName("format")] public string Format { get; set; } = "";
        [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
        [JsonPropertyName("payload")] public string Payload { get; set; } = "";
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("signing_key_id")] public string SigningKeyId { get; set; } = "";
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
        [JsonPropertyName("manifest")] public IntegrityManifest? Manifest { get; set; }
    }

    internal sealed class IntegrityManifest
    {
        [JsonPropertyName("manifest_version")] public int ManifestVersion { get; set; }
        [JsonPropertyName("product_id")] public string ProductId { get; set; } = "";
        [JsonPropertyName("release_id")] public string? ReleaseId { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("platform")] public string? Platform { get; set; }
        [JsonPropertyName("arch")] public string? Arch { get; set; }
        [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
        [JsonPropertyName("signing_key_id")] public string SigningKeyId { get; set; } = "";
        [JsonPropertyName("metadata")] public Dictionary<string, string>? Metadata { get; set; }
        [JsonPropertyName("files")] public List<IntegrityFileEntry> Files { get; set; } =
            new List<IntegrityFileEntry>();
    }

    internal sealed class IntegrityFileEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>
    /// Verifies a Keygate signed integrity manifest and checks installed
    /// files against it. The manifest format mirrors pkg/integrity on the
    /// server:
    ///
    /// <code>
    /// { "format": "keygate-integrity-v1", "manifest_version": 1,
    ///   "payload": base64url(canonical manifest JSON),
    ///   "signature": base64url(ed25519 over the payload TEXT),
    ///   "signing_key_id": "...", "algorithm": "ed25519" }
    /// </code>
    ///
    /// Signature failures and file mismatches are reported as results, not
    /// exceptions, so the caller can report them without a crash. IO errors
    /// (permissions, locked files) go to <see cref="IntegrityCheckResult.Errors"/>
    /// and must not be treated as tampering.
    /// </summary>
    internal sealed class IntegrityVerifier
    {
        public const string FormatIdentifier = "keygate-integrity-v1";
        public const int CurrentManifestVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly Dictionary<string, byte[]> _trustedKeys;

        public IntegrityVerifier(IDictionary<string, string> trustedKeys)
        {
            _trustedKeys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var pair in trustedKeys)
            {
                if (Ed25519.TryParsePublicKey(pair.Value, out byte[] key))
                    _trustedKeys[pair.Key] = key;
            }
        }

        public bool HasTrustedKeys => _trustedKeys.Count > 0;

        public async System.Threading.Tasks.Task<IntegrityCheckResult> CheckAsync(
            string? manifestPath,
            Uri? manifestUri,
            string installDirectory,
            KeygateApi? api,
            System.Threading.CancellationToken cancellationToken)
        {
            SignedIntegrityEnvelope? envelope;
            try
            {
                envelope = await LoadEnvelopeAsync(manifestPath, manifestUri, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new IntegrityCheckResult
                {
                    IsValid = false,
                    ManifestError = "The integrity manifest could not be read: " + ex.Message
                };
            }
            catch (JsonException)
            {
                return new IntegrityCheckResult
                {
                    IsValid = false,
                    ManifestError = "The integrity manifest is not valid JSON."
                };
            }

            IntegrityManifest manifest;
            try
            {
                manifest = VerifyEnvelope(envelope);
            }
            catch (KeygateException ex)
            {
                return new IntegrityCheckResult
                {
                    IsValid = false,
                    ManifestError = ex.Message
                };
            }

            return VerifyFiles(manifest, installDirectory);
        }

        internal static async System.Threading.Tasks.Task<SignedIntegrityEnvelope> LoadEnvelopeAsync(
            string? manifestPath, Uri? manifestUri, System.Threading.CancellationToken cancellationToken)
        {
            string json;
            if (!string.IsNullOrWhiteSpace(manifestPath))
            {
                json = File.ReadAllText(manifestPath!);
            }
            else if (manifestUri != null)
            {
                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) })
                {
                    json = await client.GetStringAsync(manifestUri).ConfigureAwait(false);
                }
            }
            else
            {
                throw new KeygateConfigurationException(
                    "No integrity manifest is configured. Set IntegrityManifestPath or IntegrityManifestUri.");
            }

            return JsonSerializer.Deserialize<SignedIntegrityEnvelope>(json, JsonOptions)
                   ?? throw new KeygateProtocolException("The integrity manifest is empty.");
        }

        public IntegrityManifest VerifyEnvelope(SignedIntegrityEnvelope envelope)
        {
            if (envelope == null) throw new KeygateProtocolException("The integrity manifest is empty.");
            if (!string.Equals(envelope.Format, FormatIdentifier, StringComparison.Ordinal))
                throw new KeygateProtocolException(
                    "Unsupported manifest format identifier: " + envelope.Format);
            if (envelope.ManifestVersion != CurrentManifestVersion)
                throw new KeygateProtocolException("Unsupported manifest version: " + envelope.ManifestVersion);
            if (!string.Equals(envelope.Algorithm, "ed25519", StringComparison.Ordinal))
                throw new KeygateProtocolException(
                    "Unsupported manifest signature algorithm: " + envelope.Algorithm);
            if (string.IsNullOrEmpty(envelope.SigningKeyId))
                throw new KeygateProtocolException("The manifest has no signing_key_id.");
            if (!_trustedKeys.TryGetValue(envelope.SigningKeyId, out byte[]? key))
                throw new KeygateProtocolException(
                    "The manifest was signed by an unknown key id: " + envelope.SigningKeyId);
            if (!Base64Url.TryDecodeFlexible(envelope.Signature, out byte[] signature) ||
                signature.Length != Ed25519.SignatureSize)
                throw new KeygateProtocolException("The manifest signature is malformed.");

            // The signed region is the payload TEXT, not the decoded JSON.
            if (!Ed25519.Verify(key, System.Text.Encoding.UTF8.GetBytes(envelope.Payload), signature))
                throw new KeygateProtocolException(
                    "The manifest signature is invalid; the manifest is untrusted.");

            if (!Base64Url.TryDecodeFlexible(envelope.Payload, out byte[] payloadBytes))
                throw new KeygateProtocolException("The manifest payload is not valid base64url.");

            IntegrityManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<IntegrityManifest>(payloadBytes, JsonOptions)
                           ?? throw new KeygateProtocolException("The manifest payload is empty.");
            }
            catch (JsonException)
            {
                throw new KeygateProtocolException("The manifest payload is not valid JSON.");
            }

            if (manifest.ManifestVersion != CurrentManifestVersion)
                throw new KeygateProtocolException(
                    "Unsupported manifest payload version: " + manifest.ManifestVersion);
            if (!string.Equals(manifest.SigningKeyId, envelope.SigningKeyId, StringComparison.Ordinal))
                throw new KeygateProtocolException(
                    "The manifest signing_key_id does not match the envelope.");
            if (manifest.Files == null)
                throw new KeygateProtocolException("The manifest has no files array.");

            foreach (IntegrityFileEntry file in manifest.Files)
            {
                ValidateRelativePath(file.Path);
                if (file.Sha256 == null || file.Sha256.Length != 64 || !IsHex(file.Sha256))
                    throw new KeygateProtocolException(
                        "The manifest has an invalid sha256 digest for " + file.Path + ".");
                if (file.Size < 0)
                    throw new KeygateProtocolException(
                        "The manifest has a negative size for " + file.Path + ".");
            }

            return manifest;
        }

        public IntegrityCheckResult VerifyFiles(IntegrityManifest manifest, string installDirectory)
        {
            var result = new IntegrityCheckResult
            {
                ManifestVersion = manifest.ManifestVersion.ToString(),
                SigningKeyId = manifest.SigningKeyId,
                TotalFiles = manifest.Files.Count
            };

            string root;
            try
            {
                root = Path.GetFullPath(installDirectory);
                if (TryResolveDirectory(root, out string? resolved)) root = resolved!;
            }
            catch (Exception ex)
            {
                result.Errors = new[] { "Cannot resolve the install directory: " + ex.Message };
                result.IsValid = false;
                return result;
            }

            var missing = new List<string>();
            var modified = new List<IntegrityFileMismatch>();
            var errors = new List<string>();
            int passed = 0;
            bool windowsPaths = Path.DirectorySeparatorChar == '\\';

            foreach (IntegrityFileEntry entry in manifest.Files)
            {
                try
                {
                    ValidateRelativePath(entry.Path);
                }
                catch (KeygateException ex)
                {
                    errors.Add("Invalid manifest path \"" + entry.Path + "\": " + ex.Message);
                    continue;
                }

                string nativeRel = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(root, nativeRel));
                if (!IsWithin(root, target, windowsPaths))
                {
                    errors.Add("Path traversal blocked for \"" + entry.Path + "\".");
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(target);
                    if (!info.Exists)
                    {
                        missing.Add(entry.Path);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Cannot stat \"" + entry.Path + "\": " + ex.Message);
                    continue;
                }

                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    modified.Add(new IntegrityFileMismatch
                    {
                        Path = entry.Path,
                        ExpectedSha256 = entry.Sha256,
                        ExpectedSize = entry.Size,
                        ActualSize = SafeLength(info),
                        Reason = "path is a symbolic link or reparse point"
                    });
                    continue;
                }
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    modified.Add(new IntegrityFileMismatch
                    {
                        Path = entry.Path,
                        ExpectedSha256 = entry.Sha256,
                        ExpectedSize = entry.Size,
                        Reason = "not a regular file"
                    });
                    continue;
                }

                long length = SafeLength(info);
                if (length != entry.Size)
                {
                    modified.Add(new IntegrityFileMismatch
                    {
                        Path = entry.Path,
                        ExpectedSha256 = entry.Sha256,
                        ExpectedSize = entry.Size,
                        ActualSize = length,
                        Reason = "file size mismatch (expected " + entry.Size + ", got " + length + ")"
                    });
                    continue;
                }

                string actual;
                try
                {
                    actual = HashFile(target);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A locked or unreadable file is not proof of tampering.
                    errors.Add("Cannot hash \"" + entry.Path + "\": " + ex.Message);
                    continue;
                }

                if (!FixedTimeHexEquals(actual, entry.Sha256))
                {
                    modified.Add(new IntegrityFileMismatch
                    {
                        Path = entry.Path,
                        ExpectedSha256 = entry.Sha256.ToLowerInvariant(),
                        ActualSha256 = actual,
                        ExpectedSize = entry.Size,
                        ActualSize = length,
                        Reason = "sha256 digest mismatch (content modified)"
                    });
                    continue;
                }

                passed++;
            }

            result.PassedFiles = passed;
            result.MissingFiles = missing;
            result.ModifiedFiles = modified;
            result.Errors = errors;
            result.IsValid = missing.Count == 0 && modified.Count == 0 && errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Validates the relative-path rules from the Go implementation:
        /// forward slashes only, no absolute paths, no empty or dot segments,
        /// no control characters, and canonical form.
        /// </summary>
        public static void ValidateRelativePath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new KeygateProtocolException("Manifest path cannot be empty.");
            if (relativePath.IndexOf('\\') >= 0)
                throw new KeygateProtocolException("Manifest path must use forward slashes: " + relativePath);
            if (relativePath[0] == '/')
                throw new KeygateProtocolException("Manifest path cannot be absolute: " + relativePath);
            foreach (char c in relativePath)
            {
                if (c < 32 || c == 127)
                    throw new KeygateProtocolException("Manifest path contains a control character.");
            }
            string[] parts = relativePath.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0)
                    throw new KeygateProtocolException(
                        "Manifest path contains an empty segment: " + relativePath);
                if (part == "." || part == "..")
                    throw new KeygateProtocolException(
                        "Manifest path attempts directory traversal: " + relativePath);
            }
            string cleaned = string.Join("/", parts);
            if (cleaned != relativePath)
                throw new KeygateProtocolException("Manifest path is not canonical: " + relativePath);
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
            {
                byte[] digest = sha.ComputeHash(stream);
                return Hex.Encode(digest);
            }
        }

        private static long SafeLength(FileInfo info)
        {
            try { return info.Length; } catch { return 0; }
        }

        private static bool IsWithin(string root, string target, bool windows)
        {
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            StringComparison comparison = windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return target.Equals(root, comparison) || target.StartsWith(prefix, comparison);
        }

        private static bool TryResolveDirectory(string path, out string? resolved)
        {
            resolved = null;
#if NET6_0_OR_GREATER
            try
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    FileSystemInfo? target = info.ResolveLinkTarget(true);
                    if (target != null) resolved = Path.GetFullPath(target.FullName);
                }
            }
            catch { }
#endif
            return resolved != null;
        }

        private static bool IsHex(string value)
        {
            foreach (char c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        private static bool FixedTimeHexEquals(string left, string right)
        {
            if (left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                char a = char.ToLowerInvariant(left[i]);
                char b = char.ToLowerInvariant(right[i]);
                diff |= a ^ b;
            }
            return diff == 0;
        }
    }
}
