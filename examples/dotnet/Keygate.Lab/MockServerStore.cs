using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keygate.Lab;

public sealed class MockActivation
{
    public string Identifier { get; set; } = "";
    public string IdentifierType { get; set; } = "device";
    public string Label { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// State of the built-in, contract-level mock of Keygate's public licence API.
/// It is intentionally small: one product, one plan, one licence. That is all
/// the scenarios need, and it keeps the lab runnable with no Go server, no
/// PostgreSQL and no network.
/// </summary>
public sealed class MockServerState
{
    public string SeedHex { get; set; } = "";
    public string ProductId { get; set; } = "prod_lab";
    public string PlanId { get; set; } = "plan_lab";
    public string PlanName { get; set; } = "Lab Pro";
    public string LicenseId { get; set; } = "lic_lab";
    public string LicenseKey { get; set; } = "KG-LAB0-DEMO-0000-0001";
    public string Status { get; set; } = "active";

    /// <summary>
    /// "strict" (server default: one device per licence, no self-service
    /// deactivate) or "tolerant" (seat-counted activations, client deactivate).
    /// </summary>
    public string DeviceBinding { get; set; } = "strict";

    public int MaxActivations { get; set; } = 1;
    public int GraceDays { get; set; } = 7;
    public int TokenTtlDays { get; set; } = 7;
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? UpdatesUntil { get; set; }
    public Dictionary<string, object?> Features { get; set; } = new();
    public List<MockActivation> Activations { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

public static class MockServerStore
{
    public const string Strict = "strict";
    public const string Tolerant = "tolerant";

    public static MockServerState LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            var state = LabJson.Read<MockServerState>(path);
            if (Normalize(state))
                Save(path, state);
            return state;
        }

        var created = NewState();
        Save(path, created);
        return created;
    }

    public static MockServerState? TryLoad(string path) =>
        File.Exists(path) ? LoadOrCreate(path) : null;

    public static void Save(string path, MockServerState state) => LabJson.Write(path, state);

    public static MockServerState Reset(
        string path,
        int? maxActivations = null,
        bool rotateKey = false,
        int? validInDays = null,
        string? status = null,
        int? tokenTtlDays = null,
        int? graceDays = null,
        string? deviceBinding = null)
    {
        var state = LoadOrCreate(path);

        if (rotateKey)
            state.SeedHex = LabUtil.RandomHex(32);
        if (maxActivations.HasValue)
            state.MaxActivations = maxActivations.Value;
        if (tokenTtlDays.HasValue)
            state.TokenTtlDays = tokenTtlDays.Value;
        if (graceDays.HasValue)
            state.GraceDays = graceDays.Value;
        if (!string.IsNullOrWhiteSpace(status))
            state.Status = status.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(deviceBinding))
            state.DeviceBinding = NormalizeBinding(deviceBinding);

        state.Activations.Clear();
        state.ValidUntil = validInDays.HasValue
            ? DateTimeOffset.UtcNow.AddDays(validInDays.Value)
            : null;
        state.UpdatesUntil = null;

        Save(path, state);
        return state;
    }

    /// <summary>
    /// The admin action POST /admin/licenses/:id/reset-device-binding, reduced
    /// to the state change the public SDK can observe: every live activation is
    /// released, so the next activation binds a new device.
    /// </summary>
    public static int ResetDeviceBinding(string path)
    {
        var state = LoadOrCreate(path);
        var freed = state.Activations.Count;
        state.Activations.Clear();
        Save(path, state);
        return freed;
    }

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public static bool IsStrict(MockServerState state) =>
        NormalizeBinding(state.DeviceBinding) == Strict;

    public static string PublicKeyHex(MockServerState state)
    {
        var seed = HexCodec.Decode(state.SeedHex);
        return HexCodec.Encode(Ed25519.PublicKeyFromSeed(seed));
    }

    /// <summary>Mirrors license.KeyIDFromPublicKey: "key_" + first 8 SHA-256 bytes.</summary>
    public static string KeyId(string publicKeyHex)
    {
        var publicKey = HexCodec.Decode(publicKeyHex);
        var hash = SHA256.HashData(publicKey);
        return "key_" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static bool IsUsable(string status) =>
        status is "active" or "trialing" or "past_due";

    public static int EffectiveGrace(MockServerState state) =>
        IsUsable(state.Status) ? state.GraceDays : 0;

    public static string FeatureString(object? value) => value switch
    {
        null => "null",
        JsonElement element => OfflineToken.FormatFeature(element),
        _ => value.ToString() ?? "",
    };

    private static string NormalizeBinding(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            Strict or "true" or "on" => Strict,
            Tolerant or "false" or "off" => Tolerant,
            _ => throw new LabException(
                "INVALID_USAGE", "--device-binding must be either 'strict' or 'tolerant'."),
        };

    private static MockServerState NewState() => new()
    {
        SeedHex = LabUtil.RandomHex(32),
        CreatedAt = DateTimeOffset.UtcNow,
        Features = new Dictionary<string, object?>
        {
            ["export"] = true,
            ["max_projects"] = 3,
            ["seats"] = 5,
        },
    };

    /// <summary>Repair hand-edited state so a typo cannot crash the mock.</summary>
    private static bool Normalize(MockServerState state)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(state.SeedHex) || state.SeedHex.Length != 64)
        {
            state.SeedHex = LabUtil.RandomHex(32);
            changed = true;
        }
        else
        {
            try
            {
                HexCodec.Decode(state.SeedHex);
            }
            catch (LabException)
            {
                state.SeedHex = LabUtil.RandomHex(32);
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(state.ProductId)) { state.ProductId = "prod_lab"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.PlanId)) { state.PlanId = "plan_lab"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.PlanName)) { state.PlanName = "Lab Pro"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.LicenseId)) { state.LicenseId = "lic_lab"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.LicenseKey)) { state.LicenseKey = "KG-LAB0-DEMO-0000-0001"; changed = true; }
        if (string.IsNullOrWhiteSpace(state.Status)) { state.Status = "active"; changed = true; }

        if (state.DeviceBinding is not (Strict or Tolerant))
        {
            state.DeviceBinding = Strict;
            changed = true;
        }

        if (state.MaxActivations < 0) { state.MaxActivations = 0; changed = true; }
        if (state.GraceDays < 0) { state.GraceDays = 0; changed = true; }
        if (state.TokenTtlDays <= 0) { state.TokenTtlDays = 7; changed = true; }

        state.Activations ??= new List<MockActivation>();
        state.Features ??= new Dictionary<string, object?>();
        if (state.CreatedAt == default)
        {
            state.CreatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        return changed;
    }
}
