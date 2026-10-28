using Keygate.Protection;
using Keygate.Protection.AspNetCore;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// One license for the whole process. On a server the device identity is the
// host's, so activation binds the deployment to this machine or container.
builder.Services.AddKeygate(options =>
{
    options.Protection.BaseUrl =
        builder.Configuration["Keygate:BaseUrl"] ?? "http://localhost:9000";
    options.Protection.LicenseKey = builder.Configuration["Keygate:LicenseKey"];
    options.Protection.ProductId = builder.Configuration["Keygate:ProductId"];
    options.Protection.LicensePublicKey = builder.Configuration["Keygate:PublicKey"];
    options.Protection.LicensePublicKeyId = builder.Configuration["Keygate:PublicKeyId"];
    options.Protection.Log = message =>
        Console.Error.WriteLine("[keygate] " + message);

    // Whole-app gate. Set false if only some endpoints are licensed and you
    // rely on [RequireKeygateFeature] instead.
    options.BlockOnInvalidLicense =
        builder.Configuration.GetValue("Keygate:BlockWhenInvalid", true);
    options.PeriodicValidationInterval = TimeSpan.FromHours(6);
});

var app = builder.Build();

app.UseKeygate();
app.MapControllers();

app.Run();

/// <summary>
/// A controller, because minimal APIs cannot use the attribute filter.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class ReportsController : ControllerBase
{
    [HttpGet("basic")]
    public IActionResult Basic() => Ok(new { tier = "basic" });

    // 402 when the license is unusable, 403 when it lacks "pro".
    [HttpGet("pro")]
    [RequireKeygateFeature("pro")]
    public IActionResult Pro() => Ok(new { tier = "pro" });

    [HttpGet("license")]
    public IActionResult License()
    {
        var snapshot = HttpContext.Items["Keygate.License"] as LicenseSnapshot;
        return snapshot == null
            ? Ok(new { license = (object?)null })
            : Ok(new
            {
                license_id = snapshot.LicenseId,
                product_id = snapshot.ProductId,
                status = snapshot.Status,
                valid_until = snapshot.ValidUntil
            });
    }
}
