namespace Keygate.Lab;

public static class LabCommands
{
    public static async Task<int> RunAsync(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        if (options.Flag("help"))
        {
            Help.Print();
            return 0;
        }

        return options.Command switch
        {
            "install" => InstallCommand(options, paths),
            "activate" => await ActivateCommand(options, paths, serverUrl, licenseKey),
            "verify" => await VerifyCommand(options, paths, serverUrl, licenseKey),
            "deactivate" => await DeactivateCommand(options, paths, serverUrl, licenseKey),
            "start" => await StartCommand(options, paths, serverUrl, licenseKey),
            "status" => StatusCommand(options, paths, serverUrl),
            "machine-change" => await MachineChangeCommand(options, paths, serverUrl, licenseKey),
            "tamper" => TamperCommand(options, paths),
            "repair" => RepairCommand(options, paths),
            "reset" => ResetCommand(options, paths),
            "server" => await ServerCommand(options, paths, serverUrl),
            "demo" => await DemoCommand.RunAsync(options, paths, serverUrl),
            _ => throw new LabException(
                "INVALID_USAGE", $"unknown command '{options.Command}'. Run: dotnet run -- help"),
        };
    }

    // ─── install / activate / verify / deactivate ───────────────────────

    private static int InstallCommand(CliOptions options, LabPaths paths)
    {
        var name = RequireName(options);
        var label = options.Value("label") ?? $"Lab install {name}";
        var scheme = options.Value("scheme") ?? "kgid";
        var platform = options.Value("platform");

        var install = Install.Create(paths, name, label, options.Flag("force"), scheme, platform);
        var hardware = install.DescribeHardware();

        Ui.Header($"Install '{install.Name}'");
        Ui.Field("directory", install.Dir);
        Ui.Field("scheme", install.Identity.Scheme);
        Ui.Field("platform", install.Identity.Platform);
        Ui.Field("identifier", install.Identifier);
        Ui.Field("machine id", install.Device.MachineId);
        Ui.Field("install id", install.Identity.InstallId);
        Ui.Field("label", install.Identity.Label);
        Ui.Field("composite", hardware["digest"]);
        Ui.Field("components", string.Join(", ", DeviceIdentity.Components));
        Ui.Field("app files", string.Join(", ", install.ListAppFiles()));

        if (!DeviceIdentity.IsHardwareScheme(install.Identifier))
        {
            Ui.Warn("this identifier is not hardware-derived; a strict server rejects it with " +
                    "IDENTIFIER_SCHEME_UNSUPPORTED.");
        }

        Ui.Info($"Next: dotnet run -- activate --name {install.Name}");
        return 0;
    }

    private static async Task<int> ActivateCommand(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        var install = Install.Open(paths, RequireName(options));
        using var session = LabSession.Start(paths, serverUrl);
        using var api = new LicenseApi(session.Url);
        var key = ResolveKey(licenseKey, session, install);
        var previous = options.Value("previous-identifier");

        var outcome = await LabOperations.ActivateAsync(install, api, key, previous);

        Ui.Header($"Activate '{install.Name}'");
        Ui.Field("server", DescribeServer(session));
        Ui.Field("binding", DescribeBinding(session));
        Ui.Field("identifier", install.Identifier);
        Ui.Field("status", outcome.Result.Status);
        Ui.Field("license id", outcome.Result.LicenseId);
        Ui.Field("license key", LabUtil.MaskKey(key));
        Ui.Field("signature", "VERIFIED (Ed25519)");
        Ui.Field("key id", outcome.Token.KeyId);
        Ui.Field("token expires", LabUtil.FormatUnix(outcome.Token.ExpiresAt));
        Ui.Field("fingerprint", outcome.Token.Fingerprint);

        if (!string.IsNullOrWhiteSpace(previous))
            Ui.Info($"previous_identifier was sent: {previous} (tolerant mode migrates the seat in place).");

        Ui.Ok($"token saved to {install.LicensePath}");
        return 0;
    }

    private static async Task<int> VerifyCommand(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        var install = Install.Open(paths, RequireName(options));
        using var session = LabSession.Start(paths, serverUrl);
        using var api = new LicenseApi(session.Url);
        var key = ResolveKey(licenseKey, session, install);

        var outcome = await LabOperations.VerifyAsync(install, api, key);

        Ui.Header($"Verify '{install.Name}'");
        Ui.Field("server", DescribeServer(session));
        Ui.Field("binding", DescribeBinding(session));
        Ui.Field("identifier", install.Identifier);
        Ui.Field("status", outcome.Result.Status);
        Ui.Field("plan", $"{outcome.Result.PlanName} ({outcome.Result.PlanId})");
        Ui.Field("valid until", outcome.Result.ValidUntil is null
            ? "perpetual"
            : LabUtil.FormatDate(outcome.Result.ValidUntil));
        Ui.Field("updates until", LabUtil.FormatDate(outcome.Result.UpdatesUntil));
        Ui.Field("grace days", outcome.Result.GraceDays);
        Ui.Field("signature", "VERIFIED (Ed25519)");
        Ui.Field("token expires", LabUtil.FormatUnix(outcome.Token.ExpiresAt));
        Ui.Field("fingerprint", outcome.Token.Fingerprint);

        if (outcome.Result.Features is { Count: > 0 } features)
        {
            Ui.Field("features", "");
            foreach (var (name, value) in features)
                Ui.Bullet($"{name} = {OfflineToken.FormatFeature(value)}");
        }

        Ui.Ok($"token refreshed at {install.LicensePath}");
        return 0;
    }

    private static async Task<int> DeactivateCommand(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        var install = Install.Open(paths, RequireName(options));
        using var session = LabSession.Start(paths, serverUrl);
        using var api = new LicenseApi(session.Url);
        var key = ResolveKey(licenseKey, session, install);
        var identifier = options.Value("identifier") ?? install.Identifier;

        DeactivateResult result;
        try
        {
            result = await LabOperations.DeactivateAsync(install, api, key, identifier);
        }
        catch (KeygateApiException ex) when (ex.Code == "DEACTIVATION_DISABLED")
        {
            Ui.Header($"Deactivate '{install.Name}'");
            Ui.Field("server", DescribeServer(session));
            Ui.Field("binding", DescribeBinding(session));
            Ui.Field("identifier", identifier);
            Ui.Fail("the server refused: DEACTIVATION_DISABLED (this is strict binding working as designed).");
            Ui.Info("A client must not be able to release its own device and move the licence.");
            Ui.Info("Simulate the admin action instead: dotnet run -- server reset-binding");
            return 1;
        }

        Ui.Header($"Deactivate '{install.Name}'");
        Ui.Field("server", DescribeServer(session));
        Ui.Field("binding", DescribeBinding(session));
        Ui.Field("identifier", identifier);
        Ui.Field("status", result.Status);

        if (string.Equals(identifier, install.Identifier, StringComparison.Ordinal))
            Ui.Info("the local token was cleared, so this install now needs activate/verify.");
        else
            Ui.Info("an old identifier was freed; the current token was left untouched.");

        return 0;
    }

    // ─── start / status ─────────────────────────────────────────────────

    private static async Task<int> StartCommand(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        var install = Install.Open(paths, RequireName(options));
        var offline = options.Flag("offline");

        LabSession? session = null;
        LicenseApi? api = null;
        try
        {
            if (!offline)
            {
                session = LabSession.Start(paths, serverUrl);
                api = new LicenseApi(session.Url);
            }

            var key = offline ? null : ResolveKey(licenseKey, session!, install);
            var result = await LabOperations.EvaluateStartAsync(install, api, key, offline);

            Ui.Header($"Application start: '{install.Name}'");
            Ui.Field("server", offline ? "offline (no network call)" : DescribeServer(session!));
            Ui.Field("binding", offline ? "(not asked)" : DescribeBinding(session!));
            Ui.Field("identifier", install.Identifier);
            Ui.Field("app integrity", "OK (all shipped files match the manifest)");

            if (result.FellBackOffline)
                Ui.Warn(result.Warning ?? "server unreachable; using the saved token.");

            Ui.Ok("licence accepted");
            Ui.Field("license id", result.Token.LicenseId);
            Ui.Field("plan id", result.Token.PlanId);
            Ui.Field("status", result.Token.Status);
            Ui.Field("key id", result.Token.KeyId);
            Ui.Field("device", result.Token.Identifier);
            Ui.Field("token expires", LabUtil.FormatUnix(result.Token.ExpiresAt));
            Ui.Field("license term", result.Token.ValidUntil == 0
                ? "perpetual"
                : $"{LabUtil.FormatUnix(result.Token.ValidUntil)} (+{result.Token.GraceDays}d grace)");
            Ui.Field("updates until", result.Token.UpdatesUntil == 0
                ? "(no separate limit)"
                : LabUtil.FormatUnix(result.Token.UpdatesUntil));

            if (result.Token.Features is { Count: > 0 } features)
            {
                Ui.Field("features", "");
                foreach (var (name, value) in features)
                    Ui.Bullet($"{name} = {OfflineToken.FormatFeature(value)}");
            }

            foreach (var note in result.Notes)
                Ui.Warn(note);

            return 0;
        }
        finally
        {
            api?.Dispose();
            session?.Dispose();
        }
    }

    private static int StatusCommand(CliOptions options, LabPaths paths, string? serverUrl)
    {
        Ui.Header("Keygate Lab status");
        Ui.Field("root", paths.Root);
        Ui.Field("server", serverUrl is null
            ? "built-in mock (no external server)"
            : LicenseApi.NormalizeBaseUrl(serverUrl));

        if (!Directory.Exists(paths.Root))
        {
            Ui.Info("the lab root does not exist yet.");
            return 0;
        }

        var installs = paths.ListInstalls().ToList();
        Ui.Field("installs", installs.Count == 0 ? "(none)" : string.Join(", ", installs));

        Install? install = null;
        var name = options.Value("name") ?? options.FirstPositional;
        if (!string.IsNullOrWhiteSpace(name))
        {
            install = Install.Open(paths, name);
        }
        else if (installs.Count == 1)
        {
            install = Install.Open(paths, installs[0]);
        }

        if (install is null)
        {
            Ui.Info("pick an install with --name (or create one with: install --name a).");
            PrintMockBrief(paths);
            return 0;
        }

        PrintInstallStatus(install, paths);
        PrintMockBrief(paths);
        return 0;
    }

    private static void PrintInstallStatus(Install install, LabPaths paths)
    {
        Ui.Header($"Install '{install.Name}'");
        Ui.Field("directory", install.Dir);
        Ui.Field("scheme", install.Identity.Scheme);
        Ui.Field("platform", install.Identity.Platform);
        Ui.Field("identifier", install.Identifier);
        Ui.Field("machine id", install.Device.MachineId);
        Ui.Field("host", install.Device.HostName);
        Ui.Field("install id", install.Identity.InstallId);
        Ui.Field("label", install.Identity.Label);

        var identity = install.Identity;
        if (identity.PreviousIdentifiers is { Count: > 0 })
        {
            Ui.Field("old identifiers", "");
            foreach (var previous in identity.PreviousIdentifiers)
                Ui.Bullet(previous);
        }

        if (!DeviceIdentity.IsHardwareScheme(install.Identifier))
            Ui.Warn("not hardware-derived; a strict server rejects this identifier.");

        var issues = install.VerifyAppFiles();
        if (issues.Count == 0)
        {
            Ui.Ok("app integrity: OK (all files match the manifest)");
        }
        else
        {
            Ui.Fail("app integrity failed:");
            foreach (var issue in issues) Ui.Bullet(issue);
            Ui.Info($"Repair with: dotnet run -- repair --name {install.Name} --target app");
        }

        var license = install.License;
        if (license is null || !license.HasToken)
        {
            Ui.Info("licence: not activated (no token).");
            return;
        }

        Ui.Field("license key", LabUtil.MaskKey(license.LicenseKey));
        Ui.Field("token saved", LabUtil.FormatDate(license.SavedAt));

        if (string.IsNullOrWhiteSpace(license.PublicKeyHex))
        {
            Ui.Warn("no pinned public key; run an online verify to fetch /license/pubkey.");
            return;
        }

        TokenPayload token;
        try
        {
            token = OfflineToken.Verify(license.Token, HexCodec.Decode(license.PublicKeyHex), checkExpiry: false);
            Ui.Ok("token signature: VERIFIED (Ed25519)");
        }
        catch (LabException ex)
        {
            Ui.Fail($"token signature: {ex.Code} — {ex.Message}");
            try
            {
                token = OfflineToken.DecodeUnsafe(license.Token);
                Ui.Warn("showing the unverified payload for diagnostics only:");
            }
            catch (LabException)
            {
                return;
            }
        }

        PrintToken(token);

        var binding = OfflineToken.BindingError(token, install.Identifier);
        if (binding is null) Ui.Ok("device binding: matches this install");
        else Ui.Fail($"device binding: {binding}");

        if (OfflineToken.LicenseTermOk(token, out var reason)) Ui.Ok("licence term: still valid");
        else Ui.Fail($"licence term: {reason}");

        if (token.ExpiresAt > 0 && LabUtil.NowUnix() > token.ExpiresAt)
            Ui.Warn($"the token expired at {LabUtil.FormatUnix(token.ExpiresAt)}; run verify online.");
        else
            Ui.Ok($"token expiry: {LabUtil.FormatUnix(token.ExpiresAt)}");

        var mock = MockServerStore.TryLoad(paths.MockState);
        if (mock is not null)
        {
            var slot = mock.Activations.FirstOrDefault(
                activation => string.Equals(activation.Identifier, install.Identifier, StringComparison.Ordinal));
            Ui.Field("mock activation", slot is null
                ? "not present (this identifier does not hold a slot)"
                : $"held since {LabUtil.FormatDate(slot.CreatedAt)}");
        }
    }

    private static void PrintMockBrief(LabPaths paths)
    {
        var state = MockServerStore.TryLoad(paths.MockState);
        if (state is null)
        {
            Ui.Info("mock server state has not been created yet.");
            return;
        }

        Ui.Header("Mock licence server");
        Ui.Field("license key", state.LicenseKey);
        Ui.Field("status", state.Status);
        Ui.Field("device binding", state.DeviceBinding);
        Ui.Field("seat limit", MockServerStore.IsStrict(state) ? "1 (strict)" : state.MaxActivations.ToString());
        Ui.Field("activations", state.Activations.Count);
        foreach (var activation in state.Activations)
        {
            Ui.Bullet($"{activation.Identifier}  label='{activation.Label}'  last seen {LabUtil.FormatDate(activation.LastSeenAt)}");
        }
    }

    // ─── machine-change / tamper / repair ───────────────────────────────

    private static async Task<int> MachineChangeCommand(
        CliOptions options, LabPaths paths, string? serverUrl, string? licenseKey)
    {
        var install = Install.Open(paths, RequireName(options));
        var (oldMachine, oldIdentifier, newIdentifier) = install.RotateMachine();
        var deactivateOld = options.Flag("deactivate-old");
        var bindingMode = DescribeBindingMode(serverUrl, paths);
        var attemptedDeactivate = false;
        var deactivated = false;

        if (deactivateOld)
        {
            using var session = LabSession.Start(paths, serverUrl);
            using var api = new LicenseApi(session.Url);
            var key = ResolveKey(licenseKey, session, install);
            try
            {
                await LabOperations.DeactivateAsync(install, api, key, oldIdentifier);
                Ui.Ok($"old identifier freed on the server: {oldIdentifier}");
                deactivated = true;
            }
            catch (KeygateApiException ex) when (ex.Code == "LICENSE_NOT_FOUND")
            {
                Ui.Warn($"the old identifier did not hold a slot: {oldIdentifier}");
                deactivated = true;
            }
            catch (KeygateApiException ex) when (ex.Code == "DEACTIVATION_DISABLED")
            {
                Ui.Warn("the server refused DEACTIVATION_DISABLED; strict binding does not allow client-side release.");
                Ui.Info("Use the admin action instead: dotnet run -- server reset-binding");
                attemptedDeactivate = true;
            }
            finally
            {
                if (deactivated) install.ClearToken();
            }
        }

        var stillBound = !deactivated && !attemptedDeactivate;

        Ui.Header($"Machine change: '{install.Name}'");
        Ui.Field("binding mode", bindingMode);
        Ui.Field("old machine id", oldMachine);
        Ui.Field("new machine id", install.Device.MachineId);
        Ui.Field("old identifier", oldIdentifier);
        Ui.Field("new identifier", newIdentifier);
        Ui.Field("token", deactivated
            ? "cleared"
            : "kept (still bound to the old identifier)");

        if (stillBound)
        {
            Ui.Info("Expected offline start: DEVICE_MISMATCH.");
            Ui.Info("Expected online verify: LICENSE_NOT_FOUND (the new identifier is not activated).");
        }

        if (bindingMode == "strict")
        {
            if (!deactivated)
            {
                Ui.Info("Expected activate on the new machine: DEVICE_CHANGED (one device per licence).");
                Ui.Info("Expected deactivate: DEACTIVATION_DISABLED.");
                Ui.Info("Recovery (simulates the admin reset-device-binding):");
                Ui.Info($"  dotnet run -- server reset-binding");
                Ui.Info($"  dotnet run -- activate --name {install.Name}");
            }
            Ui.Info("In production only an administrator can release strict binding.");
        }
        else if (bindingMode == "tolerant")
        {
            Ui.Info("Expected activate: ACTIVATION_LIMIT while another seat is live (max 1 by default).");
            Ui.Info($"Recovery: dotnet run -- deactivate --name {install.Name} --identifier {oldIdentifier}");
            Ui.Info($"          dotnet run -- activate --name {install.Name}");
        }
        else
        {
            Ui.Info("This is a real server, so the binding mode is unknown here. Both models are possible:");
            Ui.Info("  strict   -> DEVICE_CHANGED + admin reset required");
            Ui.Info("  tolerant -> ACTIVATION_LIMIT + deactivate the old identifier");
        }

        return 0;
    }

    private static int TamperCommand(CliOptions options, LabPaths paths)
    {
        var install = Install.Open(paths, RequireName(options));
        var target = (options.Value("target") ?? "app").ToLowerInvariant();

        switch (target)
        {
            case "app":
            {
                var files = install.TamperApp();
                Ui.Header($"Tamper app files: '{install.Name}'");
                foreach (var file in files) Ui.Bullet(file);
                Ui.Info("Expected offline start: APP_FILES_CHANGED.");
                Ui.Info($"Repair: dotnet run -- repair --name {install.Name} --target app");
                return 0;
            }
            case "token":
            {
                install.TamperToken();
                Ui.Header($"Tamper token: '{install.Name}'");
                Ui.Ok("one character of the signed payload was flipped.");
                Ui.Info("Expected offline start: TOKEN_SIGNATURE_INVALID.");
                Ui.Info($"Recover: dotnet run -- activate --name {install.Name}");
                return 0;
            }
            case "identity":
            {
                var (oldIdentifier, newIdentifier) = install.TamperIdentity();
                Ui.Header($"Tamper identity: '{install.Name}'");
                Ui.Field("before", oldIdentifier);
                Ui.Field("after", newIdentifier);
                Ui.Info("Expected offline start: DEVICE_MISMATCH (the token still names the old identifier).");
                Ui.Info($"Repair: dotnet run -- repair --name {install.Name} --target identity");
                return 0;
            }
            default:
                throw new LabException(
                    "INVALID_USAGE", "--target must be one of: app, token, identity.");
        }
    }

    private static int RepairCommand(CliOptions options, LabPaths paths)
    {
        var install = Install.Open(paths, RequireName(options));
        var target = (options.Value("target") ?? "all").ToLowerInvariant();
        var clearToken = options.Flag("token");

        if (target is not ("app" or "identity" or "all"))
            throw new LabException("INVALID_USAGE", "--target must be one of: app, identity, all.");

        Ui.Header($"Repair '{install.Name}'");

        if (target is "app" or "all")
        {
            install.ResetApp();
            Ui.Ok("app files restored and manifest.json rebuilt.");
        }

        if (target is "identity" or "all")
        {
            var (oldIdentifier, newIdentifier) = install.RepairIdentity();
            if (oldIdentifier != newIdentifier)
                Ui.Ok($"identifier restored: {oldIdentifier} -> {newIdentifier}");
            else
                Ui.Ok($"identifier already matches machineId + installId: {newIdentifier}");
        }

        if (clearToken || target == "token")
        {
            install.ClearToken();
            Ui.Ok("local token cleared; run activate or verify.");
        }

        return 0;
    }

    // ─── reset / server / demo helpers ──────────────────────────────────

    private static int ResetCommand(CliOptions options, LabPaths paths)
    {
        var all = options.Flag("all");
        var mock = options.Flag("mock");
        var name = options.Value("name") ?? options.FirstPositional;

        if (all && (mock || !string.IsNullOrWhiteSpace(name)))
            throw new LabException("INVALID_USAGE", "reset accepts exactly one of --all, --mock, or --name.");

        if (all)
        {
            if (!Directory.Exists(paths.Root))
            {
                Ui.Info("nothing to reset.");
                return 0;
            }
            paths.ResetAll();
            Ui.Ok($"removed the lab root: {paths.Root}");
            return 0;
        }

        if (mock)
        {
            if (!File.Exists(paths.MockState))
            {
                Ui.Info("the mock state does not exist.");
                return 0;
            }
            MockServerStore.Delete(paths.MockState);
            Ui.Ok($"removed the mock state: {paths.MockState}");
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var install = Install.Open(paths, name);
            LabUtil.SafeDelete(install.Dir, paths.Installs);
            Ui.Ok($"removed install '{install.Name}'.");
            return 0;
        }

        throw new LabException(
            "INVALID_USAGE",
            "reset needs --name <install>, --mock, or --all.");
    }

    private static async Task<int> ServerCommand(CliOptions options, LabPaths paths, string? serverUrl)
    {
        var sub = (options.FirstPositional ?? "").ToLowerInvariant();

        if (sub == "url")
        {
            Ui.Field("server", serverUrl is null
                ? "built-in mock (started per command on a random localhost port)"
                : LicenseApi.NormalizeBaseUrl(serverUrl));
            return 0;
        }

        if (sub == "info")
        {
            if (serverUrl is not null)
            {
                Ui.Header("Real Keygate server");
                Ui.Field("url", LicenseApi.NormalizeBaseUrl(serverUrl));
                Ui.Info("server info/reset only apply to the built-in mock.");
                Ui.Info("The binding mode is whatever the server runs with (STRICT_DEVICE_BINDING, default true).");
                return 0;
            }

            paths.EnsureRoot();
            var state = MockServerStore.LoadOrCreate(paths.MockState);
            PrintMockState(state, paths);
            return 0;
        }

        if (sub == "reset")
        {
            if (serverUrl is not null)
            {
                throw new LabException(
                    "MOCK_ONLY",
                    "server reset only works with the built-in mock. Refusing to mutate a real server.");
            }

            var maxActivations = LabUtil.ParseInt(options.Value("max-activations"), "max-activations");
            var tokenTtlDays = LabUtil.ParseInt(options.Value("token-ttl-days"), "token-ttl-days");
            var validInDays = LabUtil.ParseInt(options.Value("valid-in-days"), "valid-in-days");
            var graceDays = LabUtil.ParseInt(options.Value("grace-days"), "grace-days");
            var status = options.Value("status");
            var deviceBinding = options.Value("device-binding");

            if (tokenTtlDays is <= 0)
                throw new LabException("INVALID_USAGE", "--token-ttl-days must be greater than 0.");
            if (graceDays is < 0)
                throw new LabException("INVALID_USAGE", "--grace-days cannot be negative.");
            if (deviceBinding is not null && deviceBinding.ToLowerInvariant() is not ("strict" or "tolerant"))
                throw new LabException("INVALID_USAGE", "--device-binding must be 'strict' or 'tolerant'.");

            if (status is not null && status.ToLowerInvariant() is not
                ("active" or "trialing" or "past_due" or "canceled" or "suspended" or "revoked" or "expired"))
            {
                throw new LabException(
                    "INVALID_USAGE",
                    "--status must be active, trialing, past_due, canceled, suspended, revoked, or expired.");
            }

            paths.EnsureRoot();
            var state = MockServerStore.Reset(
                paths.MockState,
                maxActivations: maxActivations,
                rotateKey: options.Flag("rotate-key"),
                validInDays: validInDays,
                status: status,
                tokenTtlDays: tokenTtlDays,
                graceDays: graceDays,
                deviceBinding: deviceBinding);

            PrintMockState(state, paths);
            Ui.Ok("mock server reset. Existing activations were cleared.");
            if (options.Flag("rotate-key"))
                Ui.Warn("the signing key was rotated; all previously saved tokens are now invalid.");
            else
                Ui.Warn("saved offline tokens are still valid until their own exp; use --offline to test that path.");
            return 0;
        }

        if (sub == "reset-binding")
        {
            if (serverUrl is not null)
            {
                throw new LabException(
                    "MOCK_ONLY",
                    "reset-binding simulates the admin API, which the lab cannot call on a real server. " +
                    "Use the admin panel there.");
            }

            using var session = LabSession.Start(paths, null);
            using var api = new LicenseApi(session.Url);
            var result = await api.ResetDeviceBindingAsync();

            Ui.Header("Reset device binding (mock admin action)");
            Ui.Field("server", "built-in mock");
            Ui.Field("status", result.Status);
            Ui.Field("released", result.Freed);
            Ui.Ok("the licence has no live device; the next activate binds a new identifier.");
            Ui.Info("On a real server this is POST /api/v1/admin/licenses/:id/reset-device-binding.");
            return 0;
        }

        throw new LabException(
            "INVALID_USAGE",
            "server needs a subcommand: info, reset, reset-binding, or url. Example: dotnet run -- server info");
    }

    private static void PrintMockState(MockServerState state, LabPaths paths)
    {
        Ui.Header("Mock licence server");
        Ui.Field("state file", paths.MockState);
        Ui.Field("license key", state.LicenseKey);
        Ui.Field("license id", state.LicenseId);
        Ui.Field("product", state.ProductId);
        Ui.Field("plan", $"{state.PlanName} ({state.PlanId})");
        Ui.Field("status", state.Status);
        Ui.Field("device binding", state.DeviceBinding);
        Ui.Field("seat limit", MockServerStore.IsStrict(state) ? "1 (strict)" : state.MaxActivations.ToString());
        Ui.Field("activations", state.Activations.Count);

        foreach (var activation in state.Activations)
        {
            Ui.Bullet(
                $"{activation.Identifier}  label='{activation.Label}'  " +
                $"created {LabUtil.FormatDate(activation.CreatedAt)}  " +
                $"last seen {LabUtil.FormatDate(activation.LastSeenAt)}");
        }

        Ui.Field("token ttl", $"{state.TokenTtlDays} day(s)");
        Ui.Field("grace", $"{state.GraceDays} day(s)");
        Ui.Field("valid until", state.ValidUntil is null ? "perpetual" : LabUtil.FormatDate(state.ValidUntil));
        Ui.Field("updates until", state.UpdatesUntil is null ? "(no separate limit)" : LabUtil.FormatDate(state.UpdatesUntil));

        if (state.Features.Count > 0)
        {
            Ui.Field("features", "");
            foreach (var (name, value) in state.Features)
                Ui.Bullet($"{name} = {MockServerStore.FeatureString(value)}");
        }

        Ui.Field("public key", MockServerStore.PublicKeyHex(state));
        Ui.Field("key id", MockServerStore.KeyId(MockServerStore.PublicKeyHex(state)));
        Ui.Info("the Ed25519 seed stays in server.json and is never printed.");

        if (MockServerStore.IsStrict(state))
        {
            Ui.Info("strict: one device per licence; activate/deactivate of a second device is refused.");
        }
        else
        {
            Ui.Info("tolerant: activations are counted up to --max-activations and clients may deactivate.");
        }
    }

    // ─── shared helpers ─────────────────────────────────────────────────

    private static string RequireName(CliOptions options)
    {
        var name = options.Value("name") ?? options.FirstPositional;
        if (string.IsNullOrWhiteSpace(name))
            throw new LabException("INVALID_USAGE", "provide --name <install> (for example: --name a).");
        return name;
    }

    private static string ResolveKey(string? cliKey, LabSession session, Install install)
    {
        var key = cliKey;
        if (string.IsNullOrWhiteSpace(key)) key = install.License?.LicenseKey;
        if (string.IsNullOrWhiteSpace(key)) key = session.MockState?.LicenseKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LabException(
                "MISSING_LICENSE_KEY",
                "provide --license-key (or set KEYGATE_LAB_LICENSE_KEY). " +
                "Omit --server to use the built-in mock's key.");
        }

        return key.Trim();
    }

    private static string DescribeServer(LabSession session) =>
        session.IsMock ? "built-in mock (no external server)" : session.Url;

    private static string DescribeBinding(LabSession session) =>
        session.MockState is { } state
            ? $"{state.DeviceBinding} (mock)"
            : "server-side setting (STRICT_DEVICE_BINDING; default strict)";

    private static string DescribeBindingMode(string? serverUrl, LabPaths paths)
    {
        if (serverUrl is not null) return "unknown (real server)";
        return MockServerStore.TryLoad(paths.MockState) is { } state
            ? state.DeviceBinding
            : "strict (default)";
    }

    private static void PrintToken(TokenPayload token)
    {
        Ui.Field("license id", token.LicenseId);
        Ui.Field("product id", token.ProductId);
        Ui.Field("plan id", token.PlanId);
        Ui.Field("status", token.Status);
        Ui.Field("device", token.Identifier);
        Ui.Field("key id", token.KeyId);
        Ui.Field("fingerprint", token.Fingerprint);
        Ui.Field("issued", LabUtil.FormatUnix(token.IssuedAt));
        Ui.Field("token expires", LabUtil.FormatUnix(token.ExpiresAt));
        Ui.Field("license term", token.ValidUntil == 0
            ? "perpetual"
            : $"{LabUtil.FormatUnix(token.ValidUntil)} (+{token.GraceDays}d grace)");
        Ui.Field("updates until", token.UpdatesUntil == 0
            ? "(no separate limit)"
            : LabUtil.FormatUnix(token.UpdatesUntil));

        if (token.Features is { Count: > 0 } features)
        {
            Ui.Field("features", "");
            foreach (var (name, value) in features)
                Ui.Bullet($"{name} = {OfflineToken.FormatFeature(value)}");
        }
    }
}
