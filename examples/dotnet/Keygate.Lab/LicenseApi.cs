using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keygate.Lab;

public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("details")] public JsonElement? Details { get; set; }
}

public sealed class ActivateResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("license_id")] public string LicenseId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public sealed class VerifyResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("plan_id")] public string PlanId { get; set; } = "";
    [JsonPropertyName("plan_name")] public string PlanName { get; set; } = "";
    [JsonPropertyName("valid_until")] public DateTimeOffset? ValidUntil { get; set; }
    [JsonPropertyName("updates_until")] public DateTimeOffset? UpdatesUntil { get; set; }
    [JsonPropertyName("features")] public Dictionary<string, JsonElement>? Features { get; set; }
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("grace_days")] public int GraceDays { get; set; }
}

public sealed class PublicKeyResult
{
    [JsonPropertyName("kid")] public string Kid { get; set; } = "";
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("keys")] public List<PublicKeyEntry>? Keys { get; set; }
}

public sealed class PublicKeyEntry
{
    [JsonPropertyName("kid")] public string Kid { get; set; } = "";
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

public sealed class DeactivateResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

public sealed class ResetBindingResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("freed")] public int Freed { get; set; }
}

internal sealed class ActivateRequest
{
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
    [JsonPropertyName("identifier_type")] public string IdentifierType { get; set; } = "device";

    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("previous_identifier"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousIdentifier { get; set; }
}

internal sealed class VerifyRequest
{
    [JsonPropertyName("license_key")] public string LicenseKey { get; set; } = "";
    [JsonPropertyName("identifier")] public string Identifier { get; set; } = "";
}

/// <summary>
/// The PUBLIC SDK surface only (/api/v1/license/*). Never touches /admin/*,
/// because a shipped desktop binary must not carry an admin API key.
/// </summary>
public sealed class LicenseApi : IDisposable
{
    private readonly HttpClient _http;

    public LicenseApi(string baseUrl)
    {
        var root = NormalizeBaseUrl(baseUrl);
        _http = new HttpClient
        {
            BaseAddress = new Uri(root.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        var value = (baseUrl ?? "").Trim();
        if (value.Length == 0)
            throw new LabException("INVALID_SERVER", "server URL is empty. Pass --server http://host:port");

        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new LabException("INVALID_SERVER", $"'{baseUrl}' is not an http(s) URL.");
        }

        // Be forgiving if the user pasted the SDK prefix by mistake.
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            path = path[..^"/api/v1".Length];

        return (uri.GetLeftPart(UriPartial.Authority) + path).TrimEnd('/');
    }

    public Task<PublicKeyResult> GetPublicKeyAsync(CancellationToken ct = default) =>
        SendAsync<PublicKeyResult>(HttpMethod.Get, "api/v1/license/pubkey", null, ct);

    public Task<ActivateResult> ActivateAsync(
        string licenseKey,
        string identifier,
        string? label = null,
        string? previousIdentifier = null,
        CancellationToken ct = default)
    {
        var body = new ActivateRequest
        {
            LicenseKey = licenseKey,
            Identifier = identifier,
            Label = string.IsNullOrWhiteSpace(label) ? null : label,
            PreviousIdentifier = string.IsNullOrWhiteSpace(previousIdentifier) ? null : previousIdentifier.Trim(),
        };
        return SendAsync<ActivateResult>(HttpMethod.Post, "api/v1/license/activate", body, ct);
    }

    public Task<VerifyResult> VerifyAsync(
        string licenseKey, string identifier, CancellationToken ct = default)
    {
        var body = new VerifyRequest { LicenseKey = licenseKey, Identifier = identifier };
        return SendAsync<VerifyResult>(HttpMethod.Post, "api/v1/license/verify", body, ct);
    }

    public Task<DeactivateResult> DeactivateAsync(
        string licenseKey, string identifier, CancellationToken ct = default)
    {
        var body = new VerifyRequest { LicenseKey = licenseKey, Identifier = identifier };
        return SendAsync<DeactivateResult>(HttpMethod.Post, "api/v1/license/deactivate", body, ct);
    }

    /// <summary>
    /// Mock-only stand-in for POST /admin/licenses/:id/reset-device-binding.
    /// A real server has no public equivalent, which is exactly why strict
    /// binding cannot be released by the client.
    /// </summary>
    public Task<ResetBindingResult> ResetDeviceBindingAsync(CancellationToken ct = default) =>
        SendAsync<ResetBindingResult>(HttpMethod.Post, "api/v1/lab/reset-binding", new { }, ct);

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            // StringContent sets Content-Length, which the tiny mock server
            // (and every normal proxy) understands without chunked decoding.
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, LabJson.Compact), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new KeygateApiException(
                0, "NETWORK_ERROR", $"cannot reach {_http.BaseAddress}: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            throw new KeygateApiException(
                0, "NETWORK_ERROR", $"request to {_http.BaseAddress} timed out: {ex.Message}");
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            ApiEnvelope<T>? envelope = null;
            try
            {
                envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(raw, LabJson.Compact);
            }
            catch (JsonException)
            {
                // handled below as BAD_RESPONSE
            }

            if (envelope is null)
            {
                throw new KeygateApiException(
                    (int)response.StatusCode, "BAD_RESPONSE",
                    raw.Length <= 300 ? raw : raw[..300] + "...");
            }

            if (!response.IsSuccessStatusCode || !envelope.Success)
            {
                throw new KeygateApiException(
                    (int)response.StatusCode,
                    envelope.Error?.Code ?? "UNKNOWN",
                    envelope.Error?.Message ?? $"HTTP {(int)response.StatusCode}",
                    envelope.Error?.Details);
            }

            if (envelope.Data is null)
                throw new KeygateApiException((int)response.StatusCode, "EMPTY_DATA", "server returned no data.");

            return envelope.Data;
        }
    }

    public void Dispose() => _http.Dispose();
}
