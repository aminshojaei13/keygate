using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keygate.Sample
{
// All API responses under /api/v1 use this envelope:
//   success: {"success":true,"data":...}
//   error:   {"success":false,"error":{"code","message","details"?}}
public sealed class ApiEnvelope<T> where T : class
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("data")] public T? Data { get; set; }
    [JsonPropertyName("error")] public ApiError? Error { get; set; }
}

public sealed class ApiError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

// POST /api/v1/license/activate
public sealed class ActivateResult
{
    [JsonPropertyName("status")] public string Status { get; set; } = ""; // "activated" | "already_activated"
    [JsonPropertyName("license_id")] public string LicenseId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

// POST /api/v1/license/verify
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

// GET /api/v1/license/pubkey
public sealed class PublicKeyResult
{
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "";
    [JsonPropertyName("public_key")] public string PublicKey { get; set; } = ""; // hex
    [JsonPropertyName("format")] public string Format { get; set; } = "";
}

// The signed offline payload inside the token.
public sealed class VerifyToken
{
    [JsonPropertyName("lid")] public string LicenseId { get; set; } = "";
    [JsonPropertyName("pid")] public string ProductId { get; set; } = "";
    [JsonPropertyName("pln")] public string PlanId { get; set; } = "";
    [JsonPropertyName("sts")] public string Status { get; set; } = "";
    [JsonPropertyName("did")] public string Identifier { get; set; } = "";
    [JsonPropertyName("ftr")] public Dictionary<string, JsonElement>? Features { get; set; }
    [JsonPropertyName("iat")] public long IssuedAt { get; set; }
    [JsonPropertyName("exp")] public long ExpiresAt { get; set; }
    [JsonPropertyName("vun")] public long ValidUntil { get; set; }
    [JsonPropertyName("upd")] public long UpdatesUntil { get; set; }
    [JsonPropertyName("grc")] public int GraceDays { get; set; }
    [JsonPropertyName("nce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("fpr")] public string Fingerprint { get; set; } = "";
}

public sealed class KeygateApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public KeygateApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }

    // A friendly, actionable message for each documented error code.
    public string Friendly() => Code switch
    {
        "LICENSE_NOT_FOUND" => "License key is not valid for this product (or not activated on this device).",
        "ACTIVATION_LIMIT" => "Activation limit reached. Deactivate another device first.",
        "FEATURE_NOT_AVAILABLE" => "This product does not support device activation.",
        "LOCKED_OUT" => "Too many failed attempts. Please wait and try again.",
        "IDEMPOTENCY_KEY_CONFLICT" => "Same idempotency key was used with a different payload.",
        "IDEMPOTENCY_IN_FLIGHT" => "An identical request is already in progress.",
        "UPDATES_EXPIRED" => "Your update period has ended; renew to install this release.",
        "RELEASE_YANKED" => "That release was withdrawn. Pick another version.",
        "PLATFORM_NOT_AVAILABLE" => "No artifact exists for the requested platform.",
        "STORAGE_DISABLED" => "Release storage is not configured on the server.",
        _ => Message,
    };
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new JsonSerializerOptions()
    {
        PropertyNameCaseInsensitive = true,
    };
}

}
