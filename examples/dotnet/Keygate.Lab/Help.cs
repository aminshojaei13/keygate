namespace Keygate.Lab;

public static class Help
{
    public static void Print()
    {
        Console.WriteLine("Keygate Lab - a tiny, repeatable licence test bench");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  install        --name a [--label \"Laptop A\"] [--scheme kgid|sha256|uuid] [--platform darwin] [--force]");
        Console.WriteLine("  activate       --name a [--previous-identifier ID] [--license-key KEY]");
        Console.WriteLine("  verify         --name a [--license-key KEY]");
        Console.WriteLine("  deactivate     --name a [--identifier ID]");
        Console.WriteLine("  start          --name a [--offline]");
        Console.WriteLine("  status         [--name a]");
        Console.WriteLine("  machine-change --name a [--deactivate-old]");
        Console.WriteLine("  tamper         --name a --target app|token|identity");
        Console.WriteLine("  repair         --name a [--target app|identity|all] [--token]");
        Console.WriteLine("  server         info|reset|reset-binding|url");
        Console.WriteLine("  reset          --name a | --mock | --all");
        Console.WriteLine("  demo           (mock-only, scripted end-to-end checks)");
        Console.WriteLine();
        Console.WriteLine("Server options:");
        Console.WriteLine("  --server URL          use a real Keygate server instead of the built-in mock");
        Console.WriteLine("  --license-key KEY     licence key (mock mode defaults to the mock's key)");
        Console.WriteLine("  --root DIR            data directory (default: ./.keygate-lab)");
        Console.WriteLine();
        Console.WriteLine("Mock controls (server reset):");
        Console.WriteLine("  --device-binding MODE strict (default) | tolerant");
        Console.WriteLine("  --max-activations N   seat limit in tolerant mode (default 1)");
        Console.WriteLine("  --token-ttl-days N    offline token lifetime (default 7)");
        Console.WriteLine("  --valid-in-days N     licence term; 0 ends now, a negative value is already ended");
        Console.WriteLine("  --grace-days N        grace period after the term (default 7)");
        Console.WriteLine("  --status STATE        active|trialing|past_due|canceled|suspended|revoked|expired");
        Console.WriteLine("  --rotate-key          generate a new Ed25519 signing key (invalidates old tokens)");
        Console.WriteLine();
        Console.WriteLine("Environment:");
        Console.WriteLine("  KEYGATE_LAB_ROOT, KEYGATE_LAB_SERVER, KEYGATE_LAB_LICENSE_KEY");
        Console.WriteLine();
        Console.WriteLine("Start here:");
        Console.WriteLine("  dotnet run -- demo");
        Console.WriteLine();
        Console.WriteLine("Strict binding (the server default) in one screen:");
        Console.WriteLine("  dotnet run -- install --name a");
        Console.WriteLine("  dotnet run -- activate --name a");
        Console.WriteLine("  dotnet run -- machine-change --name a");
        Console.WriteLine("  dotnet run -- start --name a --offline      # DEVICE_MISMATCH");
        Console.WriteLine("  dotnet run -- activate --name a             # DEVICE_CHANGED");
        Console.WriteLine("  dotnet run -- deactivate --name a --identifier <old-id>   # DEACTIVATION_DISABLED");
        Console.WriteLine("  dotnet run -- server reset-binding          # the admin action");
        Console.WriteLine("  dotnet run -- activate --name a             # OK");
        Console.WriteLine();
        Console.WriteLine("Tolerant binding (two seats):");
        Console.WriteLine("  dotnet run -- server reset --device-binding tolerant --max-activations 2");
        Console.WriteLine("  dotnet run -- activate --name a");
        Console.WriteLine("  dotnet run -- activate --name b");
        Console.WriteLine();
        Console.WriteLine("Tamper tests:");
        Console.WriteLine("  dotnet run -- tamper --name a --target app");
        Console.WriteLine("  dotnet run -- start  --name a --offline     # APP_FILES_CHANGED");
        Console.WriteLine("  dotnet run -- repair --name a --target app");
        Console.WriteLine("  dotnet run -- tamper --name a --target token");
        Console.WriteLine("  dotnet run -- start  --name a --offline     # TOKEN_SIGNATURE_INVALID");
        Console.WriteLine();
        Console.WriteLine("Real server:");
        Console.WriteLine("  dotnet run -- activate --name a --server http://localhost:9000 --license-key KG-...");
        Console.WriteLine("  (identifiers are hardware-derived kgid:v1; a strict server needs that)");
    }
}
