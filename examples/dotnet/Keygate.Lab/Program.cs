using Keygate.Lab;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);

            if (options.Command == "help" || options.Flag("help"))
            {
                Help.Print();
                return 0;
            }

            var root = options.Value("root")
                       ?? Environment.GetEnvironmentVariable("KEYGATE_LAB_ROOT")
                       ?? Path.Combine(Directory.GetCurrentDirectory(), ".keygate-lab");

            var server = options.Value("server")
                         ?? Environment.GetEnvironmentVariable("KEYGATE_LAB_SERVER");

            var licenseKey = options.Value("license-key")
                             ?? Environment.GetEnvironmentVariable("KEYGATE_LAB_LICENSE_KEY");

            return await LabCommands.RunAsync(options, new LabPaths(root), server, licenseKey);
        }
        catch (LabException ex)
        {
            Console.Error.WriteLine($"  FAIL [{ex.Code}] {ex.Message}");
            return 1;
        }
        catch (KeygateApiException ex)
        {
            Console.Error.WriteLine($"  FAIL [{ex.Code}] HTTP {ex.Status}: {ex.Message}");
            Console.Error.WriteLine($"  HINT {ex.Friendly()}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("  FAIL [CANCELED] operation canceled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  FAIL [UNEXPECTED] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
