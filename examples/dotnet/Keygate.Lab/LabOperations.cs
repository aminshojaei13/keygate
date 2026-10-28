namespace Keygate.Lab;

public sealed class ActivateOutcome
{
    public required ActivateResult Result { get; init; }
    public required TokenPayload Token { get; init; }
}

public sealed class VerifyOutcome
{
    public required VerifyResult Result { get; init; }
    public required TokenPayload Token { get; init; }
}

public sealed class StartResult
{
    public required LicenseState License { get; init; }
    public required TokenPayload Token { get; init; }
    public bool Refreshed { get; init; }
    public bool FellBackOffline { get; init; }
    public string? Warning { get; init; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// The actual licensing loop, shared by the CLI commands and the demo.
/// These methods never print; callers decide how much to show.
/// </summary>
public static class LabOperations
{
    public static async Task<ActivateOutcome> ActivateAsync(
        Install install,
        LicenseApi api,
        string licenseKey,
        string? previousIdentifier = null,
        CancellationToken ct = default)
    {
        var publicKey = await api.GetPublicKeyAsync(ct);
        var result = await api.ActivateAsync(
            licenseKey, install.Identifier, install.Identity.Label, previousIdentifier, ct);
        install.SaveLicense(licenseKey, result.Token, publicKey.PublicKey);

        // Signature-only here: whether the licence term or token has ended is
        // the startup gate's decision, not activation's.
        var payload = OfflineToken.Verify(result.Token, HexCodec.Decode(publicKey.PublicKey), checkExpiry: false);
        return new ActivateOutcome { Result = result, Token = payload };
    }

    public static async Task<VerifyOutcome> VerifyAsync(
        Install install, LicenseApi api, string licenseKey, CancellationToken ct = default)
    {
        var publicKey = await api.GetPublicKeyAsync(ct);
        var result = await api.VerifyAsync(licenseKey, install.Identifier, ct);
        install.SaveLicense(licenseKey, result.Token, publicKey.PublicKey);

        // Signature-only; expiry is evaluated by the startup gate.
        var payload = OfflineToken.Verify(result.Token, HexCodec.Decode(publicKey.PublicKey), checkExpiry: false);
        return new VerifyOutcome { Result = result, Token = payload };
    }

    public static async Task<DeactivateResult> DeactivateAsync(
        Install install, LicenseApi api, string licenseKey, string identifier, CancellationToken ct = default)
    {
        var result = await api.DeactivateAsync(licenseKey, identifier, ct);
        if (string.Equals(identifier, install.Identifier, StringComparison.Ordinal))
            install.ClearToken();
        return result;
    }

    /// <summary>
    /// Mock-only: stands in for the admin "reset device binding" action that
    /// strict mode requires before a replaced machine can activate again.
    /// </summary>
    public static async Task<ResetBindingResult> ResetDeviceBindingAsync(
        Install install, LicenseApi api, CancellationToken ct = default)
    {
        var result = await api.ResetDeviceBindingAsync(ct);
        install.ClearToken();
        return result;
    }

    /// <summary>
    /// The app-startup gate:
    ///   app files → token present → optional online refresh → signature →
    ///   device binding → licence term → token expiry.
    /// Throws LabException with a stable code on the first failure.
    /// </summary>
    public static async Task<StartResult> EvaluateStartAsync(
        Install install, LicenseApi? api, string? licenseKey, bool offline, CancellationToken ct = default)
    {
        var appIssues = install.VerifyAppFiles();
        if (appIssues.Count > 0)
        {
            throw new LabException(
                "APP_FILES_CHANGED",
                "application integrity check failed:\n" +
                string.Join("\n", appIssues.Select(issue => "    - " + issue)) + "\n" +
                $"    Restore the files with: dotnet run -- repair --name {install.Name} --target app");
        }

        var license = install.License;
        var refreshed = false;
        var fellBack = false;
        string? warning = null;

        if (!offline)
        {
            if (api is null)
                throw new LabException("INVALID_USAGE", "online mode requires a server.");
            if (string.IsNullOrWhiteSpace(licenseKey))
                throw new LabException(
                    "MISSING_LICENSE_KEY",
                    "provide --license-key (or set KEYGATE_LAB_LICENSE_KEY).");

            if (license is null || !license.HasToken)
            {
                await VerifyAsync(install, api, licenseKey, ct);
                license = install.License;
            }
            else
            {
                try
                {
                    await VerifyAsync(install, api, licenseKey, ct);
                    license = install.License;
                    refreshed = true;
                }
                catch (KeygateApiException ex) when (ex.IsNetwork)
                {
                    // A network outage is not a licence decision. Fall back to
                    // the signed token, which still has its own expiry.
                    fellBack = true;
                    warning = $"server unreachable ({ex.Message}); continuing with the saved offline token.";
                }
            }
        }

        license = install.License;
        if (license is null || !license.HasToken)
        {
            throw new LabException(
                "NO_TOKEN",
                offline
                    ? "offline mode needs a saved token; run activate (or verify) online first."
                    : "the server did not return a token; run activate first.");
        }

        if (string.IsNullOrWhiteSpace(license.PublicKeyHex) && !offline && api is not null)
        {
            var publicKey = await api.GetPublicKeyAsync(ct);
            install.SaveLicense(license.LicenseKey, license.Token, publicKey.PublicKey);
            license = install.License!;
        }

        if (string.IsNullOrWhiteSpace(license.PublicKeyHex))
        {
            throw new LabException(
                "NO_PUBLIC_KEY",
                "no pinned public key. Run an online verify once to fetch /license/pubkey.");
        }

        var token = OfflineToken.Verify(license.Token, HexCodec.Decode(license.PublicKeyHex), checkExpiry: false);

        var binding = OfflineToken.BindingError(token, install.Identifier);
        if (binding is not null)
        {
            throw new LabException(
                "DEVICE_MISMATCH",
                binding + "\n" +
                "    This is the machine-change / copied-token case. The token is validly signed, " +
                "but it was not issued for this install.\n" +
                $"    Current identifier: {install.Identifier}");
        }

        if (!OfflineToken.LicenseTermOk(token, out var termReason))
            throw new LabException("LICENSE_TERM_ENDED", termReason);

        if (token.ExpiresAt > 0 && LabUtil.NowUnix() > token.ExpiresAt)
        {
            throw new LabException(
                "TOKEN_EXPIRED",
                $"the offline token expired at {LabUtil.FormatUnix(token.ExpiresAt)}. " +
                (offline
                    ? "Run without --offline to refresh it."
                    : "The server returned an expired token; check the licence term and grace."));
        }

        var result = new StartResult
        {
            License = license,
            Token = token,
            Refreshed = refreshed,
            FellBackOffline = fellBack,
            Warning = warning,
        };

        if (token.UpdatesUntil > 0 && LabUtil.NowUnix() > token.UpdatesUntil)
        {
            result.Notes.Add(
                "the maintenance period ended; the app may run but releases published after " +
                $"{LabUtil.FormatUnix(token.UpdatesUntil)} must not be installed.");
        }

        return result;
    }
}
