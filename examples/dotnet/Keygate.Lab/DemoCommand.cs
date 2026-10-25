namespace Keygate.Lab;

/// <summary>
/// A scripted end-to-end walk-through that runs entirely against the built-in
/// mock. It covers what this lab exists to answer, in the order a real client
/// hits them:
///
///   1. strict device binding: one licence, one device (DEVICE_CHANGED)
///   2. a non-hardware identifier (IDENTIFIER_SCHEME_UNSUPPORTED)
///   3. changed application files (APP_FILES_CHANGED)
///   4. a changed token file (TOKEN_SIGNATURE_INVALID)
///   5. a changed machine / copied token (DEVICE_MISMATCH + admin reset)
///   6. tolerant mode: seat limit and moving a seat
///   7. licence term and grace
///
/// Every check asserts on a stable error code, so a regression shows up as a
/// failed line instead of a vague "it didn't work".
/// </summary>
public static class DemoCommand
{
    public static async Task<int> RunAsync(CliOptions options, LabPaths paths, string? serverUrl)
    {
        if (serverUrl is not null)
        {
            throw new LabException(
                "DEMO_REQUIRES_MOCK",
                "demo mutates activations and is mock-only. " +
                "Run the individual commands with --server to test a real Keygate server.");
        }

        var demoRoot = Path.Combine(paths.Root, "demo");
        paths.EnsureRoot();
        if (Directory.Exists(demoRoot))
            LabUtil.SafeDelete(demoRoot, paths.Root);

        var demoPaths = new LabPaths(demoRoot);
        demoPaths.EnsureRoot();
        MockServerStore.Reset(demoPaths.MockState, maxActivations: 1, rotateKey: true);

        var session = LabSession.Start(demoPaths, null);
        var api = new LicenseApi(session.Url);
        var key = session.MockState!.LicenseKey;

        try
        {
            var a = Install.Create(demoPaths, "a", "Demo laptop A", force: false);
            var b = Install.Create(demoPaths, "b", "Demo desktop B", force: false);
            var c = Install.Create(demoPaths, "c", "Demo opaque C", force: false, scheme: "uuid");

            var results = new List<(bool Passed, string Title, string? Detail)>();
            var oldIdentifier = "";

            Ui.Header("Keygate Lab demo");
            Ui.Field("root", demoPaths.Root);
            Ui.Field("server", "built-in mock");
            Ui.Field("license key", key);
            Ui.Field("binding", "strict (one device per licence)");
            Ui.Info("A and B are two simulated machines; C uses a disk-stored UUID identifier.");
            Console.WriteLine();

            async Task Check(string title, Func<Task> action, string? expectedCode = null)
            {
                try
                {
                    await action();
                    if (expectedCode is null)
                    {
                        Pass(title);
                    }
                    else
                    {
                        Fail(title, $"expected {expectedCode}, but the call succeeded");
                    }
                }
                catch (LabException ex)
                {
                    Record(title, expectedCode, ex.Code, $"{ex.Code}: {ex.Message}");
                }
                catch (KeygateApiException ex)
                {
                    Record(title, expectedCode, ex.Code, $"{ex.Code} (HTTP {ex.Status}): {ex.Message}");
                }
                catch (Exception ex)
                {
                    Fail(title, $"unexpected {ex.GetType().Name}: {ex.Message}");
                }
            }

            void Pass(string title)
            {
                results.Add((true, title, null));
                Ui.Ok(title);
            }

            void Fail(string title, string detail)
            {
                results.Add((false, title, detail));
                Ui.Fail($"{title} -- {detail}");
            }

            void Record(string title, string? expectedCode, string actualCode, string detail)
            {
                if (expectedCode is not null && actualCode == expectedCode)
                    Pass($"{title} -- {actualCode}");
                else
                    Fail(title, $"expected {expectedCode ?? "success"}; got {detail}");
            }

            // ─── 1. strict device binding ───────────────────────────────
            Ui.Info("1) strict binding: one licence, one device");
            await Check("activate A (takes the only binding)", () => LabOperations.ActivateAsync(a, api, key));

            await Check("activate A again is idempotent", async () =>
            {
                var repeat = await LabOperations.ActivateAsync(a, api, key);
                if (repeat.Result.Status != "already_activated")
                    throw new LabException("UNEXPECTED_STATUS", $"status was '{repeat.Result.Status}'.");
            });

            await Check("activate B is refused with DEVICE_CHANGED",
                () => LabOperations.ActivateAsync(b, api, key), "DEVICE_CHANGED");

            await Check("verify B is refused with LICENSE_NOT_FOUND",
                () => LabOperations.VerifyAsync(b, api, key), "LICENSE_NOT_FOUND");

            // ─── 2. non-hardware identifier ─────────────────────────────
            Ui.Info("2) a UUID identifier cannot detect a machine change");
            await Check("activate C is refused with IDENTIFIER_SCHEME_UNSUPPORTED",
                () => LabOperations.ActivateAsync(c, api, key), "IDENTIFIER_SCHEME_UNSUPPORTED");

            await Check("start A offline", () => LabOperations.EvaluateStartAsync(a, null, null, offline: true));

            // ─── 3. changed application files ───────────────────────────
            Ui.Info("3) changed application files");
            await Check("tamper A's shipped files", () =>
            {
                a.TamperApp();
                return Task.CompletedTask;
            });
            await Check("start A detects APP_FILES_CHANGED",
                () => LabOperations.EvaluateStartAsync(a, null, null, true), "APP_FILES_CHANGED");
            await Check("repair A's shipped files", () =>
            {
                a.ResetApp();
                return Task.CompletedTask;
            });
            await Check("start A works again after repair",
                () => LabOperations.EvaluateStartAsync(a, null, null, true));

            // ─── 4. changed token ───────────────────────────────────────
            Ui.Info("4) changed token file");
            await Check("tamper A's saved token", () =>
            {
                a.TamperToken();
                return Task.CompletedTask;
            });
            await Check("start A detects TOKEN_SIGNATURE_INVALID",
                () => LabOperations.EvaluateStartAsync(a, null, null, true), "TOKEN_SIGNATURE_INVALID");
            await Check("re-activate A refreshes the token", () => LabOperations.ActivateAsync(a, api, key));
            await Check("start A offline after refresh",
                () => LabOperations.EvaluateStartAsync(a, null, null, true));

            // ─── 5. changed machine / copied token ──────────────────────
            Ui.Info("5) machine change under strict binding");
            await Check("simulate a machine change for A", () =>
            {
                var rotation = a.RotateMachine();
                oldIdentifier = rotation.OldIdentifier;
                return Task.CompletedTask;
            });
            await Check("start A detects DEVICE_MISMATCH",
                () => LabOperations.EvaluateStartAsync(a, null, null, true), "DEVICE_MISMATCH");
            await Check("verify A with the new identifier is LICENSE_NOT_FOUND",
                () => LabOperations.VerifyAsync(a, api, key), "LICENSE_NOT_FOUND");
            await Check("activate A is refused with DEVICE_CHANGED",
                () => LabOperations.ActivateAsync(a, api, key), "DEVICE_CHANGED");
            await Check("deactivate A is refused with DEACTIVATION_DISABLED",
                () => LabOperations.DeactivateAsync(a, api, key, oldIdentifier), "DEACTIVATION_DISABLED");

            await Check("the admin reset releases the old device (mock action)",
                () => LabOperations.ResetDeviceBindingAsync(a, api));
            await Check("activate A on the new machine", () => LabOperations.ActivateAsync(a, api, key));
            await Check("start A offline after recovery",
                () => LabOperations.EvaluateStartAsync(a, null, null, true));

            // ─── 6. tolerant mode: seats ────────────────────────────────
            Ui.Info("6) tolerant mode: seat limit and moving a seat");
            api.Dispose();
            session.Dispose();
            MockServerStore.Reset(demoPaths.MockState,
                maxActivations: 1, deviceBinding: "tolerant");
            session = LabSession.Start(demoPaths, null);
            api = new LicenseApi(session.Url);

            await Check("activate A in tolerant mode", () => LabOperations.ActivateAsync(a, api, key));
            await Check("activate B is refused with ACTIVATION_LIMIT",
                () => LabOperations.ActivateAsync(b, api, key), "ACTIVATION_LIMIT");
            await Check("deactivate A frees the seat",
                () => LabOperations.DeactivateAsync(a, api, key, a.Identifier));
            await Check("activate B now receives the free seat", () => LabOperations.ActivateAsync(b, api, key));
            await Check("start B offline", () => LabOperations.EvaluateStartAsync(b, null, null, true));

            // ─── 7. tolerant migration ─────────────────────────────────
            Ui.Info("7) tolerant mode migrates previous_identifier in place");
            await Check("simulate a machine change for B", () =>
            {
                var rotation = b.RotateMachine();
                oldIdentifier = rotation.OldIdentifier;
                return Task.CompletedTask;
            });
            await Check("start B detects DEVICE_MISMATCH",
                () => LabOperations.EvaluateStartAsync(b, null, null, true), "DEVICE_MISMATCH");
            await Check("activate B with previous_identifier is migrated",
                () => LabOperations.ActivateAsync(b, api, key, oldIdentifier));
            await Check("verify B after migration", () => LabOperations.VerifyAsync(b, api, key));
            await Check("start B offline after migration",
                () => LabOperations.EvaluateStartAsync(b, null, null, true));

            // ─── 8. licence term ───────────────────────────────────────
            Ui.Info("8) licence term and grace");
            api.Dispose();
            session.Dispose();
            MockServerStore.Reset(demoPaths.MockState,
                maxActivations: 1, deviceBinding: "strict", validInDays: -1, graceDays: 0);
            session = LabSession.Start(demoPaths, null);
            api = new LicenseApi(session.Url);

            await Check("activate A with an already-ended term", () => LabOperations.ActivateAsync(a, api, key));
            await Check("start A detects LICENSE_TERM_ENDED",
                () => LabOperations.EvaluateStartAsync(a, null, null, true), "LICENSE_TERM_ENDED");

            api.Dispose();
            session.Dispose();
            MockServerStore.Reset(demoPaths.MockState,
                maxActivations: 1, deviceBinding: "strict", validInDays: -1, graceDays: 3);
            session = LabSession.Start(demoPaths, null);
            api = new LicenseApi(session.Url);

            await Check("activate A inside its grace period", () => LabOperations.ActivateAsync(a, api, key));
            await Check("start A offline inside grace",
                () => LabOperations.EvaluateStartAsync(a, null, null, true));

            // ─── summary ───────────────────────────────────────────────
            var passed = results.Count(result => result.Passed);
            var failed = results.Count - passed;

            Ui.Header("Summary");
            Ui.Field("checks", results.Count);
            Ui.Field("passed", passed);
            Ui.Field("failed", failed);
            Ui.Field("data", demoPaths.Root);

            if (failed > 0)
            {
                Ui.Fail("failed checks:");
                foreach (var result in results.Where(result => !result.Passed))
                    Ui.Bullet($"{result.Title}: {result.Detail}");
            }
            else
            {
                Ui.Ok("all checks passed");
            }

            Ui.Info($"Inspect with: dotnet run -- status --name a --root {demoPaths.Root}");
            Ui.Info($"Reset with:   dotnet run -- reset --all --root {demoPaths.Root}");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            api.Dispose();
            session.Dispose();
        }
    }
}
