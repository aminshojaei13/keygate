using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Keygate.Protection.Device;
using Keygate.Protection.Integrity;
using Keygate.Protection.Internal;
using Keygate.Protection.Licensing;
using Keygate.Protection.Security;
using Keygate.Protection.Storage;

namespace Keygate.Protection
{
    /// <summary>
    /// One object that hides the Keygate protocol from the host application:
    /// device identity, activation, signed offline validation, file integrity
    /// and tamper reporting.
    ///
    /// Typical console/desktop flow:
    /// <code>
    /// using var protection = new KeygateProtection(new KeygateOptions { BaseUrl = ... });
    /// await protection.InitializeAsync();
    /// if (protection.CurrentLicense == null)
    ///     await protection.ActivateAsync();          // first run only
    /// var check = await protection.ValidateLicenseAsync();
    /// if (!check.IsValid) return 1;
    /// var integrity = await protection.CheckIntegrityAsync();
    /// if (!integrity.IsValid) return 2;
    /// if (!protection.HasFeature("pro")) { ... }
    /// </code>
    ///
    /// This class is thread-safe for normal use. It never treats a network
    /// failure as tampering, never stores the license key in a report, and
    /// never sends the device private key anywhere.
    /// </summary>
    public sealed class KeygateProtection : IDisposable
    {
        private const long MaxFutureSkewSeconds = 10 * 60;
        private const long MaxPastSkewSeconds = 7 * 24 * 60 * 60;

        private readonly KeygateOptions _options;
        private readonly ProtectionStateRepository _repository;
        private readonly KeygateApi _api;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private ProtectionState _state = new ProtectionState();
        private DeviceIdentity _device = new DeviceIdentity();
        private byte[] _deviceSeed = Array.Empty<byte>();
        private byte[] _devicePublicKey = Array.Empty<byte>();
        private LicenseTokenVerifier? _verifier;
        private LicenseSnapshot? _snapshot;
        private string? _licenseKey;
        private DateTimeOffset _lastOnlineCheck = DateTimeOffset.MinValue;
        private bool _initialized;
        private bool _disposed;
        private Timer? _timer;

        public KeygateProtection(KeygateOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();

            IKeygateSecretStore store = _options.SecretStore
                ?? new FileSecretStore(_options.StorageDirectory);
            _repository = new ProtectionStateRepository(store);
            _api = new KeygateApi(_options);
        }

        /// <summary>The canonical device identifier sent to the server.</summary>
        public string DeviceId => _device.Id;

        /// <summary>Device identity metadata (never raw hardware values).</summary>
        public DeviceIdentity Device => _device;

        /// <summary>
        /// The last verified license snapshot, or null before activation /
        /// before a cached token has been verified. Read-only; call
        /// <see cref="ValidateLicenseAsync"/> to refresh it.
        /// </summary>
        public LicenseSnapshot? CurrentLicense => Volatile.Read(ref _snapshot);

        /// <summary>Raised after each periodic validation.</summary>
        public event EventHandler<LicenseValidation>? PeriodicValidationCompleted;

        /// <summary>
        /// Loads local state, derives the device ID and prepares the device's
        /// report-signing key. This is local-only: it does not require the
        /// network and does not fail when the app is not activated yet.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (_initialized) return;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized) return;

                _state = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
                bool dirty = false;

                _device = DeviceIdentityFactory.Create(
                    _options.DeviceId,
                    _state.FallbackDigest,
                    _options.IncludeHostnameInFingerprint);

                if (!string.Equals(_state.DeviceId, _device.Id, StringComparison.Ordinal))
                {
                    _state.DeviceId = _device.Id;
                    dirty = true;
                }
                if (!_device.IsHardwareDerived)
                {
                    string digest = ExtractDigest(_device.Id);
                    if (!string.Equals(_state.FallbackDigest, digest, StringComparison.Ordinal))
                    {
                        _state.FallbackDigest = digest;
                        dirty = true;
                    }
                }

                if (!TryLoadSeed(_state.DeviceSeed, out _deviceSeed))
                {
                    _deviceSeed = Ed25519.GenerateSeed();
                    _state.DeviceSeed = Base64Url.Encode(_deviceSeed);
                    dirty = true;
                }
                _devicePublicKey = Ed25519.PublicKeyFromSeed(_deviceSeed);

                _licenseKey = string.IsNullOrWhiteSpace(_options.LicenseKey)
                    ? _state.LicenseKey
                    : _options.LicenseKey!.Trim();
                if (!string.IsNullOrWhiteSpace(_options.LicenseKey) && _options.PersistLicenseKey)
                {
                    if (!string.Equals(_state.LicenseKey, _licenseKey, StringComparison.Ordinal))
                    {
                        _state.LicenseKey = _licenseKey;
                        dirty = true;
                    }
                }

                _verifier = BuildVerifierFromConfigurationOrCache();

                // Verify the cached offline token before exposing it. A
                // corrupted state file or a rotated key must not look like a
                // valid license.
                if (!string.IsNullOrEmpty(_state.Token) && _verifier != null && _verifier.HasKeys)
                {
                    try
                    {
                        _snapshot = _verifier.Verify(
                            _state.Token!, _device.Id, ExpectedProductId(), DateTimeOffset.UtcNow,
                            _options.ClockSkew, fromCache: true);
                    }
                    catch (KeygateException ex)
                    {
                        Log("cached token is not usable: " + ex.Message);
                        _snapshot = null;
                    }
                }

                if (dirty) await _repository.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
                _initialized = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Activates this device for the configured license key. Safe to call
        /// on every start: the server answers "already_activated" for the same
        /// device without consuming a second seat. A new activation registers
        /// the device's Ed25519 public key; the private half stays local.
        /// </summary>
        public async Task<ActivationResult> ActivateAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                string? licenseKey = CurrentLicenseKey();
                if (string.IsNullOrWhiteSpace(licenseKey))
                    throw new KeygateConfigurationException(
                        "No license key is configured. Set KeygateOptions.LicenseKey before activating.");

                _verifier = await EnsureVerifierAsync(allowFetch: true, cancellationToken).ConfigureAwait(false);

                var request = new Dictionary<string, object?>
                {
                    ["license_key"] = licenseKey,
                    ["identifier"] = _device.Id,
                    ["label"] = string.IsNullOrWhiteSpace(_options.DeviceLabel)
                        ? "C# SDK"
                        : _options.DeviceLabel,
                    ["device_public_key"] = Base64Url.Encode(_devicePublicKey),
                    ["device_key_alg"] = "ed25519"
                };

                ActivationResponse response = await _api.ActivateAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(response.Token))
                    throw new KeygateProtocolException("The activation response did not contain a token.");

                string? expectedProduct = string.IsNullOrWhiteSpace(_options.ProductId)
                    ? response.ProductId
                    : _options.ProductId;
                if (!string.IsNullOrWhiteSpace(_options.ProductId) &&
                    !string.IsNullOrWhiteSpace(response.ProductId) &&
                    !string.Equals(_options.ProductId, response.ProductId, StringComparison.Ordinal))
                {
                    throw new KeygateLicenseException(
                        "PRODUCT_MISMATCH",
                        "The server activated the license for a different product than configured.");
                }

                LicenseSnapshot snapshot = await VerifyTokenAsync(
                    response.Token, expectedProduct, allowRefresh: true, cancellationToken)
                    .ConfigureAwait(false);

                _snapshot = snapshot;
                _lastOnlineCheck = DateTimeOffset.UtcNow;
                await PersistAfterSuccessAsync(licenseKey!, response.Token, expectedProduct, cancellationToken)
                    .ConfigureAwait(false);

                return new ActivationResult
                {
                    Status = response.Status,
                    LicenseId = response.LicenseId,
                    ProductId = response.ProductId,
                    DeviceId = _device.Id,
                    Token = response.Token,
                    License = snapshot
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Validates the license online, falling back to the cached signed
        /// token when the network is unavailable. A network failure is never
        /// reported as tampering and never invalidates a still-usable cache.
        /// </summary>
        public async Task<LicenseValidation> ValidateLicenseAsync(
            bool forceOnline = false,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                string? licenseKey = CurrentLicenseKey();
                if (string.IsNullOrWhiteSpace(licenseKey))
                    return LicenseValidation.Invalid(
                        "NOT_ACTIVATED", "This installation has not been activated yet.");

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!forceOnline && _snapshot != null &&
                    _lastOnlineCheck != DateTimeOffset.MinValue &&
                    now - _lastOnlineCheck < _options.ValidationCacheDuration &&
                    SnapshotIsUsable(_snapshot, now))
                {
                    LicenseValidation cachedOk = ValidateSnapshot(_snapshot, now);
                    if (cachedOk.IsValid)
                    {
                        cachedOk.IsOffline = true;
                        return cachedOk;
                    }
                }

                try
                {
                    var request = new Dictionary<string, object?>
                    {
                        ["license_key"] = licenseKey,
                        ["identifier"] = _device.Id
                    };
                    VerifyResponse response = await _api.VerifyAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(response.Token))
                        throw new KeygateProtocolException("The verify response did not contain a token.");

                    string? expectedProduct = string.IsNullOrWhiteSpace(_options.ProductId)
                        ? response.ProductId
                        : _options.ProductId;
                    LicenseSnapshot snapshot = await VerifyTokenAsync(
                        response.Token, expectedProduct, allowRefresh: true, cancellationToken)
                        .ConfigureAwait(false);

                    LicenseValidation checkedNow = ValidateSnapshot(snapshot, now);
                    if (!checkedNow.IsValid) return checkedNow;

                    _snapshot = snapshot;
                    _lastOnlineCheck = now;
                    await PersistAfterSuccessAsync(licenseKey!, response.Token, expectedProduct, cancellationToken)
                        .ConfigureAwait(false);
                    return checkedNow;
                }
                catch (KeygateApiException apiError) when (IsDefinitiveLicenseError(apiError))
                {
                    // The server gave an authoritative answer: suspended,
                    // revoked, wrong device, unknown key. Do not mask it with
                    // the cached token.
                    return LicenseValidation.Invalid(apiError.Code, apiError.Message);
                }
                catch (KeygateApiException apiError)
                {
                    // 5xx, 429 and similar: the license is not the problem.
                    return OfflineFallback(apiError.Message);
                }
                catch (KeygateNetworkException networkError)
                {
                    return OfflineFallback(networkError.Message);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// True when the last verified license grants <paramref name="feature"/>.
        /// Feature values follow the server's entitlement rules: present,
        /// non-null and not explicitly false/zero/empty.
        /// </summary>
        public bool HasFeature(string feature)
        {
            if (string.IsNullOrWhiteSpace(feature)) return false;
            LicenseSnapshot? snapshot = Volatile.Read(ref _snapshot);
            if (snapshot == null) return false;
            if (!snapshot.IsUsableAt(DateTimeOffset.UtcNow)) return false;
            return snapshot.Features.TryGetValue(feature, out object? value) &&
                   LicenseTokenVerifier.FeatureIsEnabled(value);
        }

        /// <summary>True when the license permits installing a release published at this instant.</summary>
        public bool IsUpdateAllowed(DateTimeOffset publishedAt)
        {
            LicenseSnapshot? snapshot = Volatile.Read(ref _snapshot);
            return snapshot != null && snapshot.IsUsableAt(DateTimeOffset.UtcNow) &&
                   snapshot.AllowsUpdatePublishedAt(publishedAt);
        }

        /// <summary>
        /// Verifies the installed files against the signed manifest configured
        /// in <see cref="KeygateOptions.IntegrityManifestPath"/> or
        /// <see cref="KeygateOptions.IntegrityManifestUri"/>.
        ///
        /// A bad signature or modified file is returned as an invalid result,
        /// not an exception, so the app can keep running while it reports.
        /// Permission/locking errors are reported separately and are never
        /// treated as tampering.
        /// </summary>
        public async Task<IntegrityCheckResult> CheckIntegrityAsync(
            string? installDirectory = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (_options.ReleasePublicKeys.Count == 0)
                throw new KeygateConfigurationException(
                    "No release signing keys are configured. Pin them in KeygateOptions.ReleasePublicKeys.");

            var verifier = new IntegrityVerifier(_options.ReleasePublicKeys);
            string directory = installDirectory
                ?? _options.IntegrityInstallDirectory
                ?? AppContext.BaseDirectory;

            IntegrityCheckResult result = await verifier.CheckAsync(
                _options.IntegrityManifestPath,
                _options.IntegrityManifestUri,
                directory,
                null,
                cancellationToken).ConfigureAwait(false);

            if (!result.IsValid && _options.AutoReportIntegrityFailures)
            {
                if (!string.IsNullOrEmpty(result.ManifestError))
                {
                    await TryAutoReportAsync(
                        SecurityEventTypes.ManifestInvalid,
                        "high",
                        new Dictionary<string, string>
                        {
                            ["source"] = "sdk",
                            ["check"] = "integrity",
                            ["reason"] = "manifest_invalid"
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var details = new Dictionary<string, string>
                    {
                        ["source"] = "sdk",
                        ["check"] = "integrity",
                        ["missing"] = result.MissingFiles.Count.ToString(CultureInfo.InvariantCulture),
                        ["modified"] = result.ModifiedFiles.Count.ToString(CultureInfo.InvariantCulture),
                        ["errors"] = result.Errors.Count.ToString(CultureInfo.InvariantCulture),
                        ["total"] = result.TotalFiles.ToString(CultureInfo.InvariantCulture),
                        ["passed"] = result.PassedFiles.ToString(CultureInfo.InvariantCulture)
                    };
                    string first = result.MissingFiles.Concat(result.ModifiedFiles.Select(m => m.Path))
                        .FirstOrDefault() ?? "";
                    if (first.Length > 0) details["first_path"] = first;

                    await TryAutoReportAsync(
                        SecurityEventTypes.IntegrityViolation, "high", details, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return result;
        }

        /// <summary>
        /// Reports a tamper signal. The report is signed with the device key
        /// registered at activation and carries no secret. The server response
        /// says whether the report was accepted, whether it was a duplicate,
        /// and whether a policy now refuses the current operation.
        /// </summary>
        public async Task<SecurityReportResult> ReportSecurityEventAsync(
            SecurityEvent securityEvent,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            if (!_options.SecurityReportingEnabled)
                throw new KeygateSecurityEventException("Security reporting is disabled by configuration.");
            if (securityEvent == null) throw new ArgumentNullException(nameof(securityEvent));

            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            if (!SecurityEventTypes.IsKnown(securityEvent.Type))
                throw new KeygateSecurityEventException(
                    "Unsupported event type. Use one of SecurityEventTypes.");

            string eventId = string.IsNullOrWhiteSpace(securityEvent.EventId)
                ? SecurityEventFactory.NewEventId()
                : securityEvent.EventId!.Trim();
            if (!SecurityEventSigner.IsValidEventId(eventId))
                throw new KeygateSecurityEventException(
                    "event_id must be 1-128 characters from [A-Za-z0-9._-].");

            DateTimeOffset occurred = securityEvent.OccurredAt ?? DateTimeOffset.UtcNow;
            long occurredUnix = occurred.ToUnixTimeSeconds();
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (occurredUnix > nowUnix + MaxFutureSkewSeconds ||
                occurredUnix < nowUnix - MaxPastSkewSeconds)
            {
                throw new KeygateSecurityEventException(
                    "occurred_at is outside the server's accepted window (10 minutes future, 7 days past).");
            }

            string? licenseKey;
            LicenseSnapshot? snapshot;
            string deviceId;
            byte[] seed;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                licenseKey = CurrentLicenseKey();
                snapshot = _snapshot;
                deviceId = _device.Id;
                seed = _deviceSeed;
            }
            finally
            {
                _gate.Release();
            }

            if (string.IsNullOrWhiteSpace(licenseKey) || snapshot == null)
                throw new KeygateSecurityEventException(
                    "The license must be activated and validated before reporting security events.");

            Dictionary<string, string> details = SecurityEventSigner.SanitizeDetails(securityEvent.Details);
            byte[] canonical = SecurityEventSigner.CanonicalBytes(
                eventId, snapshot.LicenseId, deviceId, securityEvent.Type, occurredUnix);
            byte[] signature = Ed25519.Sign(seed, canonical);

            var body = new Dictionary<string, object?>
            {
                ["license_key"] = licenseKey,
                ["identifier"] = deviceId,
                ["event_id"] = eventId,
                ["event_type"] = securityEvent.Type,
                ["severity"] = NormalizeSeverity(securityEvent.Severity),
                ["occurred_at"] = occurredUnix,
                ["details"] = details,
                ["signature"] = Base64Url.Encode(signature)
            };

            ReportResponse response = await _api.ReportSecurityEventAsync(body, cancellationToken)
                .ConfigureAwait(false);

            return new SecurityReportResult
            {
                EventId = response.EventId,
                Accepted = response.Accepted,
                Duplicate = response.Duplicate,
                Verified = response.Verified,
                ActionTaken = response.ActionTaken,
                LicenseStatus = response.LicenseStatus,
                UnderReview = response.UnderReview,
                IsAllowed = response.IsAllowed,
                RetryAfter = response.RetryAfter
            };
        }

        /// <summary>
        /// Releases this device's activation slot and clears the locally
        /// cached token. The device key is kept so a later re-activation
        /// reuses the same identity instead of looking like a new machine.
        /// </summary>
        public async Task DeactivateAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string? licenseKey = CurrentLicenseKey();
                if (string.IsNullOrWhiteSpace(licenseKey))
                    throw new KeygateConfigurationException("No license key is configured.");

                await _api.DeactivateAsync(new Dictionary<string, object?>
                {
                    ["license_key"] = licenseKey,
                    ["identifier"] = _device.Id
                }, cancellationToken).ConfigureAwait(false);

                _snapshot = null;
                _state.Token = null;
                _state.LicenseKey = null;
                _licenseKey = null;
                await _repository.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Starts background revalidation. The first check runs after one
        /// interval. Call <see cref="StopPeriodicValidation"/> (or dispose) to
        /// stop it. Network failures fall back to the cached token exactly
        /// like <see cref="ValidateLicenseAsync"/>.
        /// </summary>
        public void StartPeriodicValidation(TimeSpan? interval = null)
        {
            ThrowIfDisposed();
            TimeSpan period = interval ?? _options.PeriodicValidationInterval;
            if (period <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");

            StopPeriodicValidation();
            _timer = new Timer(async _ =>
            {
                try
                {
                    LicenseValidation result = await ValidateLicenseAsync(forceOnline: true)
                        .ConfigureAwait(false);
                    PeriodicValidationCompleted?.Invoke(this, result);
                }
                catch (Exception ex)
                {
                    Log("periodic validation failed: " + ex.Message);
                }
            }, null, period, period);
        }

        /// <summary>Stops the background timer started by <see cref="StartPeriodicValidation"/>.</summary>
        public void StopPeriodicValidation()
        {
            Timer? timer = Interlocked.Exchange(ref _timer, null);
            if (timer != null) timer.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopPeriodicValidation();
            _api.Dispose();
            _gate.Dispose();
        }

        // ─── internals ───

        private LicenseValidation OfflineFallback(string networkMessage)
        {
            LicenseSnapshot? snapshot = _snapshot;
            if (snapshot == null && _verifier != null && _verifier.HasKeys &&
                !string.IsNullOrEmpty(_state.Token))
            {
                try
                {
                    snapshot = _verifier.Verify(
                        _state.Token!, _device.Id, ExpectedProductId(), DateTimeOffset.UtcNow,
                        _options.ClockSkew, fromCache: true);
                    _snapshot = snapshot;
                }
                catch (KeygateException ex)
                {
                    Log("cached token could not be verified offline: " + ex.Message);
                }
            }

            if (snapshot != null && SnapshotIsUsable(snapshot, DateTimeOffset.UtcNow))
            {
                LicenseValidation valid = ValidateSnapshot(snapshot, DateTimeOffset.UtcNow);
                if (valid.IsValid)
                {
                    valid.IsOffline = true;
                    valid.NetworkError = true;
                    valid.ProblemCode = "NETWORK_UNAVAILABLE";
                    valid.Problem = networkMessage;
                    return valid;
                }
            }

            return new LicenseValidation
            {
                IsValid = false,
                IsOffline = true,
                NetworkError = true,
                ProblemCode = "NETWORK_UNAVAILABLE",
                Problem = networkMessage,
                License = snapshot
            };
        }

        private LicenseValidation ValidateSnapshot(LicenseSnapshot snapshot, DateTimeOffset now)
        {
            if (!snapshot.IsUsableAt(now))
                return LicenseValidation.Invalid(
                    "OFFLINE_TOKEN_EXPIRED",
                    "The cached license token has expired; connect to the server to refresh it.");

            foreach (string feature in _options.RequiredFeatures)
            {
                if (!snapshot.Features.TryGetValue(feature, out object? value) ||
                    !LicenseTokenVerifier.FeatureIsEnabled(value))
                {
                    return LicenseValidation.Invalid(
                        "MISSING_FEATURE", "The license does not include the required feature: " + feature);
                }
            }

            return new LicenseValidation
            {
                IsValid = true,
                License = snapshot,
                CheckedAt = now
            };
        }

        private static bool SnapshotIsUsable(LicenseSnapshot? snapshot, DateTimeOffset now) =>
            snapshot != null && snapshot.IsUsableAt(now);

        private async Task<LicenseSnapshot> VerifyTokenAsync(
            string token, string? expectedProductId, bool allowRefresh, CancellationToken cancellationToken)
        {
            LicenseTokenVerifier? verifier = _verifier ??
                                               await EnsureVerifierAsync(allowFetch: true, cancellationToken)
                                                   .ConfigureAwait(false);
            if (verifier == null || !verifier.HasKeys)
                throw new KeygateLicenseException(
                    "TOKEN_NO_KEY", "No license verification key is available.");

            try
            {
                return verifier.Verify(
                    token, _device.Id, expectedProductId, DateTimeOffset.UtcNow,
                    _options.ClockSkew, fromCache: false);
            }
            catch (KeygateLicenseException ex) when (
                allowRefresh &&
                ex.Code == "TOKEN_UNKNOWN_KEY" &&
                HasNoPinnedKey())
            {
                // Signing key rotated since this install cached the old one.
                // Fetch the current key once and retry; a pinned deployment
                // skips this entirely.
                await FetchPublicKeyAsync(cancellationToken).ConfigureAwait(false);
                verifier = _verifier;
                if (verifier == null || !verifier.HasKeys)
                    throw;
                return verifier.Verify(
                    token, _device.Id, expectedProductId, DateTimeOffset.UtcNow,
                    _options.ClockSkew, fromCache: false);
            }
        }

        private async Task<LicenseTokenVerifier?> EnsureVerifierAsync(
            bool allowFetch, CancellationToken cancellationToken)
        {
            if (_verifier != null) return _verifier;

            _verifier = BuildVerifierFromConfigurationOrCache();
            if (_verifier != null && _verifier.HasKeys) return _verifier;

            if (!allowFetch) return null;

            await FetchPublicKeyAsync(cancellationToken).ConfigureAwait(false);
            return _verifier;
        }

        private LicenseTokenVerifier? BuildVerifierFromConfigurationOrCache()
        {
            if (HasNoPinnedKey() && _options.LicensePublicKeys.Count == 0 &&
                string.IsNullOrEmpty(_state.PublicKey))
                return null;

            var extra = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in _options.LicensePublicKeys) extra[pair.Key] = pair.Value;

            if (!HasNoPinnedKey())
                return new LicenseTokenVerifier(_options.LicensePublicKey, _options.LicensePublicKeyId, extra);

            if (!string.IsNullOrEmpty(_state.PublicKey))
            {
                if (!Ed25519.TryParsePublicKey(_state.PublicKey!, out _))
                {
                    Log("cached license public key is malformed; it will be refetched.");
                    return null;
                }
                string kid = string.IsNullOrEmpty(_state.PublicKeyId) ? "cached" : _state.PublicKeyId!;
                extra[kid] = _state.PublicKey!;
                return new LicenseTokenVerifier(null, null, extra);
            }

            return null;
        }

        private async Task FetchPublicKeyAsync(CancellationToken cancellationToken)
        {
            PublicKeyResponse key = await _api.GetLicensePublicKeyAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key.Algorithm) &&
                !string.Equals(key.Algorithm, "ed25519", StringComparison.OrdinalIgnoreCase))
            {
                throw new KeygateProtocolException(
                    "The server advertises an unsupported license key algorithm: " + key.Algorithm);
            }
            if (!Ed25519.TryParsePublicKey(key.PublicKey, out _))
                throw new KeygateProtocolException("The server returned an invalid license public key.");

            _state.PublicKey = key.PublicKey;
            _state.PublicKeyId = key.Kid;
            var extra = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [string.IsNullOrEmpty(key.Kid) ? "cached" : key.Kid] = key.PublicKey
            };
            foreach (var pair in _options.LicensePublicKeys) extra[pair.Key] = pair.Value;
            _verifier = new LicenseTokenVerifier(null, null, extra);
        }

        private bool HasNoPinnedKey() => string.IsNullOrWhiteSpace(_options.LicensePublicKey);

        private async Task PersistAfterSuccessAsync(
            string licenseKey, string token, string? productId, CancellationToken cancellationToken)
        {
            _state.Token = token;
            if (_options.PersistLicenseKey) _state.LicenseKey = licenseKey;
            else _state.LicenseKey = null;
            _state.DeviceId = _device.Id;
            _state.DeviceSeed = Base64Url.Encode(_deviceSeed);
            if (!string.IsNullOrWhiteSpace(productId) && string.IsNullOrWhiteSpace(_options.ProductId))
                _state.ProductId = productId;
            await _repository.SaveAsync(_state, cancellationToken).ConfigureAwait(false);
        }

        private string? ExpectedProductId()
        {
            if (!string.IsNullOrWhiteSpace(_options.ProductId)) return _options.ProductId;
            if (!string.IsNullOrWhiteSpace(_state.ProductId)) return _state.ProductId;
            return null;
        }

        private string? CurrentLicenseKey()
        {
            if (!string.IsNullOrWhiteSpace(_options.LicenseKey)) return _options.LicenseKey!.Trim();
            if (!string.IsNullOrWhiteSpace(_licenseKey)) return _licenseKey;
            return _state.LicenseKey;
        }

        private async Task TryAutoReportAsync(
            string type, string severity, IDictionary<string, string> details,
            CancellationToken cancellationToken)
        {
            try
            {
                if (_snapshot == null && _state.Token == null) return;
                if (string.IsNullOrWhiteSpace(CurrentLicenseKey())) return;
                await ReportSecurityEventAsync(new SecurityEvent
                {
                    Type = type,
                    Severity = severity,
                    Details = details
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (KeygateException ex)
            {
                // Reporting is best-effort. A failure here (offline laptop,
                // rate limit) must not turn into a failed integrity check or
                // an application crash.
                Log("security report was not delivered: " + ex.Message);
            }
        }

        private static bool IsDefinitiveLicenseError(KeygateApiException error)
        {
            int status = (int)error.StatusCode;
            if (status == 400 || status == 401 || status == 403 || status == 404 ||
                status == 409 || status == 410 || status == 422)
                return true;
            return false;
        }

        private static string NormalizeSeverity(string? severity)
        {
            switch ((severity ?? "").Trim().ToLowerInvariant())
            {
                case "low": return "low";
                case "high": return "high";
                case "critical": return "critical";
                default: return "medium";
            }
        }

        private static bool TryLoadSeed(string? encoded, out byte[] seed)
        {
            seed = Array.Empty<byte>();
            if (string.IsNullOrWhiteSpace(encoded)) return false;
            try
            {
                byte[] decoded = Base64Url.Decode(encoded!);
                if (decoded.Length != Ed25519.SeedSize) return false;
                seed = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string ExtractDigest(string kgid)
        {
            int colon = kgid.LastIndexOf(':');
            return colon >= 0 && colon + 1 < kgid.Length ? kgid.Substring(colon + 1) : kgid;
        }

        private void Log(string message)
        {
            try { _options.Log?.Invoke(message); } catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(KeygateProtection));
        }
    }
}
