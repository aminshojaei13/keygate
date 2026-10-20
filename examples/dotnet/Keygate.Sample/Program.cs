namespace Keygate.Sample;

/// <summary>
/// Keygate.Sample -- a minimal, self-contained .NET console client that shows
/// the full desktop licensing loop against a real Keygate server:
///
///   activate  -> POST /api/v1/license/activate  (binds this device, returns a token)
///   verify    -> POST /api/v1/license/verify    (validates + returns a fresh token)
///   pubkey    -> GET  /api/v1/license/pubkey     (Ed25519 public key)
///   offline   -> verify a previously saved token WITHOUT any network call
///
/// Usage:
///   dotnet run -- pubkey
///   dotnet run -- activate --license-key KG-XXXX-XXXX-XXXX-XXXX --identifier my-machine
///   dotnet run -- verify   --license-key KG-XXXX-XXXX-XXXX-XXXX --identifier my-machine
///   dotnet run -- offline  --token-file keygate-token.txt
/// </summary>
public static class Program
{
    private const string DefaultTokenFile = "keygate-token.txt";

    public static async Task<int> Main(string[] args)
    {
        var cfg = AppConfig.Load();
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var opts = ParseOptions(args.Skip(1).ToArray());

        var client = new KeygateClient(cfg.BaseUrl);

        try
        {
            switch (command)
            {
                case "pubkey":
                    return await PubKeyAsync(client);

                case "activate":
                    return await ActivateAsync(client, opts);

                case "verify":
                    return await VerifyAsync(client, opts);

                case "offline":
                    return Offline(opts);

                case "help":
                case "--help":
                case "-h":
                    PrintHelp(cfg.BaseUrl);
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown command: {command}");
                    PrintHelp(cfg.BaseUrl);
                    return 2;
            }
        }
        catch (KeygateApiException ex)
        {
            Console.Error.WriteLine($"[{ex.Status} {ex.Code}] {ex.Message}");
            Console.Error.WriteLine($"Hint: {ex.Friendly()}");
            return 1;
        }
        catch (OfflineTokenException ex)
        {
            Console.Error.WriteLine($"[OFFLINE_TOKEN_INVALID] {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> PubKeyAsync(KeygateClient client)
    {
        var pk = await client.GetPublicKeyAsync();
        Console.WriteLine($"algorithm : {pk.Algorithm}");
        Console.WriteLine($"format    : {pk.Format}");
        Console.WriteLine($"public key: {pk.PublicKey}");
        return 0;
    }

    private static async Task<int> ActivateAsync(KeygateClient client, Options opts)
    {
        RequireLicense(opts);
        var res = await client.ActivateAsync(opts.LicenseKey!, opts.Identifier!, opts.Label);
        Console.WriteLine($"status    : {res.Status}");
        Console.WriteLine($"license_id: {res.LicenseId}");
        SaveToken(opts.TokenFile, res.Token);
        Console.WriteLine($"token     : saved to {opts.TokenFile}");
        await VerifyTokenWithServerKeyAsync(client, res.Token);
        return 0;
    }

    private static async Task<int> VerifyAsync(KeygateClient client, Options opts)
    {
        RequireLicense(opts);
        var res = await client.VerifyAsync(opts.LicenseKey!, opts.Identifier!);
        Console.WriteLine($"status     : {res.Status}");
        Console.WriteLine($"plan       : {res.PlanName} ({res.PlanId})");
        Console.WriteLine($"valid_until: {Fmt(res.ValidUntil)}");
        Console.WriteLine($"updates_unt: {Fmt(res.UpdatesUntil)}");
        Console.WriteLine($"grace_days : {res.GraceDays}");
        if (res.Features is { Count: > 0 })
        {
            Console.WriteLine("features   :");
            foreach (var (k, v) in res.Features)
                Console.WriteLine($"   - {k} = {v}");
        }
        SaveToken(opts.TokenFile, res.Token);
        Console.WriteLine($"token      : saved to {opts.TokenFile}");
        await VerifyTokenWithServerKeyAsync(client, res.Token);
        return 0;
    }

    private static int Offline(Options opts)
    {
        if (!File.Exists(opts.TokenFile))
        {
            Console.Error.WriteLine($"No saved token at {opts.TokenFile}. Run 'activate' or 'verify' first.");
            return 1;
        }

        // In a real app you would PIN the public key at build time; here we
        // read it from a file next to the token for the demo.
        string keyFile = opts.TokenFile + ".pub.hex";
        if (!File.Exists(keyFile))
        {
            Console.Error.WriteLine($"Missing public key file {keyFile}. Run 'pubkey' and save it, or run 'activate'/'verify' first.");
            return 1;
        }

        var publicKey = Hex.Decode(File.ReadAllText(keyFile).Trim());
        var token = File.ReadAllText(opts.TokenFile).Trim();

        var parsed = OfflineToken.VerifyAndParse(token, publicKey);

        Console.WriteLine("Offline verification: OK (signature valid, token not expired).");
        Console.WriteLine($"license_id : {parsed.LicenseId}");
        Console.WriteLine($"product_id : {parsed.ProductId}");
        Console.WriteLine($"plan_id    : {parsed.PlanId}");
        Console.WriteLine($"status     : {parsed.Status}");
        Console.WriteLine($"device     : {parsed.Identifier}");
        Console.WriteLine($"expires_at : {Fmt(DateTimeOffset.FromUnixTimeSeconds(parsed.ExpiresAt))}");
        Console.WriteLine($"valid_until: {(parsed.ValidUntil == 0 ? "perpetual" : Fmt(DateTimeOffset.FromUnixTimeSeconds(parsed.ValidUntil)))}");

        if (!OfflineToken.LicenseTermOk(parsed, out var reason))
        {
            Console.Error.WriteLine($"License term check failed: {reason}");
            return 1;
        }
        return 0;
    }

    private static async Task VerifyTokenWithServerKeyAsync(KeygateClient client, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            var pk = await client.GetPublicKeyAsync();
            var publicKey = Hex.Decode(pk.PublicKey);
            var parsed = OfflineToken.VerifyAndParse(token, publicKey);
            Console.WriteLine("signature : VERIFIED (Ed25519, matches /license/pubkey)");
            Console.WriteLine($"token exp : {Fmt(DateTimeOffset.FromUnixTimeSeconds(parsed.ExpiresAt))}");
        }
        catch (KeygateApiException ex)
        {
            Console.Error.WriteLine($"Could not fetch public key to verify token: {ex.Message}");
        }
        catch (OfflineTokenException ex)
        {
            Console.Error.WriteLine($"Token verification FAILED: {ex.Message}");
        }
    }

    private static void SaveToken(string path, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        File.WriteAllText(path, token);
        // Also persist the public key so 'offline' works without a server.
        try
        {
            var client = new KeygateClient(AppConfig.Load().BaseUrl);
            var pk = client.GetPublicKeyAsync().GetAwaiter().GetResult();
            File.WriteAllText(path + ".pub.hex", pk.PublicKey);
        }
        catch { /* offline demo can also just run after any online command */ }
    }

    private static string Fmt(DateTimeOffset? d) =>
        d.HasValue ? d.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") : "(none)";

    private static string Fmt(DateTimeOffset d) =>
        d.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    private static void RequireLicense(Options opts)
    {
        if (string.IsNullOrWhiteSpace(opts.LicenseKey))
            throw new KeygateApiException(0, "MISSING_LICENSE_KEY", "Provide --license-key.");
        if (string.IsNullOrWhiteSpace(opts.Identifier))
            throw new KeygateApiException(0, "MISSING_IDENTIFIER", "Provide --identifier (a stable per-device value).");
    }

    private static Options ParseOptions(string[] args)
    {
        var o = new Options { TokenFile = DefaultTokenFile };
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--license-key": o.LicenseKey = Next(args, ref i); break;
                case "--identifier": o.Identifier = Next(args, ref i); break;
                case "--label": o.Label = Next(args, ref i); break;
                case "--token-file": o.TokenFile = Next(args, ref i) ?? DefaultTokenFile; break;
            }
        }
        return o;
    }

    private static string? Next(string[] args, ref int i) => i + 1 < args.Length ? args[++i] : null;

    private static void PrintHelp(string baseUrl)
    {
        Console.WriteLine("Keygate .NET sample client");
        Console.WriteLine($"Server: {baseUrl}");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  pubkey                                            Fetch the Ed25519 public key");
        Console.WriteLine("  activate --license-key KEY --identifier ID        Activate this device");
        Console.WriteLine("  verify   --license-key KEY --identifier ID        Verify + refresh the offline token");
        Console.WriteLine("  offline  [--token-file FILE]                      Verify a saved token with no network");
        Console.WriteLine();
        Console.WriteLine("Optional: --label NAME, --token-file FILE");
        Console.WriteLine("Config:   appsettings.json { \"Keygate\": { \"BaseUrl\": \"...\" } } or KEYGATE_BASE_URL env var");
    }

    private sealed class Options
    {
        public string? LicenseKey { get; set; }
        public string? Identifier { get; set; }
        public string? Label { get; set; }
        public string TokenFile { get; set; } = DefaultTokenFile;
    }
}
