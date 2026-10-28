using System;
using System.Collections.Generic;
using Keygate.Protection;

namespace Keygate.Protection.AspNetCore
{
    /// <summary>
    /// ASP.NET Core specific settings layered on top of
    /// <see cref="KeygateOptions"/>.
    /// </summary>
    public sealed class KeygateAspNetCoreOptions
    {
        /// <summary>The SDK configuration. Always required.</summary>
        public KeygateOptions Protection { get; set; } = new KeygateOptions();

        /// <summary>
        /// Paths that never require a valid license. Defaults include the
        /// health and readiness probes so orchestration keeps working while
        /// a license is being fixed.
        /// </summary>
        public IList<string> ExcludedPaths { get; } = new List<string>
        {
            "/health", "/healthz", "/ready", "/readyz", "/livez"
        };

        /// <summary>
        /// When the license is invalid, answer with HTTP 402 and a small JSON
        /// body instead of running the request. Turn this off when the API is
        /// mostly public and only some endpoints are licensed (use
        /// <see cref="RequireKeygateFeatureAttribute"/> there instead).
        /// </summary>
        public bool BlockOnInvalidLicense { get; set; } = true;

        /// <summary>
        /// Add the <c>X-Powered-By: Keygate</c> attribution header. This is an
        /// AGPL v3 Section 7(b) requirement of the Keygate license; leave it
        /// on for any deployed instance.
        /// </summary>
        public bool EmitAttributionHeader { get; set; } = true;

        /// <summary>
        /// Run a background revalidation on this interval. Null disables the
        /// hosted timer (startup validation still runs).
        /// </summary>
        public TimeSpan? PeriodicValidationInterval { get; set; } = TimeSpan.FromHours(6);
    }
}
