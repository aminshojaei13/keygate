using System;
using System.Collections.Generic;
using System.Net;

namespace Keygate.Protection
{
    /// <summary>Base class for every error raised by the SDK.</summary>
    public class KeygateException : Exception
    {
        public KeygateException(string message) : base(message) { }
        public KeygateException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// The SDK was constructed or used with invalid configuration. This is a
    /// programming/deployment error and should fail fast at startup.
    /// </summary>
    public sealed class KeygateConfigurationException : KeygateException
    {
        public KeygateConfigurationException(string message) : base(message) { }
    }

    /// <summary>
    /// A network call did not complete: DNS, TCP, TLS, timeout or a proxy
    /// failure. Crucially, this is NOT evidence of tampering and the SDK
    /// never turns it into a security report.
    /// </summary>
    public sealed class KeygateNetworkException : KeygateException
    {
        public KeygateNetworkException(string message, Exception? inner = null)
            : base(message, inner!) { }
    }

    /// <summary>
    /// The license is not usable: signature failure, expired token, wrong
    /// product, missing feature, suspended/revoked license, and so on.
    /// </summary>
    public sealed class KeygateLicenseException : KeygateException
    {
        public string Code { get; }

        public KeygateLicenseException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// The server answered with a non-success envelope. <see cref="Code"/> is
    /// the machine-readable code the server sent, which is the value an
    /// application should branch on.
    /// </summary>
    public sealed class KeygateApiException : KeygateException
    {
        public HttpStatusCode StatusCode { get; }
        public string Code { get; }

        public KeygateApiException(HttpStatusCode statusCode, string code, string message)
            : base(message)
        {
            StatusCode = statusCode;
            Code = code;
        }
    }

    /// <summary>
    /// A response or manifest did not match the documented wire format.
    /// </summary>
    public sealed class KeygateProtocolException : KeygateException
    {
        public KeygateProtocolException(string message) : base(message) { }
    }

    /// <summary>A security report could not be built or sent.</summary>
    public sealed class KeygateSecurityEventException : KeygateException
    {
        public KeygateSecurityEventException(string message) : base(message) { }
    }

    /// <summary>Result of a successful activation call.</summary>
    public sealed class ActivationResult
    {
        public string Status { get; set; } = "";
        public string LicenseId { get; set; } = "";
        public string? ProductId { get; set; }
        public string DeviceId { get; set; } = "";
        public string Token { get; set; } = "";
        public LicenseSnapshot? License { get; set; }
    }

    /// <summary>
    /// A verified license token. Every property comes from the signed
    /// payload; <see cref="RawToken"/> is the token that was verified.
    /// </summary>
    public sealed class LicenseSnapshot
    {
        public string Token { get; set; } = "";
        public string Version { get; set; } = "1";
        public string KeyId { get; set; } = "";
        public string LicenseId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string Status { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public IReadOnlyDictionary<string, object?> Features { get; set; } =
            new Dictionary<string, object?>(StringComparer.Ordinal);
        public long IssuedAt { get; set; }
        public long ExpiresAt { get; set; }
        public long ValidUntil { get; set; }
        public long UpdatesUntil { get; set; }
        public int GraceDays { get; set; }
        public string Nonce { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public DateTimeOffset? VerifiedAt { get; set; }
        public bool FromCache { get; set; }

        /// <summary>
        /// True while the check-in token and the license term (plus grace)
        /// are both still in the future.
        /// </summary>
        public bool IsUsableAt(DateTimeOffset moment)
        {
            long now = moment.ToUnixTimeSeconds();
            if (ExpiresAt > 0 && now > ExpiresAt) return false;
            if (ValidUntil > 0 && now > ValidUntil + (long)GraceDays * 86400L) return false;
            return true;
        }

        /// <summary>
        /// True when the release's publication date is inside the paid
        /// maintenance window. Use it to gate auto-updates; the license
        /// itself may remain valid for older releases.
        /// </summary>
        public bool AllowsUpdatePublishedAt(DateTimeOffset publishedAt)
        {
            if (UpdatesUntil <= 0) return true;
            return publishedAt.ToUnixTimeSeconds() <= UpdatesUntil;
        }
    }

    /// <summary>Outcome of an online or offline license validation.</summary>
    public sealed class LicenseValidation
    {
        public bool IsValid { get; set; }
        public LicenseSnapshot? License { get; set; }
        public bool IsOffline { get; set; }
        public bool NetworkError { get; set; }
        public string? ProblemCode { get; set; }
        public string? Problem { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;

        public static LicenseValidation Invalid(string code, string message) =>
            new LicenseValidation { IsValid = false, ProblemCode = code, Problem = message };
    }

    public sealed class IntegrityFileMismatch
    {
        public string Path { get; set; } = "";
        public string ExpectedSha256 { get; set; } = "";
        public string ActualSha256 { get; set; } = "";
        public long ExpectedSize { get; set; }
        public long ActualSize { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>Result of a signed-manifest file integrity check.</summary>
    public sealed class IntegrityCheckResult
    {
        public bool IsValid { get; set; }
        public int TotalFiles { get; set; }
        public int PassedFiles { get; set; }
        public IReadOnlyList<string> MissingFiles { get; set; } = new List<string>();
        public IReadOnlyList<IntegrityFileMismatch> ModifiedFiles { get; set; } =
            new List<IntegrityFileMismatch>();
        public IReadOnlyList<string> Errors { get; set; } = new List<string>();
        public string ManifestVersion { get; set; } = "";
        public string SigningKeyId { get; set; } = "";
        /// <summary>
        /// Set when the manifest itself could not be trusted (bad signature,
        /// unsupported format, unreadable file). File results are empty in
        /// that case.
        /// </summary>
        public string? ManifestError { get; set; }
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Outcome of a security-event report.</summary>
    public sealed class SecurityReportResult
    {
        public string EventId { get; set; } = "";
        public bool Accepted { get; set; }
        public bool Duplicate { get; set; }
        public bool Verified { get; set; }
        public string? ActionTaken { get; set; }
        public string? LicenseStatus { get; set; }
        public bool UnderReview { get; set; }
        public bool IsAllowed { get; set; } = true;
        public int RetryAfter { get; set; }
    }
}
