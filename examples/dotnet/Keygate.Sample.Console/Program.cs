using Keygate.Protection;
using Keygate.Protection.Security;

// Minimal console host: activate once, then validate on every start.
//
//   KEYGATE_URL=https://licenses.example.com \
//   KEYGATE_LICENSE_KEY=KG-XXXX \
//   KEYGATE_PRODUCT_ID=prod_... \
//   KEYGATE_PUBLIC_KEY=<64 hex> \
//   dotnet run

var options = new KeygateOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("KEYGATE_URL") ?? "http://localhost:9000",
    LicenseKey = Environment.GetEnvironmentVariable("KEYGATE_LICENSE_KEY"),
    ProductId = Environment.GetEnvironmentVariable("KEYGATE_PRODUCT_ID"),
    LicensePublicKey = Environment.GetEnvironmentVariable("KEYGATE_PUBLIC_KEY"),
    Log = message => Console.Error.WriteLine("[keygate] " + message)
};

using var protection = new KeygateProtection(options);

try
{
    await protection.InitializeAsync();
    Console.WriteLine("device: " + protection.DeviceId);

    if (protection.CurrentLicense == null)
    {
        ActivationResult activation = await protection.ActivateAsync();
        Console.WriteLine($"activated: {activation.LicenseId} ({activation.Status})");
    }

    LicenseValidation validation = await protection.ValidateLicenseAsync();
    if (!validation.IsValid)
    {
        Console.Error.WriteLine($"license rejected: {validation.ProblemCode} {validation.Problem}");
        return 1;
    }

    if (validation.IsOffline)
        Console.WriteLine("offline mode: using the cached signed license");

    if (!protection.HasFeature("pro"))
    {
        Console.Error.WriteLine("this build requires the 'pro' feature");
        return 2;
    }

    // Optional: report a tamper signal the application detected itself.
    SecurityReportResult report = await protection.ReportSecurityEventAsync(new SecurityEvent
    {
        Type = SecurityEventTypes.UnexpectedEnvironment,
        Severity = "low",
        Details = new Dictionary<string, string>
        {
            ["source"] = "sample-console",
            ["reason"] = "debugger_attached"
        }
    });
    Console.WriteLine($"report accepted={report.Accepted} verified={report.Verified} action={report.ActionTaken}");

    Console.WriteLine("licensed work would run here.");
    return 0;
}
catch (KeygateException ex)
{
    // Configuration and API errors are not tampering; show them plainly.
    Console.Error.WriteLine($"keygate error: {ex.Message}");
    return 3;
}
