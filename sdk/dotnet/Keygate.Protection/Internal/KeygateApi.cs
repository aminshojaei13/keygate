using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Keygate.Protection.Internal
{
    internal sealed class ApiEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("data")] public JsonElement Data { get; set; }
        [JsonPropertyName("error")] public ApiError? Error { get; set; }
    }

    internal sealed class ApiError
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("details")] public JsonElement Details { get; set; }
    }

    internal sealed class ActivationResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("license_id")] public string LicenseId { get; set; } = "";
        [JsonPropertyName("product_id")] public string? ProductId { get; set; }
        [JsonPropertyName("token")] public string Token { get; set; } = "";
    }

    internal sealed class VerifyResponse
    {
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("product_id")] public string? ProductId { get; set; }
        [JsonPropertyName("plan_id")] public string? PlanId { get; set; }
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("features")] public JsonElement Features { get; set; }
        [JsonPropertyName("grace_days")] public int GraceDays { get; set; }
    }

    internal sealed class PublicKeyResponse
    {
        [JsonPropertyName("kid")] public string Kid { get; set; } = "";
        [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
        [JsonPropertyName("public_key")] public string PublicKey { get; set; } = "";
    }

    internal sealed class ReportResponse
    {
        [JsonPropertyName("event_id")] public string EventId { get; set; } = "";
        [JsonPropertyName("accepted")] public bool Accepted { get; set; }
        [JsonPropertyName("duplicate")] public bool Duplicate { get; set; }
        [JsonPropertyName("verified")] public bool Verified { get; set; }
        [JsonPropertyName("action_taken")] public string? ActionTaken { get; set; }
        [JsonPropertyName("license_status")] public string? LicenseStatus { get; set; }
        [JsonPropertyName("under_review")] public bool UnderReview { get; set; }
        [JsonPropertyName("is_allowed")] public bool IsAllowed { get; set; } = true;
        [JsonPropertyName("retry_after")] public int RetryAfter { get; set; }
    }

    /// <summary>
    /// Small HTTP client for the public Keygate API. It never talks to
    /// /api/v1/admin and never sends a license key to a host other than the
    /// configured base URL.
    /// </summary>
    internal sealed class KeygateApi : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        private readonly TimeSpan _timeout;
        private readonly Uri _baseUri;

        public KeygateApi(KeygateOptions options, HttpClient? http = null)
        {
            _timeout = options.HttpTimeout;
            string baseUrl = options.BaseUrl.TrimEnd('/') + "/";
            _baseUri = new Uri(baseUrl, UriKind.Absolute);

            if (http != null)
            {
                _http = http;
                _ownsClient = false;
            }
            else if (options.HttpClient != null)
            {
                _http = options.HttpClient;
                _ownsClient = false;
            }
            else
            {
                _http = new HttpClient { Timeout = options.HttpTimeout };
                _ownsClient = true;
            }

            if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("Keygate.Protection/0.1.0");
            }
            if (!_http.DefaultRequestHeaders.Contains("X-Keygate-Sdk"))
                _http.DefaultRequestHeaders.Add("X-Keygate-Sdk", "dotnet/0.1.0");
        }

        public async Task<PublicKeyResponse> GetLicensePublicKeyAsync(CancellationToken cancellationToken)
        {
            return await SendAsync<PublicKeyResponse>(
                HttpMethod.Get, "api/v1/license/pubkey", null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ActivationResponse> ActivateAsync(
            object body, CancellationToken cancellationToken)
        {
            return await SendAsync<ActivationResponse>(
                HttpMethod.Post, "api/v1/license/activate", body, cancellationToken).ConfigureAwait(false);
        }

        public async Task<VerifyResponse> VerifyAsync(
            object body, CancellationToken cancellationToken)
        {
            return await SendAsync<VerifyResponse>(
                HttpMethod.Post, "api/v1/license/verify", body, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeactivateAsync(object body, CancellationToken cancellationToken)
        {
            await SendAsync<JsonElement>(
                HttpMethod.Post, "api/v1/license/deactivate", body, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ReportResponse> ReportSecurityEventAsync(
            object body, CancellationToken cancellationToken)
        {
            return await SendAsync<ReportResponse>(
                HttpMethod.Post, "api/v1/license/security-events", body, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> SendAsync<T>(
            HttpMethod method, string relativePath, object? body, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath));
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Add("Cache-Control", "no-store");
            if (body != null)
            {
                string json = JsonSerializer.Serialize(body, JsonOptions);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                using (var timeoutCts = new CancellationTokenSource(_timeout))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeoutCts.Token))
                {
                    response = await _http.SendAsync(request, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new KeygateNetworkException(
                    "The Keygate request timed out. This is a network problem, not evidence of tampering.");
            }
            catch (HttpRequestException ex)
            {
                throw new KeygateNetworkException(
                    "Could not reach the Keygate server. This is a network problem, not evidence of tampering.", ex);
            }
            catch (IOException ex)
            {
                throw new KeygateNetworkException(
                    "The Keygate request failed while reading the connection.", ex);
            }

            using (response)
            {
                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                ApiEnvelope? envelope = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        envelope = JsonSerializer.Deserialize<ApiEnvelope>(text, JsonOptions);
                    }
                    catch (JsonException)
                    {
                        if (response.IsSuccessStatusCode)
                            throw new KeygateProtocolException(
                                "The Keygate server returned a non-JSON response.");
                    }
                }

                if (envelope == null)
                {
                    if (response.IsSuccessStatusCode) return default!;
                    throw new KeygateApiException(
                        response.StatusCode,
                        "HTTP_" + (int)response.StatusCode,
                        "The Keygate server returned HTTP " + (int)response.StatusCode + ".");
                }

                if (!envelope.Success || !response.IsSuccessStatusCode)
                {
                    string code = envelope.Error?.Code ?? ("HTTP_" + (int)response.StatusCode);
                    string message = envelope.Error?.Message ?? "The Keygate request was refused.";
                    if (envelope.Error != null && envelope.Error.Details.ValueKind == JsonValueKind.Object &&
                        envelope.Error.Details.TryGetProperty("retry_after", out JsonElement retry) &&
                        retry.TryGetInt32(out int retryAfter) && retryAfter > 0)
                    {
                        message += " Retry after " + retryAfter + " seconds.";
                    }
                    throw new KeygateApiException(response.StatusCode, code, message);
                }

                if (typeof(T) == typeof(JsonElement)) return (T)(object)envelope.Data;
                if (envelope.Data.ValueKind == JsonValueKind.Undefined ||
                    envelope.Data.ValueKind == JsonValueKind.Null)
                {
                    return default!;
                }
                T? value = envelope.Data.Deserialize<T>(JsonOptions);
                if (value == null)
                    throw new KeygateProtocolException("The Keygate response had an empty data object.");
                return value;
            }
        }

        public void Dispose()
        {
            if (_ownsClient) _http.Dispose();
        }
    }
}
