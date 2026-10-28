using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Keygate.Protection.Device;
using Keygate.Protection.Integrity;
using Keygate.Protection.Internal;
using Keygate.Protection.Licensing;
using Keygate.Protection.Security;
using Xunit;

namespace Keygate.Protection.Tests;

public class ProtocolVectorTests
{
    private static readonly byte[] Seed = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly string PublicHex = Hex.Encode(Ed25519.PublicKeyFromSeed(Seed));

    [Fact]
    public void DeviceId_MatchesTheGoServerVector()
    {
        var components = new Dictionary<string, string>
        {
            ["board"] = DeviceIdFormats.HashComponent("board", "BOARD-X"),
            ["os"] = DeviceIdFormats.HashComponent("os", "OS-Y"),
            ["cpu"] = DeviceIdFormats.HashComponent("cpu", "CPU-Z"),
            ["disk"] = DeviceIdFormats.HashComponent("disk", "DISK-Q"),
            ["nic"] = DeviceIdFormats.HashComponent("nic", "AA:BB:CC:DD:EE:FF")
        };

        Assert.Equal("47b9f1387d12a0747af7da6dd9cc947dfc9a969ac53fe6d2c16808742e9e03de",
            components["board"]);
        Assert.Equal("3631076dad8d788189aa43b328c683fb8575d816fd1a34b1ceb978c1f91b9eaa",
            DeviceIdFormats.ComputeCompositeDigest("darwin", components));
        Assert.Equal(
            "kgid:v1:darwin:3631076dad8d788189aa43b328c683fb8575d816fd1a34b1ceb978c1f91b9eaa",
            DeviceIdFormats.Format("darwin", DeviceIdFormats.ComputeCompositeDigest("darwin", components)));
    }

    [Fact]
    public void DeviceId_IsStableAndCanonical()
    {
        var a = new Dictionary<string, string> { ["os"] = "ABC", ["board"] = "XYZ" };
        var b = new Dictionary<string, string> { ["board"] = "xyz", ["os"] = "abc" };
        Assert.Equal(
            DeviceIdFormats.ComputeCompositeDigest("linux", a),
            DeviceIdFormats.ComputeCompositeDigest("linux", b));
        Assert.True(DeviceIdFormats.IsValidKgid(
            DeviceIdFormats.Format("linux", DeviceIdFormats.ComputeCompositeDigest("linux", a))));
    }

    [Fact]
    public void SecurityEventSignature_MatchesTheGoServerVector()
    {
        const string device = "kgid:v1:linux:0000000000000000000000000000000000000000000000000000000000000000";
        byte[] canonical = SecurityEventSigner.CanonicalBytes(
            "evt-vector-1", "lic_vector", device, "integrity_violation", 1700000000);

        Assert.Equal(
            "keygate-security-event-v1\nevt-vector-1\nlic_vector\n" + device +
            "\nintegrity_violation\n1700000000",
            Encoding.UTF8.GetString(canonical));
        Assert.Equal(
            "2v3Rvr0T7ci1e86SeFkpn5Oi1MBB_9mjzFDX8zLx5kKghXXsN793nyIAyfq85TsspZvW6ZqtgiVLSbxLIguzDw",
            Base64Url.Encode(Ed25519.Sign(Seed, canonical)));
        Assert.True(Ed25519.Verify(Convert.FromHexString(PublicHex), canonical,
            Base64Url.Decode("2v3Rvr0T7ci1e86SeFkpn5Oi1MBB_9mjzFDX8zLx5kKghXXsN793nyIAyfq85TsspZvW6ZqtgiVLSbxLIguzDw")));
    }

    [Fact]
    public void LicenseToken_VerifiesTheGoServerVector()
    {
        const string token =
            "eyJ2ZXIiOiIxIiwia2lkIjoia2V5XzU2NDc1YWE3NTQ2MzQ3NGMiLCJsaWQiOiJsaWNfdmVjdG9yIiwicGlkIjoicHJvZF92ZWN0b3IiLCJwbG4iOiJwbGFuX3ZlY3RvciIsInN0cyI6ImFjdGl2ZSIsImRpZCI6ImtnaWQ6djE6bGludXg6MDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMCIsImZ0ciI6eyJwcm8iOnRydWUsInNlYXRzIjozfSwiaWF0IjoxNzAwMDAwMDAwLCJleHAiOjE3MDAwODY0MDAsInZ1biI6MTgwMDAwMDAwMCwiZ3JjIjo3LCJuY2UiOiJNREV5TXpRMU5qYzRPV0ZpWTJSbFpnIiwiZnByIjoiZTZkOGE5ZmE5MTA5OWYyZCJ9.0tV--jECcYjMvd9O-rGEUiLAbAN4j6Ckooz-MhnGKto4WOC2l5GygcqI0VUWHqQcQ5V3d8sIcRYCahGgaQRXAA";
        const string device = "kgid:v1:linux:0000000000000000000000000000000000000000000000000000000000000000";

        var verifier = new LicenseTokenVerifier(PublicHex, "key_56475aa75463474c", null);
        LicenseSnapshot snapshot = verifier.Verify(
            token, device, "prod_vector", DateTimeOffset.FromUnixTimeSeconds(1700000000),
            TimeSpan.Zero, fromCache: false);

        Assert.Equal("lic_vector", snapshot.LicenseId);
        Assert.Equal("prod_vector", snapshot.ProductId);
        Assert.Equal("plan_vector", snapshot.PlanId);
        Assert.Equal("active", snapshot.Status);
        Assert.Equal(1800000000, snapshot.ValidUntil);
        Assert.Equal(7, snapshot.GraceDays);
        Assert.True(LicenseTokenVerifier.FeatureIsEnabled(snapshot.Features["pro"]));
        Assert.True(LicenseTokenVerifier.FeatureIsEnabled(snapshot.Features["seats"]));

        Assert.Throws<KeygateLicenseException>(() => verifier.Verify(
            token, device, "prod_vector", DateTimeOffset.FromUnixTimeSeconds(1700086401),
            TimeSpan.Zero, fromCache: false));
        Assert.Throws<KeygateLicenseException>(() => verifier.Verify(
            token, "kgid:v1:linux:" + new string('1', 64), "prod_vector",
            DateTimeOffset.FromUnixTimeSeconds(1700000000), TimeSpan.Zero, fromCache: false));
    }

    [Fact]
    public void LicenseToken_RejectsTamperingAndWrongProduct()
    {
        string token = TestTokens.Create(Seed, "lic", "prod", "device-1", 1700000000);
        byte[] tampered = Encoding.ASCII.GetBytes(token);
        tampered[10] = (byte)(tampered[10] == 'A' ? 'B' : 'A');

        var verifier = new LicenseTokenVerifier(PublicHex, "key_test", null);
        Assert.Throws<KeygateLicenseException>(() => verifier.Verify(
            Encoding.ASCII.GetString(tampered), "device-1", "prod",
            DateTimeOffset.FromUnixTimeSeconds(1700000000), TimeSpan.Zero, false));
        Assert.Throws<KeygateLicenseException>(() => verifier.Verify(
            token, "device-1", "other-product",
            DateTimeOffset.FromUnixTimeSeconds(1700000000), TimeSpan.Zero, false));
    }
}

public class IntegrityTests
{
    private static readonly byte[] ReleaseSeed = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
    private static readonly string ReleasePublicHex = Hex.Encode(Ed25519.PublicKeyFromSeed(ReleaseSeed));

    [Fact]
    public void SignedManifest_VerifiesAndDetectsModifiedAndMissingFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "keygate-int-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.dll"), "trusted");
            File.WriteAllText(Path.Combine(dir, "extra.txt"), "gone");

            var manifest = new IntegrityManifest
            {
                ManifestVersion = 1,
                ProductId = "prod-1",
                Version = "1.0.0",
                SigningKeyId = "release-1",
                Files = new List<IntegrityFileEntry>
                {
                    Entry(dir, "app.dll"),
                    Entry(dir, "extra.txt")
                }
            };
            SignedIntegrityEnvelope envelope = Sign(manifest);

            var verifier = new IntegrityVerifier(new Dictionary<string, string>
                { ["release-1"] = ReleasePublicHex });
            IntegrityManifest verified = verifier.VerifyEnvelope(envelope);
            IntegrityCheckResult ok = verifier.VerifyFiles(verified, dir);
            Assert.True(ok.IsValid);
            Assert.Equal(2, ok.PassedFiles);

            File.WriteAllText(Path.Combine(dir, "app.dll"), "evil!!");
            File.Delete(Path.Combine(dir, "extra.txt"));
            IntegrityCheckResult bad = verifier.VerifyFiles(verified, dir);
            Assert.False(bad.IsValid);
            Assert.Single(bad.ModifiedFiles);
            Assert.Single(bad.MissingFiles);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SignedManifest_RejectsUnknownKeyAndTraversal()
    {
        var manifest = new IntegrityManifest
        {
            ManifestVersion = 1,
            ProductId = "prod-1",
            Version = "1.0.0",
            SigningKeyId = "release-1",
            Files = new List<IntegrityFileEntry>
            {
                new IntegrityFileEntry { Path = "../escape.txt", Sha256 = new string('a', 64), Size = 1 }
            }
        };
        SignedIntegrityEnvelope envelope = Sign(manifest);

        var wrongKey = new IntegrityVerifier(new Dictionary<string, string> { ["other"] = ReleasePublicHex });
        Assert.Throws<KeygateProtocolException>(() => wrongKey.VerifyEnvelope(envelope));

        var verifier = new IntegrityVerifier(new Dictionary<string, string> { ["release-1"] = ReleasePublicHex });
        Assert.Throws<KeygateProtocolException>(() => verifier.VerifyEnvelope(envelope));

        IntegrityVerifier.ValidateRelativePath("dir/file.bin");
        Assert.Throws<KeygateProtocolException>(() => IntegrityVerifier.ValidateRelativePath("../x"));
        Assert.Throws<KeygateProtocolException>(() => IntegrityVerifier.ValidateRelativePath("a\\b"));
        Assert.Throws<KeygateProtocolException>(() => IntegrityVerifier.ValidateRelativePath("/abs"));
    }

    private static IntegrityFileEntry Entry(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        byte[] bytes = File.ReadAllBytes(path);
        return new IntegrityFileEntry
        {
            Path = name,
            Size = bytes.Length,
            Sha256 = Hex.Encode(SHA256.HashData(bytes))
        };
    }

    private static SignedIntegrityEnvelope Sign(IntegrityManifest manifest)
    {
        string payloadJson = JsonSerializer.Serialize(manifest);
        string payloadB64 = Base64Url.Encode(Encoding.UTF8.GetBytes(payloadJson));
        byte[] sig = Ed25519.Sign(ReleaseSeed, Encoding.UTF8.GetBytes(payloadB64));
        return new SignedIntegrityEnvelope
        {
            Format = IntegrityVerifier.FormatIdentifier,
            ManifestVersion = 1,
            Payload = payloadB64,
            Signature = Base64Url.Encode(sig),
            SigningKeyId = manifest.SigningKeyId,
            Algorithm = "ed25519"
        };
    }
}

public class SecurityEventTests
{
    [Fact]
    public void Sanitizer_DropsSecretsAndControlCharacters()
    {
        var input = new Dictionary<string, string>
        {
            ["license_key"] = "KG-SECRET",
            ["token"] = "abc",
            ["Authorization"] = "Bearer x",
            ["platform"] = "linux\nforged",
            ["multi.word-key"] = "ok",
            ["UPPER"] = "yes"
        };
        Dictionary<string, string> result = SecurityEventSigner.SanitizeDetails(input);
        Assert.False(result.ContainsKey("license_key"));
        Assert.False(result.ContainsKey("token"));
        Assert.False(result.ContainsKey("authorization"));
        Assert.Equal("linux forged", result["platform"]);
        Assert.Equal("ok", result["multi.word-key"]);
        Assert.False(result.ContainsKey("UPPER"));
    }

    [Fact]
    public void EventId_IsValidated()
    {
        Assert.True(SecurityEventSigner.IsValidEventId("evt-1.a_b"));
        Assert.False(SecurityEventSigner.IsValidEventId("evt 1"));
        Assert.False(SecurityEventSigner.IsValidEventId(new string('a', 129)));
    }
}

public class KeygateProtectionTests
{
    [Fact]
    public async Task ActivateValidateOfflineFallbackAndSignedReport()
    {
        var handler = new FakeKeygateHandler();
        var store = new MemorySecretStore();
        var options = new KeygateOptions
        {
            BaseUrl = "https://license.example.test",
            LicenseKey = "KG-TEST-KEY",
            ProductId = "prod-1",
            LicensePublicKey = handler.LicensePublicKeyHex,
            LicensePublicKeyId = "key_test",
            StorageDirectory = Path.Combine(Path.GetTempPath(), "kg-" + Guid.NewGuid().ToString("N")),
            SecretStore = store,
            HttpClient = new System.Net.Http.HttpClient(handler),
            HttpTimeout = TimeSpan.FromSeconds(5)
        };

        using var protection = new KeygateProtection(options);
        await protection.InitializeAsync();
        Assert.Matches("^kgid:v1:(darwin|windows|linux|other):[0-9a-f]{64}$", protection.DeviceId);
        Assert.Null(protection.CurrentLicense);

        ActivationResult activated = await protection.ActivateAsync();
        Assert.Equal("activated", activated.Status);
        Assert.NotNull(activated.License);
        Assert.Equal("prod-1", activated.License!.ProductId);
        Assert.True(protection.HasFeature("pro"));
        Assert.False(protection.HasFeature("enterprise"));
        Assert.NotNull(handler.LastDevicePublicKey);

        LicenseValidation online = await protection.ValidateLicenseAsync(forceOnline: true);
        Assert.True(online.IsValid);
        Assert.False(online.NetworkError);

        handler.FailVerifyNetwork = true;
        LicenseValidation offline = await protection.ValidateLicenseAsync(forceOnline: true);
        Assert.True(offline.IsValid, offline.Problem);
        Assert.True(offline.IsOffline);
        Assert.True(offline.NetworkError);
        Assert.Equal("NETWORK_UNAVAILABLE", offline.ProblemCode);

        SecurityReportResult report = await protection.ReportSecurityEventAsync(new SecurityEvent
        {
            Type = SecurityEventTypes.IntegrityViolation,
            Severity = "high",
            Details = new Dictionary<string, string>
            {
                ["source"] = "unit-test",
                ["license_key"] = "must-not-leak"
            }
        });
        Assert.True(report.Accepted);
        Assert.True(report.Verified);
        Assert.Equal("flag", report.ActionTaken);
        Assert.NotNull(handler.LastDetails);
        Assert.False(handler.LastDetails!.ContainsKey("license_key"));

        var saved = store.Values.Values.First();
        Assert.DoesNotContain("must-not-leak", saved);
    }

    [Fact]
    public async Task ActivateFailure_IsAnApiErrorNotANetworkError()
    {
        var handler = new FakeKeygateHandler { FailActivate = true };
        var options = new KeygateOptions
        {
            BaseUrl = "https://license.example.test",
            LicenseKey = "KG-TEST-KEY",
            LicensePublicKey = handler.LicensePublicKeyHex,
            LicensePublicKeyId = "key_test",
            SecretStore = new MemorySecretStore(),
            HttpClient = new System.Net.Http.HttpClient(handler)
        };
        using var protection = new KeygateProtection(options);
        KeygateApiException error = await Assert.ThrowsAsync<KeygateApiException>(
            () => protection.ActivateAsync());
        Assert.Equal("ACTIVATION_LIMIT", error.Code);
    }

    [Fact]
    public async Task NoLicense_ValidatesAsNotActivated()
    {
        var handler = new FakeKeygateHandler();
        var options = new KeygateOptions
        {
            BaseUrl = "https://license.example.test",
            LicensePublicKey = handler.LicensePublicKeyHex,
            SecretStore = new MemorySecretStore(),
            HttpClient = new System.Net.Http.HttpClient(handler)
        };
        using var protection = new KeygateProtection(options);
        LicenseValidation result = await protection.ValidateLicenseAsync();
        Assert.False(result.IsValid);
        Assert.Equal("NOT_ACTIVATED", result.ProblemCode);
    }
}
