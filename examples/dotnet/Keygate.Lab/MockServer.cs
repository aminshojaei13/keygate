using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keygate.Lab;

/// <summary>
/// A miniature, contract-level Keygate server used when --server is omitted.
/// It implements exactly the public SDK endpoints the lab exercises:
///
///   GET  /api/v1/license/pubkey
///   POST /api/v1/license/activate
///   POST /api/v1/license/verify
///   POST /api/v1/license/deactivate
///
/// plus one mock-only endpoint that stands in for the admin action
/// "reset device binding":
///
///   POST /api/v1/lab/reset-binding
///
/// Device binding mirrors internal/service/license.go:
///   strict   — one device per licence, no migration, no self-service deactivate
///   tolerant — seat-counted activations, optional previous_identifier migration
///
/// Tokens are real Ed25519 tokens with the same fields, signing input and
/// fingerprint as internal/license/token.go, so the client-side verification
/// path is identical to the real server's.
public sealed class MockServer : IDisposable
{
    private readonly MiniHttpServer _http;
    private readonly string _statePath;
    private readonly object _gate = new();
    private readonly MockServerState _state;

    public MockServerState State => _state;
    public string Url => _http.Url;

    public MockServer(string statePath, MockServerState state)
    {
        _statePath = statePath;
        _state = state;
        _http = new MiniHttpServer(Handle);
    }

    public void Dispose() => _http.Dispose();

    private void Save() => MockServerStore.Save(_statePath, _state);

    private MiniResponse Handle(MiniRequest request)
    {
        try
        {
            var path = request.Path.TrimEnd('/');

            if (request.Method == "GET" && path == "/health")
                return Json(200, new { status = "ok" });

            if (request.Method == "GET" && path == "/api/v1/license/pubkey")
                return PublicKey();

            if (request.Method == "POST" && path == "/api/v1/license/activate")
                return Activate(request);

            if (request.Method == "POST" && path == "/api/v1/license/verify")
                return Verify(request);

            if (request.Method == "POST" && path == "/api/v1/license/deactivate")
                return Deactivate(request);

            if (request.Method == "POST" && path == "/api/v1/lab/reset-binding")
                return ResetBinding();

            return Err(404, "NOT_FOUND", $"no mock route for {request.Method} {request.Path}");
        }
        catch (JsonException)
        {
            return Err(400, "BAD_REQUEST", "request body is not valid JSON.");
        }
        catch (LabException ex)
        {
            return Err(400, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return Err(500, "INTERNAL_ERROR", ex.Message);
        }
    }

    private MiniResponse PublicKey()
    {
        string publicKey;
        string kid;
        lock (_gate)
        {
            publicKey = MockServerStore.PublicKeyHex(_state);
            kid = MockServerStore.KeyId(publicKey);
        }

        return Ok(new
        {
            kid,
            algorithm = "ed25519",
            public_key = publicKey,
            format = "hex",
            keys = new[]
            {
                new { kid, algorithm = "ed25519", public_key = publicKey, format = "hex", status = "active" },
            },
        }, new Dictionary<string, string> { ["Cache-Control"] = "public, max-age=3600" });
    }

    private MiniResponse Activate(MiniRequest request)
    {
        var input = ParseInput(request);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (!MatchesKey(input.LicenseKey) || !MockServerStore.IsUsable(_state.Status))
                return Err(404, "LICENSE_NOT_FOUND", "license not found");

            var existing = FindActivation(input.Identifier);
            if (existing is not null)
            {
                existing.LastSeenAt = now;
                Save();
                return Ok(new
                {
                    status = "already_activated",
                    license_id = _state.LicenseId,
                    token = IssueToken(input.Identifier),
                });
            }

            if (MockServerStore.IsStrict(_state))
            {
                // A hardware-derived identifier is the whole premise of strict
                // binding: a disk-stored UUID cannot notice a new machine.
                if (!DeviceIdentity.IsHardwareScheme(input.Identifier))
                {
                    return Err(
                        400, "IDENTIFIER_SCHEME_UNSUPPORTED",
                        "strict device binding requires a hardware-derived identifier",
                        new
                        {
                            required_schemes = new[] { "kgid:v1:<platform>:<64hex>", "sha256:<64hex>" },
                        });
                }

                if (!string.IsNullOrWhiteSpace(input.PreviousIdentifier) &&
                    !string.Equals(input.PreviousIdentifier, input.Identifier, StringComparison.Ordinal))
                {
                    return Err(
                        409, "DEVICE_CHANGED",
                        "device characteristics changed; this license is bound to another device",
                        new { reason = "migration_disabled", action = "contact_support" });
                }

                if (_state.Activations.Count > 0)
                {
                    return Err(
                        409, "DEVICE_CHANGED",
                        "device characteristics changed; this license is bound to another device",
                        new { reason = "different_device", action = "contact_support" });
                }
            }
            else if (!string.IsNullOrWhiteSpace(input.PreviousIdentifier) &&
                     !string.Equals(input.PreviousIdentifier, input.Identifier, StringComparison.Ordinal))
            {
                // Tolerant mode migrates in place: no extra seat is consumed.
                var previous = FindActivation(input.PreviousIdentifier);
                if (previous is not null)
                {
                    previous.Identifier = input.Identifier;
                    previous.Label = input.Label ?? previous.Label;
                    previous.LastSeenAt = now;
                    Save();
                    return Ok(new
                    {
                        status = "activated",
                        license_id = _state.LicenseId,
                        token = IssueToken(input.Identifier),
                    });
                }
            }

            var max = MockServerStore.IsStrict(_state) ? 1 : _state.MaxActivations;
            if (_state.Activations.Count >= max)
            {
                if (MockServerStore.IsStrict(_state))
                {
                    return Err(
                        409, "DEVICE_CHANGED",
                        "device characteristics changed; this license is bound to another device",
                        new { reason = "different_device", action = "contact_support" });
                }

                return Err(
                    409, "ACTIVATION_LIMIT", "maximum activations reached",
                    new { max, current = _state.Activations.Count });
            }

            _state.Activations.Add(new MockActivation
            {
                Identifier = input.Identifier,
                IdentifierType = input.IdentifierType,
                Label = input.Label ?? "",
                CreatedAt = now,
                LastSeenAt = now,
            });
            Save();

            return Ok(new
            {
                status = "activated",
                license_id = _state.LicenseId,
                token = IssueToken(input.Identifier),
            });
        }
    }

    private MiniResponse Verify(MiniRequest request)
    {
        var input = ParseInput(request);
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (!MatchesKey(input.LicenseKey) || !MockServerStore.IsUsable(_state.Status))
                return Err(404, "LICENSE_NOT_FOUND", "license not found");

            var activation = FindActivation(input.Identifier);
            // Same anti-oracle rule as Verify(): an unknown device looks like
            // an unknown licence.
            if (activation is null)
                return Err(404, "LICENSE_NOT_FOUND", "license not found");

            activation.LastSeenAt = now;
            Save();

            return Ok(new
            {
                status = _state.Status,
                plan_id = _state.PlanId,
                plan_name = _state.PlanName,
                valid_until = _state.ValidUntil,
                updates_until = _state.UpdatesUntil,
                features = _state.Features,
                token = IssueToken(input.Identifier),
                grace_days = MockServerStore.EffectiveGrace(_state),
            });
        }
    }

    private MiniResponse Deactivate(MiniRequest request)
    {
        var input = ParseInput(request);

        lock (_gate)
        {
            if (!MatchesKey(input.LicenseKey))
                return Err(404, "LICENSE_NOT_FOUND", "license not found");

            var activation = FindActivation(input.Identifier);
            if (activation is null)
                return Err(404, "LICENSE_NOT_FOUND", "license not found");

            // Strict binding removes the self-service reset: otherwise a
            // client could release its slot and activate another machine.
            if (MockServerStore.IsStrict(_state))
            {
                return Err(
                    403, "DEACTIVATION_DISABLED",
                    "strict device binding is enabled; an administrator must release this device");
            }

            _state.Activations.Remove(activation);
            Save();
            return Ok(new { status = "deactivated" });
        }
    }

    private MiniResponse ResetBinding()
    {
        lock (_gate)
        {
            var freed = _state.Activations.Count;
            _state.Activations.Clear();
            Save();
            return Ok(new { status = "reset", freed });
        }
    }

    private MockActivation? FindActivation(string identifier) =>
        _state.Activations.FirstOrDefault(
            activation => string.Equals(activation.Identifier, identifier, StringComparison.Ordinal));

    private bool MatchesKey(string licenseKey) =>
        string.Equals(licenseKey, _state.LicenseKey, StringComparison.Ordinal);

    private static ActivationInput ParseInput(MiniRequest request)
    {
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;

        var key = GetString(root, "license_key");
        var identifier = GetString(root, "identifier");
        var label = GetString(root, "label");
        var type = GetString(root, "identifier_type");
        var previous = GetString(root, "previous_identifier");

        if (key.Length == 0 || identifier.Length == 0)
            throw new LabException("BAD_REQUEST", "license_key and identifier are required.");

        if (identifier.Length > 256)
            throw new LabException("BAD_REQUEST", "identifier too long (max 256).");

        // The real handler rejects padded identifiers instead of trimming,
        // so two spellings cannot become two rows (and two seats).
        if (!string.Equals(identifier, identifier.Trim(), StringComparison.Ordinal) ||
            identifier.Any(char.IsControl))
        {
            throw new LabException("BAD_REQUEST", "identifier must not be padded or contain control characters.");
        }

        if (type.Length > 0 && type is not ("device" or "user"))
            throw new LabException("BAD_REQUEST", "invalid identifier_type, must be device or user");

        return new ActivationInput(
            key,
            identifier,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            type.Length == 0 ? "device" : type,
            string.IsNullOrWhiteSpace(previous) ? null : previous.Trim());
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private string IssueToken(string identifier)
    {
        var now = DateTimeOffset.UtcNow;
        var issuedAt = now.ToUnixTimeSeconds();
        var expiresAt = issuedAt + (long)_state.TokenTtlDays * 86400;

        var grace = MockServerStore.EffectiveGrace(_state);
        var validUntil = 0L;
        if (_state.ValidUntil is { } deadline)
        {
            validUntil = deadline.ToUnixTimeSeconds();
            // exp never outlives the licence: same clamp as internal/service/license.go.
            var licenceDeadline = validUntil + (long)grace * 86400;
            if (licenceDeadline < expiresAt) expiresAt = licenceDeadline;
        }

        var payload = new TokenPayloadOut
        {
            Version = "1",
            KeyId = MockServerStore.KeyId(MockServerStore.PublicKeyHex(_state)),
            LicenseId = _state.LicenseId,
            ProductId = _state.ProductId,
            PlanId = _state.PlanId,
            Status = _state.Status,
            Identifier = identifier,
            Features = _state.Features.Count > 0 ? _state.Features : null,
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
            ValidUntil = validUntil,
            UpdatesUntil = _state.UpdatesUntil?.ToUnixTimeSeconds() ?? 0,
            GraceDays = grace,
            Nonce = Base64Url.Encode(RandomBytes(16)),
            Fingerprint = OfflineToken.Fingerprint(identifier, _state.ProductId),
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, LabJson.Compact);
        var payloadB64 = Base64Url.Encode(payloadBytes);
        var seed = HexCodec.Decode(_state.SeedHex);
        var signature = Ed25519.Sign(seed, Encoding.ASCII.GetBytes(payloadB64));
        return payloadB64 + "." + Base64Url.Encode(signature);
    }

    private static byte[] RandomBytes(int size) =>
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(size);

    private static MiniResponse Ok(object data, Dictionary<string, string>? headers = null) =>
        Json(200, new { success = true, data }, headers);

    private static MiniResponse Err(int status, string code, string message, object? details = null) =>
        Json(status, new { success = false, error = new { code, message, details } });

    private static MiniResponse Json(int status, object body, Dictionary<string, string>? headers = null)
    {
        var merged = new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
        };
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
                merged[key] = value;
        }

        return new MiniResponse
        {
            Status = status,
            Body = LabJson.ToUtf8(body),
            Headers = merged,
        };
    }

    private readonly record struct ActivationInput(
        string LicenseKey,
        string Identifier,
        string? Label,
        string IdentifierType,
        string? PreviousIdentifier);

    private sealed class TokenPayloadOut
    {
        [JsonPropertyName("ver")] public string Version { get; set; } = "1";
        [JsonPropertyName("kid")] public string KeyId { get; set; } = "";
        [JsonPropertyName("lid")] public string LicenseId { get; set; } = "";
        [JsonPropertyName("pid")] public string ProductId { get; set; } = "";
        [JsonPropertyName("pln")] public string PlanId { get; set; } = "";
        [JsonPropertyName("sts")] public string Status { get; set; } = "";
        [JsonPropertyName("did")] public string Identifier { get; set; } = "";

        [JsonPropertyName("ftr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object?>? Features { get; set; }

        [JsonPropertyName("iat")] public long IssuedAt { get; set; }
        [JsonPropertyName("exp")] public long ExpiresAt { get; set; }

        [JsonPropertyName("vun"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long ValidUntil { get; set; }

        [JsonPropertyName("upd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long UpdatesUntil { get; set; }

        [JsonPropertyName("grc")] public int GraceDays { get; set; }
        [JsonPropertyName("nce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("fpr")] public string Fingerprint { get; set; } = "";
    }
}
