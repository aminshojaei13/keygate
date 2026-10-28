using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keygate.Lab;

public static class Base64Url
{
    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 0: break;
            case 2: s += "=="; break;
            case 3: s += "="; break;
            default: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(s);
    }

    public static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class HexCodec
{
    public static byte[] Decode(string hex)
    {
        try
        {
            return Convert.FromHexString(hex.Trim());
        }
        catch (FormatException)
        {
            throw new LabException("PUBLIC_KEY_INVALID", "expected a hex-encoded Ed25519 public key.");
        }
    }

    public static string Encode(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

public static class Ed25519
{
    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.PublicKeySize) return false;
        if (signature.Length != Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.SignatureSize) return false;
        return Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(
            signature, 0, publicKey, 0, message, 0, message.Length);
    }

    /// <summary>Sign with a 32-byte Ed25519 seed (what Keygate calls the private key).</summary>
    public static byte[] Sign(byte[] seed, byte[] message)
    {
        var signature = new byte[Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.SignatureSize];
        Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Sign(
            seed, 0, message, 0, message.Length, signature, 0);
        return signature;
    }

    public static byte[] PublicKeyFromSeed(byte[] seed)
    {
        var publicKey = new byte[Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.PublicKeySize];
        Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
        return publicKey;
    }
}

/// <summary>
/// The signed offline payload. Field names match internal/license/token.go.
/// </summary>
public sealed class TokenPayload
{
    [JsonPropertyName("ver")] public string Version { get; set; } = "";
    [JsonPropertyName("kid")] public string KeyId { get; set; } = "";
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

/// <summary>
/// Verifies tokens exactly the way docs/team-guide/offline-licensing-guide.md
/// describes: base64url(payload).base64url(signature), and the signature is
/// over the ASCII bytes of the payload half (not over the raw JSON).
/// </summary>
public static class OfflineToken
{
    public static TokenPayload Verify(string token, byte[] publicKey, bool checkExpiry = true)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new LabException("TOKEN_MALFORMED", "the saved token is empty.");

        token = token.Trim();
        var dot = token.LastIndexOf('.');
        if (dot <= 0 || dot == token.Length - 1)
            throw new LabException("TOKEN_MALFORMED", "token is not in 'payload.signature' form.");

        var payloadB64 = token[..dot];
        var signatureB64 = token[(dot + 1)..];

        byte[] signature;
        try
        {
            signature = Base64Url.Decode(signatureB64);
        }
        catch (FormatException)
        {
            throw new LabException("TOKEN_MALFORMED", "token signature is not valid base64url.");
        }

        if (publicKey.Length != Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.PublicKeySize)
            throw new LabException("PUBLIC_KEY_INVALID", "the pinned public key is not 32 bytes.");

        var signedMessage = Encoding.ASCII.GetBytes(payloadB64);
        if (!Ed25519.Verify(publicKey, signedMessage, signature))
            throw new LabException(
                "TOKEN_SIGNATURE_INVALID",
                "Ed25519 signature verification failed. The token was edited, or it came from another server key.");

        TokenPayload payload;
        try
        {
            var payloadBytes = Base64Url.Decode(payloadB64);
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes, LabJson.Options)
                      ?? throw new LabException("TOKEN_MALFORMED", "token payload is empty.");
        }
        catch (FormatException)
        {
            throw new LabException("TOKEN_MALFORMED", "token payload is not valid base64url.");
        }
        catch (JsonException)
        {
            throw new LabException("TOKEN_MALFORMED", "token payload is not valid JSON.");
        }

        if (checkExpiry && payload.ExpiresAt > 0 && LabUtil.NowUnix() > payload.ExpiresAt)
        {
            throw new LabException(
                "TOKEN_EXPIRED",
                $"the offline token expired at {LabUtil.FormatUnix(payload.ExpiresAt)}; run an online verify to refresh it.");
        }

        // A token may name the key that signed it (kid). With one pinned key
        // there is only one correct answer, so a mismatch means the server
        // rotated its signing key and this client must re-fetch /license/pubkey.
        if (!string.IsNullOrEmpty(payload.KeyId) &&
            !string.Equals(payload.KeyId, KeyId(publicKey), StringComparison.Ordinal))
        {
            throw new LabException(
                "TOKEN_KEY_UNKNOWN",
                $"the token was signed by '{payload.KeyId}', but the pinned key is '{KeyId(publicKey)}'. " +
                "Fetch /license/pubkey again (the server may have rotated its signing key).");
        }

        return payload;
    }

    /// <summary>Mirrors license.KeyIDFromPublicKey: "key_" + first 8 SHA-256 bytes.</summary>
    public static string KeyId(byte[] publicKey)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(publicKey);
        return "key_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Decode without trusting the signature. Only for diagnostics.</summary>
    public static TokenPayload DecodeUnsafe(string token)
    {
        var dot = token.LastIndexOf('.');
        if (dot <= 0)
            throw new LabException("TOKEN_MALFORMED", "token is not in 'payload.signature' form.");
        try
        {
            var bytes = Base64Url.Decode(token[..dot]);
            return JsonSerializer.Deserialize<TokenPayload>(bytes, LabJson.Options)
                   ?? throw new LabException("TOKEN_MALFORMED", "token payload is empty.");
        }
        catch (FormatException)
        {
            throw new LabException("TOKEN_MALFORMED", "token payload is not valid base64url.");
        }
        catch (JsonException)
        {
            throw new LabException("TOKEN_MALFORMED", "token payload is not valid JSON.");
        }
    }

    /// <summary>
    /// True while the licence itself is inside valid_until + grace. vun == 0
    /// means perpetual. grc is 0 for cancelled/suspended/revoked licences.
    /// </summary>
    public static bool LicenseTermOk(TokenPayload token, out string reason)
    {
        if (token.ValidUntil > 0)
        {
            var deadline = token.ValidUntil + (long)token.GraceDays * 86400;
            if (LabUtil.NowUnix() > deadline)
            {
                reason =
                    $"licence term ended at {LabUtil.FormatUnix(token.ValidUntil)} " +
                    $"(+{token.GraceDays} grace day(s)).";
                return false;
            }
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Binds a token to this install: did must equal the identifier and fpr
    /// must equal SHA256(identifier:product_id)[0..8] in hex.
    /// </summary>
    public static string? BindingError(TokenPayload token, string identifier)
    {
        if (!string.IsNullOrEmpty(token.Identifier) &&
            !string.Equals(token.Identifier, identifier, StringComparison.Ordinal))
        {
            return $"the token was issued for identifier '{token.Identifier}', but this install is '{identifier}'.";
        }

        if (!string.IsNullOrEmpty(token.Fingerprint))
        {
            var expected = Fingerprint(identifier, token.ProductId);
            if (!string.Equals(token.Fingerprint, expected, StringComparison.OrdinalIgnoreCase))
            {
                return $"the token fingerprint '{token.Fingerprint}' does not match '{expected}' " +
                       $"for identifier '{identifier}' (product '{token.ProductId}').";
            }
        }
        return null;
    }

    public static string Fingerprint(string identifier, string productId)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(identifier + ":" + productId));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string FormatFeature(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        _ => value.GetRawText(),
    };
}
