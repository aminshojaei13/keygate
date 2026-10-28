using Keygate.Protection;
using Keygate.Protection.Security;

// Desktop host pattern. The same code is called from the startup path of a
// WPF / WinForms / Avalonia application; it is a console app here only so the
// sample builds and runs on every platform.
//
//  1. initialize + validate on startup, before the main window opens
//  2. check file integrity once, in the background
//  3. keep the license fresh while the app stays open
//
// Integrity checking needs a signed manifest and a pinned release key:
//
//   KEYGATE_MANIFEST=/path/to/manifest.json
//   KEYGATE_RELEASE_KEY=<64 hex>
//   KEYGATE_RELEASE_KEY_ID=rel_...

var options = new KeygateOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("KEYGATE_URL") ?? "http://localhost:9000",
    LicenseKey = Environment.GetEnvironmentVariable("KEYGATE_LICENSE_KEY"),
    ProductId = Environment.GetEnvironmentVariable("KEYGATE_PRODUCT_ID"),
    LicensePublicKey = Environment.GetEnvironmentVariable("KEYGATE_PUBLIC_KEY"),
    DeviceLabel = "Desktop sample",
    IntegrityManifestPath = Environment.GetEnvironmentVariable("KEYGATE_MANIFEST"),
    IntegrityInstallDirectory = AppContext.BaseDirectory,
    PeriodicValidationInterval = TimeSpan.FromHours(6),
    Log = message => Console.Error.WriteLine("[keygate] " + message)
};

string? releaseKey = Environment.GetEnvironmentVariable("KEYGATE_RELEASE_KEY");
if (!string.IsNullOrEmpty(releaseKey))
{
    options.ReleasePublicKeys[Environment.GetEnvironmentVariable("KEYGATE_RELEASE_KEY_ID") ?? "release"] = releaseKey;
}

using var protection = new KeygateProtection(options);

protection.PeriodicValidationCompleted += (_, result) =>
{
    if (!result.IsValid)
    {
        // On the UI thread this is where you would show the "license
        // problem" dialog or switch the app into read-only mode. Do not
        // silently quit: a suspended license is usually fixable.
        Console.Error.WriteLine($"license is no longer valid: {result.ProblemCode}");
    }
};

await protection.InitializeAsync();

LicenseValidation startup = await protection.ValidateLicenseAsync();
if (!startup.IsValid)
{
    Console.Error.WriteLine($"cannot start: {startup.ProblemCode} {startup.Problem}");
    return 1;
}
Console.WriteLine("startup ok, license " + startup.License?.LicenseId);

if (!string.IsNullOrEmpty(options.IntegrityManifestPath))
{
    IntegrityCheckResult integrity = await protection.CheckIntegrityAsync();
    if (!integrity.IsValid)
    {
        // A mismatch is reported automatically (AutoReportIntegrityFailures).
        // Decide per product whether to refuse to start; this sample only
        // warns, because a false positive must not lock a paying customer out.
        Console.Error.WriteLine(
            $"integrity: {integrity.ModifiedFiles.Count} modified, " +
            $"{integrity.MissingFiles.Count} missing, {integrity.Errors.Count} unreadable");
    }
    else
    {
        Console.WriteLine($"integrity ok: {integrity.PassedFiles}/{integrity.TotalFiles} files");
    }
}

protection.StartPeriodicValidation();
Console.WriteLine("running. press Ctrl+C to stop.");
await Task.Delay(Timeout.InfiniteTimeSpan);
return 0;
