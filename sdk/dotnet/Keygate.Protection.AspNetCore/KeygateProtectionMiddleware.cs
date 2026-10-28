using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Keygate.Protection.AspNetCore
{
    /// <summary>
    /// Validates the license for every request and refuses licensed routes
    /// when it is not usable. The per-request cost is a cached snapshot check;
    /// the network call is amortised by
    /// <see cref="KeygateOptions.ValidationCacheDuration"/>.
    /// </summary>
    public sealed class KeygateProtectionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly KeygateProtection _protection;
        private readonly KeygateAspNetCoreOptions _options;
        private readonly ILogger<KeygateProtectionMiddleware> _logger;

        public KeygateProtectionMiddleware(
            RequestDelegate next,
            KeygateProtection protection,
            IOptions<KeygateAspNetCoreOptions> options,
            ILogger<KeygateProtectionMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _protection = protection ?? throw new ArgumentNullException(nameof(protection));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (_options.EmitAttributionHeader)
            {
                // AGPL v3 Section 7(b) attribution. Do not remove.
                context.Response.Headers[KeygateAttribution.HeaderName] =
                    KeygateAttribution.HeaderValue;
            }

            if (!_options.BlockOnInvalidLicense || IsExcluded(context.Request.Path))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            LicenseValidation validation;
            try
            {
                validation = await _protection.ValidateLicenseAsync().ConfigureAwait(false);
            }
            catch (KeygateException ex)
            {
                _logger.LogError(ex, "Keygate license validation failed.");
                await WriteProblemAsync(
                    context, StatusCodes.Status503ServiceUnavailable,
                    "LICENSE_CHECK_FAILED",
                    "The license could not be checked.").ConfigureAwait(false);
                return;
            }

            if (!validation.IsValid)
            {
                _logger.LogWarning(
                    "Refusing request to {Path}: {Code} {Problem}",
                    context.Request.Path, validation.ProblemCode, validation.Problem);
                await WriteProblemAsync(
                    context, StatusCodes.Status402PaymentRequired,
                    validation.ProblemCode ?? "LICENSE_INVALID",
                    validation.Problem ?? "The license is not valid.").ConfigureAwait(false);
                return;
            }

            context.Items["Keygate.License"] = validation.License;
            context.Items["Keygate.Validation"] = validation;
            await _next(context).ConfigureAwait(false);
        }

        private bool IsExcluded(PathString path)
        {
            string value = path.Value ?? string.Empty;
            foreach (string excluded in _options.ExcludedPaths)
            {
                if (value.StartsWith(excluded, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static async Task WriteProblemAsync(
            HttpContext context, int status, string code, string message)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            string body = JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code, message }
            });
            await context.Response.WriteAsync(body).ConfigureAwait(false);
        }
    }
}
