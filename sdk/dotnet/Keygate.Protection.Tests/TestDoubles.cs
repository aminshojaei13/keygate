using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Keygate.Protection.Internal;
using Keygate.Protection.Licensing;
using Keygate.Protection.Security;
using Keygate.Protection.Storage;

namespace Keygate.Protection.Tests;

internal sealed class MemorySecretStore : IKeygateSecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Values.TryGetValue(name, out string? value) ? value : null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        Values[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        Values.Remove(name);
        return Task.CompletedTask;
    }
}

internal sealed class FakeKeygateHandler : HttpMessageHandler
{
    public byte[] LicenseSeed { get; }
    public string LicensePublicKeyHex { get; }
    public string ProductId { get; set; } = "prod-1";
    public string LicenseId { get; set; } = "lic-1";
    public bool FailVerifyNetwork { get; set; }
    public bool FailActivate { get; set; }
    public string? LastDevicePublicKey { get; private set; }
    public string? LastSignature { get; private set; }
    public Dictionary<string, string>? LastDetails { get; private set; }
    public string? LastEventId { get; private set; }
    public string? LastIdentifier { get; private set; }
    public int ActivateCalls { get; private set; }
    public int VerifyCalls { get; private set; }

    public FakeKeygateHandler()
    {
        LicenseSeed = new byte[32];
        for (int i = 0; i < LicenseSeed.Length; i++) LicenseSeed[i] = (byte)i;
        LicensePublicKeyHex = Hex.Encode(Ed25519.PublicKeyFromSeed(LicenseSeed));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content == null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        string path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/api/v1/license/pubkey", StringComparison.Ordinal))
        {
            return Json(new
            {
                success = true,
                data = new
                {
                    kid = "key_test",
                    algorithm = "ed25519",
                    public_key = LicensePublicKeyHex
                }
            });
        }

        if (path.EndsWith("/api/v1/license/activate", StringComparison.Ordinal))
        {
            ActivateCalls++;
            using JsonDocument doc = JsonDocument.Parse(body);
            string identifier = doc.RootElement.GetProperty("identifier").GetString()!;
            LastIdentifier = identifier;
            LastDevicePublicKey = doc.RootElement.GetProperty("device_public_key").GetString();
            if (FailActivate)
                return Json(new { success = false, error = new { code = "ACTIVATION_LIMIT", message = "no seats" } }, 403);

            string token = TestTokens.Create(LicenseSeed, LicenseId, ProductId, identifier,
                nowUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Json(new
            {
                success = true,
                data = new
                {
                    status = "activated",
                    license_id = LicenseId,
                    product_id = ProductId,
                    token
                }
            });
        }

        if (path.EndsWith("/api/v1/license/verify", StringComparison.Ordinal))
        {
            VerifyCalls++;
            if (FailVerifyNetwork) throw new HttpRequestException("simulated offline");
            using JsonDocument doc = JsonDocument.Parse(body);
            string identifier = doc.RootElement.GetProperty("identifier").GetString()!;
            LastIdentifier = identifier;
            string token = TestTokens.Create(LicenseSeed, LicenseId, ProductId, identifier,
                nowUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return Json(new
            {
                success = true,
                data = new
                {
                    status = "active",
                    product_id = ProductId,
                    plan_id = "plan-1",
                    token
                }
            });
        }

        if (path.EndsWith("/api/v1/license/security-events", StringComparison.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            LastSignature = doc.RootElement.GetProperty("signature").GetString();
            LastEventId = doc.RootElement.GetProperty("event_id").GetString();
            LastDetails = new Dictionary<string, string>();
            foreach (JsonProperty p in doc.RootElement.GetProperty("details").EnumerateObject())
                LastDetails[p.Name] = p.Value.GetString() ?? "";

            // Verify the device signature exactly the way the Go server does.
            if (string.IsNullOrEmpty(LastDevicePublicKey))
                throw new InvalidOperationException("activation did not send a device public key");
            byte[] pub = Base64Url.Decode(LastDevicePublicKey!);
            long occurred = doc.RootElement.GetProperty("occurred_at").GetInt64();
            byte[] canonical = SecurityEventSigner.CanonicalBytes(
                LastEventId!, LicenseId, LastIdentifier!,
                doc.RootElement.GetProperty("event_type").GetString()!, occurred);
            bool verified = Ed25519.Verify(pub, canonical, Base64Url.Decode(LastSignature!));

            return Json(new
            {
                success = true,
                data = new
                {
                    event_id = LastEventId,
                    accepted = true,
                    duplicate = false,
                    verified,
                    action_taken = verified ? "flag" : "unverified",
                    license_status = "active",
                    under_review = verified,
                    is_allowed = true
                }
            });
        }

        if (path.EndsWith("/api/v1/license/deactivate", StringComparison.Ordinal))
            return Json(new { success = true, data = new { status = "deactivated" } });

        return Json(new { success = false, error = new { code = "NOT_FOUND", message = "unknown route" } }, 404);
    }

    private static HttpResponseMessage Json(object value, int status = 200) =>
        new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
}

internal static class TestTokens
{
    public static string Create(
        byte[] seed, string licenseId, string productId, string identifier, long nowUnix)
    {
        string fingerprint = LicenseTokenVerifier.Fingerprint(identifier, productId);
        var payload = new Dictionary<string, object?>
        {
            ["ver"] = "1",
            ["kid"] = "key_test",
            ["lid"] = licenseId,
            ["pid"] = productId,
            ["pln"] = "plan-1",
            ["sts"] = "active",
            ["did"] = identifier,
            ["ftr"] = new Dictionary<string, object?> { ["pro"] = true, ["seats"] = 3 },
            ["iat"] = nowUnix,
            ["exp"] = nowUnix + 86400,
            ["vun"] = 0,
            ["grc"] = 7,
            ["nce"] = "AAAAAAAAAAAAAAAAAAAAAA",
            ["fpr"] = fingerprint
        };
        string payloadJson = JsonSerializer.Serialize(payload);
        string payloadB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
        byte[] sig = Ed25519.Sign(seed, Encoding.ASCII.GetBytes(payloadB64));
        return payloadB64 + "." + Base64Url.Encode(sig);
    }
}
