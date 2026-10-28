using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Keygate.Protection.AspNetCore
{
    /// <summary>
    /// Requires the active license to grant a feature before the action runs.
    /// Use on a controller or a single action:
    ///
    /// <code>
    /// [RequireKeygateFeature("pro")]
    /// public IActionResult Report() => Ok();
    /// </code>
    ///
    /// Answers 402 when the license is missing/expired and 403 when it is
    /// valid but does not include the feature. Controllers only; for minimal
    /// APIs call <c>KeygateProtection.HasFeature</c> in the endpoint.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequireKeygateFeatureAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public RequireKeygateFeatureAttribute(params string[] features)
        {
            Features = features ?? Array.Empty<string>();
        }

        public string[] Features { get; }

        public Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            KeygateProtection? protection =
                context.HttpContext.RequestServices.GetService<KeygateProtection>();
            if (protection == null)
            {
                context.Result = Problem(
                    StatusCodes.Status500InternalServerError,
                    "KEYGATE_NOT_CONFIGURED",
                    "AddKeygate() was not called for this application.");
                return Task.CompletedTask;
            }

            LicenseSnapshot? license = protection.CurrentLicense;
            if (license == null || !license.IsUsableAt(DateTimeOffset.UtcNow))
            {
                context.Result = Problem(
                    StatusCodes.Status402PaymentRequired,
                    "LICENSE_REQUIRED",
                    "A valid license is required for this endpoint.");
                return Task.CompletedTask;
            }

            string[] missing = Features.Where(f => !protection.HasFeature(f)).ToArray();
            if (missing.Length > 0)
            {
                context.Result = Problem(
                    StatusCodes.Status403Forbidden,
                    "FEATURE_NOT_LICENSED",
                    "The license does not include: " + string.Join(", ", missing) + ".");
            }
            return Task.CompletedTask;
        }

        private static ObjectResult Problem(int status, string code, string message) =>
            new ObjectResult(new { success = false, error = new { code, message } })
            {
                StatusCode = status
            };
    }
}
