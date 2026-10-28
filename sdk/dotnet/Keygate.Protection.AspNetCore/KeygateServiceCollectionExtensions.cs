using System;
using Keygate.Protection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Keygate.Protection.AspNetCore
{
    /// <summary>
    /// Registration helpers. Typical usage:
    ///
    /// <code>
    /// builder.Services.AddKeygate(o =>
    /// {
    ///     o.Protection.BaseUrl = "https://licenses.example.com";
    ///     o.Protection.LicenseKey = builder.Configuration["Keygate:LicenseKey"];
    ///     o.Protection.ProductId = "prod_...";
    ///     o.Protection.LicensePublicKey = builder.Configuration["Keygate:PublicKey"];
    /// });
    ///
    /// var app = builder.Build();
    /// app.UseKeygate();
    /// </code>
    /// </summary>
    public static class KeygateServiceCollectionExtensions
    {
        public static IServiceCollection AddKeygate(
            this IServiceCollection services, Action<KeygateAspNetCoreOptions> configure)
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (configure == null) throw new ArgumentNullException(nameof(configure));

            services.AddOptions<KeygateAspNetCoreOptions>().Configure(configure);
            services.TryAddSingleton(provider =>
            {
                KeygateAspNetCoreOptions options = provider
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<KeygateAspNetCoreOptions>>()
                    .Value;
                return new KeygateProtection(options.Protection);
            });
            services.AddHostedService<KeygateHostedService>();
            return services;
        }

        /// <summary>
        /// Adds the license middleware and the attribution header. Place it
        /// after exception handling and before endpoints.
        /// </summary>
        public static IApplicationBuilder UseKeygate(this IApplicationBuilder app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));
            return app.UseMiddleware<KeygateProtectionMiddleware>();
        }
    }
}
