using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Keygate.Protection.AspNetCore
{
    /// <summary>
    /// Initializes the SDK once at startup and keeps the license fresh in the
    /// background. A failure to reach the server is logged, never fatal: the
    /// cached signed token keeps the process serving until it expires.
    /// </summary>
    internal sealed class KeygateHostedService : IHostedService, IDisposable
    {
        private readonly KeygateProtection _protection;
        private readonly KeygateAspNetCoreOptions _options;
        private readonly ILogger<KeygateHostedService> _logger;

        public KeygateHostedService(
            KeygateProtection protection,
            KeygateAspNetCoreOptions options,
            ILogger<KeygateHostedService> logger)
        {
            _protection = protection;
            _options = options;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _protection.InitializeAsync(cancellationToken).ConfigureAwait(false);
                LicenseValidation validation =
                    await _protection.ValidateLicenseAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                if (!validation.IsValid)
                {
                    _logger.LogWarning(
                        "Keygate license is not valid at startup: {Code} {Problem}. " +
                        "Requests will be refused until it is resolved.",
                        validation.ProblemCode, validation.Problem);
                }
                else if (validation.NetworkError)
                {
                    _logger.LogWarning(
                        "Keygate server unreachable at startup; running on the cached license until it expires.");
                }
                else
                {
                    _logger.LogInformation(
                        "Keygate license {LicenseId} validated for product {ProductId}.",
                        validation.License?.LicenseId, validation.License?.ProductId);
                }

                if (_options.PeriodicValidationInterval.HasValue)
                {
                    _protection.StartPeriodicValidation(_options.PeriodicValidationInterval);
                }
            }
            catch (KeygateException ex)
            {
                // Misconfiguration should be visible but must not take the
                // whole host down: the middleware will refuse licensed routes
                // with a clear error instead.
                _logger.LogError(ex, "Keygate initialization failed.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _protection.StopPeriodicValidation();
            return Task.CompletedTask;
        }

        public void Dispose() => _protection.StopPeriodicValidation();
    }
}
