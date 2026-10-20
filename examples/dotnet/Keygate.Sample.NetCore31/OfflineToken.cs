using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using System.Text;
using System.Text.Json;

namespace Keygate.Sample
{
public sealed class OfflineTokenException : Exception
{
    public OfflineTokenException(string message) : base(message) { }
}

/// <summary>
/// Verifies and decodes a Keygate offline token of the form
///   base64url(payload_json) "." base64url(signature)
/// The signature (Ed25519, 64 bytes) is computed over the base64 text of the
/// payload half -- NOT over the raw JSON. See docs/team-guide/offline-licensing-guide.md.
/// </summary>
public static class OfflineToken
{
    public static VerifyToken VerifyAndParse(string token, byte[] publicKey)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new OfflineTokenException("Token is empty.");

        int idx = token.LastIndexOf('.');
        if (idx <= 0 || idx == token.Length - 1)
            throw new OfflineTokenException("Invalid token format (expected payload.signature).");

        string payloadB64 = token[..idx];
        string sigB64 = token[(idx + 1)..];

        byte[] signature;
        try { signature = Base64Url.Decode(sigB64); }
        catch (FormatException) { throw new OfflineTokenException("Signature is not valid base64url."); }

        // The signed message is the ASCII bytes of the payload half.
        byte[] message = Encoding.ASCII.GetBytes(payloadB64);

        if (!Ed25519Verifier.Verify(publicKey, message, signature))
            throw new OfflineTokenException("Signature verification failed.");

        byte[] payloadBytes;
        try { payloadBytes = Base64Url.Decode(payloadB64); }
        catch (FormatException) { throw new OfflineTokenException("Payload is not valid base64url."); }

        VerifyToken token1;
        try
        {
            token1 = JsonSerializer.Deserialize<VerifyToken>(payloadBytes, JsonDefaults.Options)
                     ?? throw new OfflineTokenException("Payload is empty.");
        }
        catch (JsonException) { throw new OfflineTokenException("Payload is not valid JSON."); }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (token1.ExpiresAt > 0 && now > token1.ExpiresAt)
            throw new OfflineTokenException("Token has expired; an online verify is required.");

        return token1;
    }

    /// <summary>
    /// True when the license itself is still within (valid_until + grace).
    /// vun == 0 means perpetual. grc is the server-honoured grace in days.
    /// </summary>
    public static bool LicenseTermOk(VerifyToken t, out string reason)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (t.ValidUntil > 0)
        {
            long deadline = t.ValidUntil + (long)t.GraceDays * 86400;
            if (now > deadline)
            {
                reason = "The license term (plus grace) has ended.";
                return false;
            }
        }
        reason = "";
        return true;
    }
}

}
