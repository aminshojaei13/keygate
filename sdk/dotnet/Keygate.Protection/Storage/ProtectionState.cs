using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Keygate.Protection.Storage
{
    /// <summary>
    /// Local client state. Everything in here is either a secret or a value
    /// derived from one, so it is written through <see cref="IKeygateSecretStore"/>
    /// and never logged.
    /// </summary>
    internal sealed class ProtectionState
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
        /// <summary>
        /// 64-hex fallback digest for machines with no readable hardware
        /// identifiers. Set only when the device ID is not hardware-derived.
        /// </summary>
        [JsonPropertyName("fallback_digest")] public string? FallbackDigest { get; set; }
        /// <summary>base64url Ed25519 seed. Never leaves this machine.</summary>
        [JsonPropertyName("device_seed")] public string? DeviceSeed { get; set; }
        [JsonPropertyName("license_key")] public string? LicenseKey { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("public_key")] public string? PublicKey { get; set; }
        [JsonPropertyName("public_key_id")] public string? PublicKeyId { get; set; }
        [JsonPropertyName("product_id")] public string? ProductId { get; set; }
        [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }

        public string Serialize() => JsonSerializer.Serialize(this, Options);

        public static ProtectionState Deserialize(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new ProtectionState();
            try
            {
                return JsonSerializer.Deserialize<ProtectionState>(json!, Options)
                       ?? new ProtectionState();
            }
            catch (JsonException)
            {
                // A corrupted state file is recoverable: the user
                // re-activates. Failing closed here would brick the app.
                return new ProtectionState();
            }
        }
    }

    internal sealed class ProtectionStateRepository
    {
        public const string StateName = "state";
        private readonly IKeygateSecretStore _store;

        public ProtectionStateRepository(IKeygateSecretStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public async Task<ProtectionState> LoadAsync(CancellationToken cancellationToken)
        {
            string? json = await _store.ReadAsync(StateName, cancellationToken).ConfigureAwait(false);
            return ProtectionState.Deserialize(json);
        }

        public Task SaveAsync(ProtectionState state, CancellationToken cancellationToken)
        {
            state.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return _store.WriteAsync(StateName, state.Serialize(), cancellationToken);
        }

        public Task ClearAsync(CancellationToken cancellationToken) =>
            _store.DeleteAsync(StateName, cancellationToken);
    }
}
