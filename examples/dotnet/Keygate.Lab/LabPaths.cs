namespace Keygate.Lab;

/// <summary>
/// Where the lab keeps its data. One root holds many isolated "installs" and
/// the mock server state, so every scenario is reproducible and easy to reset.
/// </summary>
public sealed class LabPaths
{
    public string Root { get; }
    public string Installs => Path.Combine(Root, "installs");
    public string MockDir => Path.Combine(Root, "mock");
    public string MockState => Path.Combine(MockDir, "server.json");
    public string Marker => Path.Combine(Root, ".keygate-lab-root");

    public LabPaths(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new LabException("INVALID_USAGE", "the lab root directory is empty.");
        Root = Path.GetFullPath(root);
    }

    public void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        if (!File.Exists(Marker))
        {
            File.WriteAllText(
                Marker,
                "This directory is managed by Keygate.Lab.\n" +
                "Delete it with: dotnet run -- reset --all\n");
        }
    }

    /// <summary>Delete the whole lab root. The marker stops a typo'd --root from eating a real directory.</summary>
    public void ResetAll()
    {
        if (!File.Exists(Marker))
        {
            throw new LabException(
                "REFUSING_TO_DELETE",
                $"'{Root}' does not contain the Keygate.Lab marker; refusing to delete it. " +
                "Delete it manually if you are sure.");
        }

        var filesystemRoot = Path.GetPathRoot(Root) ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Path.TrimEndingDirectorySeparator keeps "/" intact, unlike TrimEnd('/'),
        // which would turn a path root into an empty string.
        var target = Path.TrimEndingDirectorySeparator(Root);
        var protectedRoot = Path.TrimEndingDirectorySeparator(filesystemRoot);
        var protectedHome = string.IsNullOrEmpty(home) ? "" : Path.TrimEndingDirectorySeparator(home);

        if (target.Length == 0 ||
            string.Equals(target, protectedRoot, StringComparison.Ordinal) ||
            (protectedHome.Length > 0 && string.Equals(target, protectedHome, StringComparison.Ordinal)))
        {
            throw new LabException("REFUSING_TO_DELETE", $"refusing to delete protected directory '{Root}'.");
        }

        Directory.Delete(Root, true);
    }

    public IEnumerable<string> ListInstalls()
    {
        if (!Directory.Exists(Installs)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(Installs)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)!;
    }
}
