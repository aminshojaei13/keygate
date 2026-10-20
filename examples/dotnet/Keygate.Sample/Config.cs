using System.Text.Json;

namespace Keygate.Sample;

/// <summary>
/// Configuration is read from appsettings.json and can be overridden by
/// environment variables. No secrets live here -- this client only ever uses
/// a license key, which the end user supplies at runtime.
/// </summary>
public sealed class AppConfig
{
    public string BaseUrl { get; init; } = "http://localhost:9000";

    public static AppConfig Load()
    {
        string baseUrl = "http://localhost:9000";

        // 1) appsettings.json next to the executable (or in the project dir).
        foreach (var candidate in new[] { "appsettings.json", Path.Combine(AppContext.BaseDirectory, "appsettings.json") })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                if (doc.RootElement.TryGetProperty("Keygate", out var kg) &&
                    kg.TryGetProperty("BaseUrl", out var b) &&
                    b.ValueKind == JsonValueKind.String)
                {
                    baseUrl = b.GetString() ?? baseUrl;
                }
            }
            catch (JsonException) { /* keep default */ }
            break;
        }

        // 2) Environment variable override (KEYGATE_BASE_URL).
        var env = Environment.GetEnvironmentVariable("KEYGATE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(env)) baseUrl = env;

        return new AppConfig { BaseUrl = baseUrl };
    }
}
