using System;
using System.Collections.Generic;
using System.Net.Http;
using Keygate.Protection.Device;
using Keygate.Protection.Storage;

namespace Keygate.Protection
{
    /// <summary>
    /// Configuration for <see cref="KeygateProtection"/>.
    /// </summary>
    public sealed class KeygateOptions
    {
        /// <summary>
        /// Base URL of the Keygate server, for example
        /// <c>https://licenses.example.com</c>. Plain HTTP is accepted only
        /// for loopback hosts unless <see cref="AllowInsecureHttp"/> is set.
        /// </summary>
        public string BaseUrl { get; set; } = "http://localhost:9000";

        /// <summary>
        /// The customer's license key. Optional at construction: it can be
        /// supplied later through <see cref="KeygateProtection.ActivateAsync"/>
        /// or read from the local state written by a previous run.
        /// </summary>
        public string? LicenseKey { get; set; }

        /// <summary>
        /// Expected product ID. When set, a token issued for another product
        /// is rejected even if its signature is valid. Strongly recommended.
        /// </summary>
        public string? ProductId { get; set; }

        /// <summary>
        /// Overrides the derived device identifier. Only use this when the
        /// host application already has a stable, privacy-preserving device
        /// ID in the <c>kgid:v1:&lt;platform&gt;:&lt;64-hex&gt;</c> format.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Human-readable label shown in the Keygate dashboard when the
        /// device activates. Defaults to "C# SDK". Do not put personal data
        /// (a person's name, an email address) here.
        /// </summary>
        public string? DeviceLabel { get; set; }

        /// <summary>
        /// Directory for the local state file (device key, cached token,
        /// license key when <see cref="PersistLicenseKey"/> is true).
        /// Defaults to the per-user application-data directory.
        /// </summary>
        public string? StorageDirectory { get; set; }

        /// <summary>
        /// Replace the default file-backed secret store. Useful for
        /// applications that already have a keychain/credential manager.
        /// </summary>
        public IKeygateSecretStore? SecretStore { get; set; }

        /// <summary>
        /// Persist the license key locally so the app can reactivate and
        /// revalidate without asking the customer again. Default true. Set
        /// false when the host app supplies the key from its own storage.
        /// </summary>
        public bool PersistLicenseKey { get; set; } = true;

        /// <summary>
        /// Adds the machine hostname to the device fingerprint. Off by
        /// default: a hostname is weak entropy and changes more often than
        /// hardware identifiers. Hardware and OS components are used first.
        /// </summary>
        public bool IncludeHostnameInFingerprint { get; set; }

        /// <summary>
        /// Pinned Ed25519 license public key, as 64 hex characters or
        /// base64url. Pinning is strongly recommended: it removes the
        /// first-run key-fetch step from the trust path.
        /// </summary>
        public string? LicensePublicKey { get; set; }

        /// <summary>
        /// Key id that goes with <see cref="LicensePublicKey"/>. Optional;
        /// when omitted the token's <c>kid</c> is not cross-checked against a
        /// pinned single key (the signature still has to verify).
        /// </summary>
        public string? LicensePublicKeyId { get; set; }

        /// <summary>
        /// Additional trusted license keys, keyed by <c>kid</c>. Use this
        /// during a signing-key rotation: old and new keys can both verify.
        /// </summary>
        public IDictionary<string, string> LicensePublicKeys { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>HTTP timeout for every Keygate call.</summary>
        public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Clock skew tolerated when checking <c>iat</c>, <c>exp</c> and the
        /// license term. Default five minutes.
        /// </summary>
        public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How long a successful online validation is reused before another
        /// network call is made. Zero means "always call the server".
        /// </summary>
        public TimeSpan ValidationCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Optional periodic revalidation interval. Call
        /// <see cref="KeygateProtection.StartPeriodicValidation"/> to arm it.
        /// </summary>
        public TimeSpan PeriodicValidationInterval { get; set; } = TimeSpan.FromHours(6);

        /// <summary>Path to a signed integrity manifest envelope (JSON).</summary>
        public string? IntegrityManifestPath { get; set; }

        /// <summary>Optional URL to fetch a signed integrity manifest from.</summary>
        public Uri? IntegrityManifestUri { get; set; }

        /// <summary>
        /// Directory the manifest paths are relative to. Defaults to the
        /// application base directory.
        /// </summary>
        public string? IntegrityInstallDirectory { get; set; }

        /// <summary>
        /// Trusted release signing keys, keyed by <c>signing_key_id</c>, as
        /// hex or base64. Fetch them once from
        /// <c>GET /api/v1/releases/:product_slug/signing-key</c> and pin them
        /// in the shipped application.
        /// </summary>
        public IDictionary<string, string> ReleasePublicKeys { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Report file mismatches automatically when
        /// <see cref="KeygateProtection.CheckIntegrityAsync"/> finds them.
        /// Signature/format failures are reported as <c>manifest_invalid</c>.
        /// IO errors are never reported: a locked or unreadable file is not
        /// evidence of tampering. Default true.
        /// </summary>
        public bool AutoReportIntegrityFailures { get; set; } = true;

        /// <summary>
        /// Master switch for <see cref="KeygateProtection.ReportSecurityEventAsync"/>.
        /// Default true. Disabling it does not disable the server's policy
        /// engine; it only stops this client from contributing evidence.
        /// </summary>
        public bool SecurityReportingEnabled { get; set; } = true;

        /// <summary>
        /// Features that must be present for a license to count as valid.
        /// Checked after activation and after every validation.
        /// </summary>
        public IList<string> RequiredFeatures { get; } = new List<string>();

        /// <summary>Supply a configured HttpClient (proxy, handler, etc.).</summary>
        public HttpClient? HttpClient { get; set; }

        /// <summary>
        /// Allow plain HTTP to a non-loopback host. Off by default because a
        /// license key is a bearer credential on the wire.
        /// </summary>
        public bool AllowInsecureHttp { get; set; }

        /// <summary>Optional diagnostic sink. Never receives secrets.</summary>
        public Action<string>? Log { get; set; }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(BaseUrl))
                throw new KeygateConfigurationException("BaseUrl is required.");
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new KeygateConfigurationException("BaseUrl must be an absolute http(s) URL.");

            if (uri.Scheme == Uri.UriSchemeHttp && !AllowInsecureHttp && !IsLoopback(uri))
            {
                throw new KeygateConfigurationException(
                    "Refusing to send a license key over plain HTTP to a non-loopback host. " +
                    "Use https, or set AllowInsecureHttp only for a trusted private network.");
            }

            if (HttpTimeout <= TimeSpan.Zero)
                throw new KeygateConfigurationException("HttpTimeout must be positive.");
            if (ClockSkew < TimeSpan.Zero)
                throw new KeygateConfigurationException("ClockSkew must not be negative.");

            if (!string.IsNullOrEmpty(DeviceId))
            {
                if (DeviceId!.Length > 256)
                    throw new KeygateConfigurationException("DeviceId is too long.");
                if (DeviceId.StartsWith("kgid:", StringComparison.Ordinal) &&
                    !DeviceIdFormats.IsValidKgid(DeviceId))
                    throw new KeygateConfigurationException(
                        "DeviceId must match kgid:v1:<platform>:<64-hex>.");
            }
        }

        private static bool IsLoopback(Uri uri)
        {
            if (uri.IsLoopback) return true;
            string host = uri.Host;
            return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   host == "::1" || host == "[::1]";
        }
    }

    /// <summary>
    /// AGPL v3 Section 7(b) attribution values. The "Powered by Keygate"
    /// notice is a license requirement, not a removable feature: keep it in
    /// your about box, footer, login page and any customer-facing surface.
    /// </summary>
    public static class KeygateAttribution
    {
        public const string Text = "Powered by Keygate";
        public const string Url = "https://keygate.app";
        public const string HeaderName = "X-Powered-By";
        public const string HeaderValue = "Keygate";
    }
}
