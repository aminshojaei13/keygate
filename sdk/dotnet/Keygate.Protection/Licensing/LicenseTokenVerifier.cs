using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keygate.Protection.Device;
using Keygate.Protection.Internal;

namespace Keygate.Protection.Licensing
{
    internal sealed class LicenseTokenPayload
    {
        [JsonPropertyName("ver")] public string? Version { get; set; }
        [JsonPropertyName("kid")] public string? KeyId { get; set; }
        [JsonPropertyName("lid")] public string LicenseId { get; set; } = "";
        [JsonPropertyName("pid")] public string ProductId { get; set; } = "";
        [JsonPropertyName("pln")] public string PlanId { get; set; } = "";
        [JsonPropertyName("sts")] public string Status { get; set; } = "";
        [JsonPropertyName("did")] public string DeviceId { get; set; } = "";
        [JsonPropertyName("ftr")] public Dictionary<string, JsonElement>? Features { get; set; }
        [JsonPropertyName("iat")] public long IssuedAt { get; set; }
        [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
        [JsonPropertyName("vun")] public long ValidUntil { get; set; }
        [JsonPropertyName("upd")] public long UpdatesUntil { get; set; }
        [JsonPropertyName("grc")] public int GraceDays { get; set; }
        [JsonPropertyName("nce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("fpr")] public string Fingerprint { get; set; } = "";
    }

    /// <summary>
    /// Verifies the server's offline token:
    /// <c>base64url(payload_json) "." base64url(signature)</c>.
    ///
    /// The Ed25519 signature covers the ASCII bytes of the base64url payload
    /// half, not the decoded JSON. That lets a client verify without
    /// re-serializing the payload, which would be a canonical-JSON problem.
    /// </summary>
    internal sealed class LicenseTokenVerifier
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly byte[] _pinnedKey;
        private readonly string? _pinnedKeyId;
        private readonly Dictionary<string, byte[]> _keyring;

        public LicenseTokenVerifier(
            string? licensePublicKey,
            string? licensePublicKeyId,
            IDictionary<string, string>? additionalKeys)
        {
            _keyring = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (additionalKeys != null)
            {
                foreach (var pair in additionalKeys)
                {
                    if (Ed25519.TryParsePublicKey(pair.Value, out byte[] key))
                        _keyring[pair.Key] = key;
                }
            }

            _pinnedKey = Array.Empty<byte>();
            if (!string.IsNullOrWhiteSpace(licensePublicKey))
            {
                if (!Ed25519.TryParsePublicKey(licensePublicKey!, out byte[] key))
                    throw new KeygateConfigurationException(
                        "LicensePublicKey is not a 32-byte Ed25519 key (hex or base64).");
                _pinnedKey = key;
            }
            _pinnedKeyId = string.IsNullOrWhiteSpace(licensePublicKeyId) ? null : licensePublicKeyId;
        }

        public bool HasKeys
        {
            get
            {
                if (_pinnedKey.Length == 32) return true;
#if NETSTANDARD2_0
                return _keyring.Count > 0;
#else
                return _keyring.Count > 0;
#endif
            }
        }

        public LicenseSnapshot Verify(
            string rawToken,
            string? expectedIdentifier,
            string? expectedProductId,
            DateTimeOffset now,
            TimeSpan clockSkew,
            bool fromCache)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
                throw new KeygateLicenseException("TOKEN_EMPTY", "The license token is empty.");

            string token = rawToken.Trim();
            int dot = token.LastIndexOf('.');
            if (dot <= 0 || dot == token.Length - 1 || token.IndexOf('.') != dot)
                throw new KeygateLicenseException(
                    "TOKEN_FORMAT", "The license token is not in payload.signature form.");

            string payloadB64 = token.Substring(0, dot);
            string signatureB64 = token.Substring(dot + 1);

            byte[] signature;
            try { signature = Base64Url.Decode(signatureB64); }
            catch (FormatException)
            {
                throw new KeygateLicenseException(
                    "TOKEN_SIGNATURE", "The license token signature is not valid base64url.");
            }
            if (signature.Length != Ed25519.SignatureSize)
                throw new KeygateLicenseException(
                    "TOKEN_SIGNATURE", "The license token signature has the wrong length.");

            byte[] payloadBytes;
            try { payloadBytes = Base64Url.Decode(payloadB64); }
            catch (FormatException)
            {
                throw new KeygateLicenseException(
                    "TOKEN_PAYLOAD", "The license token payload is not valid base64url.");
            }

            LicenseTokenPayload payload;
            try
            {
                payload = JsonSerializer.Deserialize<LicenseTokenPayload>(payloadBytes, JsonOptions)
                          ?? throw new JsonException("empty payload");
            }
            catch (JsonException)
            {
                throw new KeygateLicenseException(
                    "TOKEN_PAYLOAD", "The license token payload is not valid JSON.");
            }

            byte[]? key = ResolveKey(payload.KeyId);
            if (key == null)
                throw new KeygateLicenseException(
                    "TOKEN_UNKNOWN_KEY",
                    "The token was signed by a key this application does not trust.");

            if (!Ed25519.Verify(key, Encoding.ASCII.GetBytes(payloadB64), signature))
                throw new KeygateLicenseException(
                    "TOKEN_SIGNATURE", "The license token signature is invalid.");

            if (!string.IsNullOrEmpty(payload.Version) && payload.Version != "1")
                throw new KeygateLicenseException(
                    "TOKEN_VERSION", "The license token version is not supported.");

            long nowUnix = now.ToUnixTimeSeconds();
            long skew = (long)clockSkew.TotalSeconds;

            if (payload.IssuedAt > 0 && payload.IssuedAt > nowUnix + skew)
                throw new KeygateLicenseException(
                    "TOKEN_ISSUED_IN_FUTURE", "The license token was issued in the future.");

            if (payload.ExpiresAt > 0 && nowUnix - skew > payload.ExpiresAt)
                throw new KeygateLicenseException(
                    "TOKEN_EXPIRED",
                    "The offline license token has expired; connect to the server to refresh it.");

            if (payload.ValidUntil > 0)
            {
                long deadline = payload.ValidUntil + (long)payload.GraceDays * 86400L;
                if (nowUnix - skew > deadline)
                    throw new KeygateLicenseException(
                        "LICENSE_TERM_EXPIRED", "The license term (including grace) has ended.");
            }

            if (!string.IsNullOrEmpty(expectedProductId) &&
                !string.Equals(payload.ProductId, expectedProductId, StringComparison.Ordinal))
            {
                throw new KeygateLicenseException(
                    "PRODUCT_MISMATCH", "This license belongs to a different product.");
            }

            if (!string.IsNullOrEmpty(expectedIdentifier))
            {
                if (!string.IsNullOrEmpty(payload.DeviceId) &&
                    !string.Equals(payload.DeviceId, expectedIdentifier, StringComparison.Ordinal))
                {
                    throw new KeygateLicenseException(
                        "DEVICE_MISMATCH", "This license token was issued to a different device.");
                }

                if (!string.IsNullOrEmpty(payload.Fingerprint))
                {
                    string expected = Fingerprint(expectedIdentifier!, payload.ProductId);
                    if (!string.Equals(payload.Fingerprint, expected, StringComparison.OrdinalIgnoreCase))
                        throw new KeygateLicenseException(
                            "FINGERPRINT_MISMATCH",
                            "This license token's device fingerprint does not match this device.");
                }
            }

            return SnapshotFrom(payload, token, now, fromCache);
        }

        private byte[]? ResolveKey(string? keyId)
        {
            if (_pinnedKey.Length == Ed25519.PublicKeySize)
            {
                if (_pinnedKeyId != null && !string.IsNullOrEmpty(keyId) &&
                    !string.Equals(_pinnedKeyId, keyId, StringComparison.Ordinal))
                {
                    // The pinned key names one key id; a token claiming a
                    // different one is either forged or signed during a
                    // rotation the app was not updated for.
                    return null;
                }
                return _pinnedKey;
            }

            if (!string.IsNullOrEmpty(keyId) && _keyring.TryGetValue(keyId!, out byte[]? key))
                return key;

            if (_keyring.Count == 1)
                return _keyring.Values.First();

            return null;
        }

        private static LicenseSnapshot SnapshotFrom(
            LicenseTokenPayload payload, string rawToken, DateTimeOffset now, bool fromCache)
        {
            var features = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (payload.Features != null)
            {
                foreach (var pair in payload.Features)
                    features[pair.Key] = ToFeatureValue(pair.Value);
            }

            return new LicenseSnapshot
            {
                Token = rawToken,
                Version = string.IsNullOrEmpty(payload.Version) ? "1" : payload.Version!,
                KeyId = payload.KeyId ?? "",
                LicenseId = payload.LicenseId,
                ProductId = payload.ProductId,
                PlanId = payload.PlanId,
                Status = payload.Status,
                DeviceId = payload.DeviceId,
                Features = features,
                IssuedAt = payload.IssuedAt,
                ExpiresAt = payload.ExpiresAt,
                ValidUntil = payload.ValidUntil,
                UpdatesUntil = payload.UpdatesUntil,
                GraceDays = payload.GraceDays,
                Nonce = payload.Nonce,
                Fingerprint = payload.Fingerprint,
                VerifiedAt = now,
                FromCache = fromCache
            };
        }

        private static object? ToFeatureValue(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined: return null;
                case JsonValueKind.Number:
                    return element.TryGetInt64(out long l) ? l : (object)element.GetDouble();
                case JsonValueKind.String: return element.GetString();
                default: return element.GetRawText();
            }
        }

        /// <summary>
        /// SHA256(identifier + ":" + product_id) truncated to eight bytes,
        /// hex encoded. Same binding value the server puts in <c>fpr</c>.
        /// </summary>
        public static string Fingerprint(string identifier, string productId)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(identifier + ":" + productId));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(digest[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Feature truthiness matches the server's entitlement check: a
        /// present, non-null, non-false value grants the feature. Numbers
        /// other than zero, non-empty strings and objects grant it too.
        /// </summary>
        public static bool FeatureIsEnabled(object? value)
        {
            if (value == null) return false;
            if (value is bool b) return b;
            if (value is string s)
            {
                if (s.Length == 0) return false;
                return !s.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                       !s.Equals("0", StringComparison.Ordinal) &&
                       !s.Equals("no", StringComparison.OrdinalIgnoreCase);
            }
            if (value is long l) return l != 0;
            if (value is int i) return i != 0;
            if (value is double d) return Math.Abs(d) > double.Epsilon;
            return true;
        }
    }
}
