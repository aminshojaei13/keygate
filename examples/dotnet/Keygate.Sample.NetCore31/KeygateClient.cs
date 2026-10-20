using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Keygate.Sample
{
/// <summary>
/// A thin client for the PUBLIC SDK surface only
/// (/api/v1/license/*). It deliberately does NOT touch /api/v1/admin/*,
/// because a shipped desktop app must never carry an admin API key.
/// </summary>
public sealed class KeygateClient
{
    private readonly HttpClient _http;

    public KeygateClient(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(root), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Task<ActivateResult> ActivateAsync(
        string licenseKey, string identifier, string? label = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["license_key"] = licenseKey,
            ["identifier"] = identifier,
        };
        if (!string.IsNullOrWhiteSpace(label)) body["label"] = label;
        return SendAsync<ActivateResult>(HttpMethod.Post, "api/v1/license/activate", body, ct);
    }

    public Task<VerifyResult> VerifyAsync(
        string licenseKey, string identifier, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["license_key"] = licenseKey,
            ["identifier"] = identifier,
        };
        return SendAsync<VerifyResult>(HttpMethod.Post, "api/v1/license/verify", body, ct);
    }

    public Task<PublicKeyResult> GetPublicKeyAsync(CancellationToken ct = default) =>
        SendAsync<PublicKeyResult>(HttpMethod.Get, "api/v1/license/pubkey", null, ct);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct) where T : class
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), System.Text.Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new KeygateApiException(0, "NETWORK_ERROR", $"Cannot reach server: {ex.Message}");
        }

        using (resp)
        {
            string raw = await resp.Content.ReadAsStringAsync();

            ApiEnvelope<T>? env = null;
            try { env = JsonSerializer.Deserialize<ApiEnvelope<T>>(raw, JsonDefaults.Options); }
            catch (JsonException) { /* fall through */ }

            if (env is null)
                throw new KeygateApiException((int)resp.StatusCode, "BAD_RESPONSE", Truncate(raw));

            if (!resp.IsSuccessStatusCode || !env.Success)
            {
                var code = env.Error?.Code ?? "UNKNOWN";
                var message = env.Error?.Message ?? $"HTTP {(int)resp.StatusCode}";
                throw new KeygateApiException((int)resp.StatusCode, code, message);
            }

            if (env.Data is null)
                throw new KeygateApiException((int)resp.StatusCode, "EMPTY_DATA", "Server returned no data.");

            return env.Data;
        }
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}

}
