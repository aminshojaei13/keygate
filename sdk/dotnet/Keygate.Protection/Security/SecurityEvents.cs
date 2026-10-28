using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Keygate.Protection.Security
{
    /// <summary>
    /// Accepted security-event types. The list is closed server-side too, so
    /// a typo is rejected instead of silently logging nothing.
    /// </summary>
    public static class SecurityEventTypes
    {
        public const string IntegrityViolation = "integrity_violation";
        public const string ManifestInvalid = "manifest_invalid";
        public const string DeviceMismatch = "device_mismatch";
        public const string DeviceIdentifierChanged = "device_identifier_changed";
        public const string LicenseTokenInvalid = "license_token_invalid";
        public const string ActivationReplay = "activation_replay";
        public const string UnexpectedEnvironment = "unexpected_environment";
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = new[]
        {
            IntegrityViolation, ManifestInvalid, DeviceMismatch, DeviceIdentifierChanged,
            LicenseTokenInvalid, ActivationReplay, UnexpectedEnvironment, Other
        };

        public static bool IsKnown(string? value) =>
            value != null && All.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>
    /// A tamper/security signal to report. The SDK fills in the event id,
    /// timestamp and device signature; the caller supplies the type, a
    /// severity hint and small, non-secret details.
    /// </summary>
    public sealed class SecurityEvent
    {
        /// <summary>
        /// Optional client-generated id. Must be 1-128 characters from
        /// [A-Za-z0-9._-]. When omitted, a GUID is generated. Reusing an id
        /// makes the report idempotent: the server ignores the duplicate.
        /// </summary>
        public string? EventId { get; set; }

        /// <summary>One of <see cref="SecurityEventTypes"/>.</summary>
        public string Type { get; set; } = SecurityEventTypes.Other;

        /// <summary>low, medium, high or critical. Unknown values become medium.</summary>
        public string Severity { get; set; } = "medium";

        /// <summary>
        /// When the event happened. Defaults to now. The server accepts at
        /// most ten minutes in the future and seven days in the past.
        /// </summary>
        public DateTimeOffset? OccurredAt { get; set; }

        /// <summary>
        /// Small telemetry map. Keys are lowercased and must match
        /// <c>[a-z][a-z0-9_.-]{0,63}</c>; values are capped at 512 bytes.
        /// Token-like keys are dropped, never sent.
        /// </summary>
        public IDictionary<string, string> Details { get; set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Builds and signs the canonical security-event payload shared with the
    /// Go server (pkg/securityevent). The signed string is:
    ///
    /// <code>
    /// keygate-security-event-v1\n
    /// &lt;event_id&gt;\n
    /// &lt;license_id&gt;\n
    /// &lt;device_id&gt;\n
    /// &lt;event_type&gt;\n
    /// &lt;occurred_at_unix_seconds&gt;
    /// </code>
    ///
    /// The license key, token and details are deliberately excluded: the
    /// first two are secrets that must not be fed to a signing routine, and
    /// details are telemetry that cannot justify a policy action alone.
    /// </summary>
    public static class SecurityEventSigner
    {
        public const string CanonicalVersion = "keygate-security-event-v1";
        public const int MaxDetailsEntries = 16;
        public const int MaxDetailKeyLength = 64;
        public const int MaxDetailValueBytes = 512;
        public const int MaxEventIdLength = 128;

        private static readonly Regex Reserved = new Regex(
            "(token|license[_-]?key|private[_-]?key|secret|password|authorization|signature|cookie)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex DetailKey = new Regex(
            "^[a-z][a-z0-9_.-]{0,63}$",
            RegexOptions.CultureInvariant);

        public static string CanonicalString(
            string eventId, string licenseId, string deviceId, string eventType, long occurredAtUnix)
        {
            return CanonicalVersion + "\n" +
                   eventId + "\n" +
                   licenseId + "\n" +
                   deviceId + "\n" +
                   eventType + "\n" +
                   occurredAtUnix.ToString(CultureInfo.InvariantCulture);
        }

        public static byte[] CanonicalBytes(
            string eventId, string licenseId, string deviceId, string eventType, long occurredAtUnix)
        {
            return Encoding.UTF8.GetBytes(
                CanonicalString(eventId, licenseId, deviceId, eventType, occurredAtUnix));
        }

        public static bool IsValidEventId(string? eventId)
        {
            if (string.IsNullOrEmpty(eventId) || eventId!.Length > MaxEventIdLength) return false;
            foreach (char c in eventId)
            {
                if (!((c >= '0' && c <= '9') ||
                      (c >= 'a' && c <= 'z') ||
                      (c >= 'A' && c <= 'Z') ||
                      c == '-' || c == '_' || c == '.'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Copies caller details into a bounded flat map. Nothing throws: a
        /// malformed extra field costs that field, not the report. Reserved
        /// key names are dropped so a token cannot be smuggled into the
        /// database or the audit trail by accident.
        /// </summary>
        public static Dictionary<string, string> SanitizeDetails(
            IDictionary<string, string>? input)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (input == null || input.Count == 0) return result;

            IEnumerable<KeyValuePair<string, string>> ordered = input
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(MaxDetailsEntries);

            foreach (var pair in ordered)
            {
                string key = (pair.Key ?? "").Trim().ToLowerInvariant();
                if (key.Length == 0 || key.Length > MaxDetailKeyLength) continue;
                if (!DetailKey.IsMatch(key)) continue;
                if (Reserved.IsMatch(key)) continue;

                string value = (pair.Value ?? "").Trim();
                if (value.Length > MaxDetailValueBytes)
                    value = value.Substring(0, MaxDetailValueBytes);
                value = value.Replace("\0", "")
                    .Replace("\r", " ")
                    .Replace("\n", " ");
                result[key] = value;
            }
            return result;
        }
    }

    internal static class SecurityEventFactory
    {
        public static string NewEventId() => Guid.NewGuid().ToString("N");
    }
}
