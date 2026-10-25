namespace Keygate.Lab;

public sealed class DeviceState
{
    /// <summary>Simulates the hardware/OS identity (MachineGuid, IOPlatformUUID, ...).</summary>
    public string MachineId { get; set; } = "";
    public string HostName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<string> PreviousMachineIds { get; set; } = new();
}

public sealed class InstallIdentity
{
    /// <summary>Simulates one installation on the machine.</summary>
    public string InstallId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Identifier { get; set; } = "";

    /// <summary>kgid (default), sha256, or uuid. Strict servers accept only the first two.</summary>
    public string Scheme { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AppVersion { get; set; } = "1.0.0";
    public DateTimeOffset CreatedAt { get; set; }
    public List<string> PreviousIdentifiers { get; set; } = new();
}

public sealed class LicenseState
{
    public string LicenseKey { get; set; } = "";
    public string Token { get; set; } = "";
    public string PublicKeyHex { get; set; } = "";
    public string Identifier { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
}

public sealed class Manifest
{
    public DateTimeOffset CreatedAt { get; set; }
    public List<ManifestFile> Files { get; set; } = new();
}

public sealed class ManifestFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>
/// One simulated installation:
///
///   installs/&lt;name&gt;/
///     device.json      machineId (hardware)
///     install.json     installId + identifier scheme + identifier
///     license.json     key + signed token + pinned public key
///     manifest.json    SHA-256 of every file under app/
///     app/             the "shipped program" that can be tampered with
///
/// The identifier is hardware-derived (kgid:v1:platform:digest) exactly like
/// pkg/deviceid. machine-change rotates machineId, so the identifier changes
/// while the saved token (and its fingerprint) does not. That is exactly the
/// case a real app must reject.
/// </summary>
public sealed class Install
{
    private readonly LabPaths _paths;

    public string Name { get; }
    public string Dir { get; }

    public string DevicePath => Path.Combine(Dir, "device.json");
    public string IdentityPath => Path.Combine(Dir, "install.json");
    public string LicensePath => Path.Combine(Dir, "license.json");
    public string ManifestPath => Path.Combine(Dir, "manifest.json");
    public string AppDir => Path.Combine(Dir, "app");

    private Install(LabPaths paths, string name, string dir)
    {
        _paths = paths;
        Name = name;
        Dir = dir;
    }

    public static Install Open(LabPaths paths, string name)
    {
        var safe = LabUtil.SafeName(name);
        var dir = Path.Combine(paths.Installs, safe);
        if (!File.Exists(Path.Combine(dir, "install.json")))
        {
            throw new LabException(
                "INSTALL_NOT_FOUND",
                $"no install named '{safe}' under {paths.Installs}. " +
                $"Create it with: dotnet run -- install --name {safe}");
        }

        var install = new Install(paths, safe, dir);
        install.MigrateLegacyIdentity();
        return install;
    }

    public static Install Create(
        LabPaths paths,
        string name,
        string label,
        bool force,
        string scheme = "kgid",
        string? platform = null)
    {
        var safe = LabUtil.SafeName(name);
        paths.EnsureRoot();

        var dir = Path.Combine(paths.Installs, safe);
        if (Directory.Exists(dir))
        {
            if (!force)
            {
                throw new LabException(
                    "ALREADY_INSTALLED",
                    $"install '{safe}' already exists at {dir}. Use --force to recreate it.");
            }
            LabUtil.SafeDelete(dir, paths.Installs);
        }

        Directory.CreateDirectory(dir);

        var normalizedScheme = DeviceIdentity.NormalizeScheme(scheme);
        var normalizedPlatform = DeviceIdentity.NormalizePlatform(platform ?? "");

        var now = DateTimeOffset.UtcNow;
        var device = new DeviceState
        {
            MachineId = LabUtil.RandomHex(16),
            HostName = Environment.MachineName,
            CreatedAt = now,
        };
        var identity = new InstallIdentity
        {
            InstallId = LabUtil.RandomHex(8),
            Label = string.IsNullOrWhiteSpace(label) ? $"Lab install {safe}" : label.Trim(),
            Scheme = normalizedScheme,
            Platform = normalizedPlatform,
            AppVersion = "1.0.0",
            CreatedAt = now,
        };
        identity.Identifier = DeviceIdentity.Build(normalizedScheme, normalizedPlatform, device.MachineId);

        var install = new Install(paths, safe, dir);
        LabJson.Write(install.DevicePath, device);
        LabJson.Write(install.IdentityPath, identity);
        install.ResetApp();
        return install;
    }

    public DeviceState Device => LabJson.Read<DeviceState>(DevicePath);
    public InstallIdentity Identity => LabJson.Read<InstallIdentity>(IdentityPath);
    public string Identifier => Identity.Identifier;

    public LicenseState? License =>
        File.Exists(LicensePath) ? LabJson.Read<LicenseState>(LicensePath) : null;

    public void SaveLicense(string licenseKey, string token, string publicKeyHex)
    {
        var existing = License;
        if (string.IsNullOrWhiteSpace(publicKeyHex))
            publicKeyHex = existing?.PublicKeyHex ?? "";

        LabJson.Write(LicensePath, new LicenseState
        {
            LicenseKey = licenseKey,
            Token = token,
            PublicKeyHex = publicKeyHex,
            Identifier = Identifier,
            SavedAt = DateTimeOffset.UtcNow,
        });
    }

    public void ClearToken()
    {
        var license = License;
        if (license is null) return;
        license.Token = "";
        license.SavedAt = DateTimeOffset.UtcNow;
        LabJson.Write(LicensePath, license);
    }

    /// <summary>
    /// Fills in Scheme/Platform for install.json files written by older lab
    /// versions. It never repairs a deliberately tampered identifier: that
    /// only happens when Scheme is missing entirely.
    /// </summary>
    private void MigrateLegacyIdentity()
    {
        var identity = Identity;
        if (!string.IsNullOrWhiteSpace(identity.Scheme)) return;

        var scheme = identity.Identifier.StartsWith("uuid:", StringComparison.Ordinal) ||
                     Guid.TryParse(identity.Identifier, out _)
            ? "uuid"
            : identity.Identifier.StartsWith("sha256:", StringComparison.Ordinal)
                ? "sha256"
                : "kgid";

        var platform = identity.Identifier.StartsWith("kgid:v1:", StringComparison.Ordinal)
            ? identity.Identifier.Split(':')[2]
            : DeviceIdentity.CurrentPlatform();

        identity.Scheme = scheme;
        identity.Platform = DeviceIdentity.NormalizePlatform(platform);

        if (scheme is "kgid" or "sha256")
            identity.Identifier = DeviceIdentity.Build(scheme, identity.Platform, Device.MachineId);

        LabJson.Write(IdentityPath, identity);
    }

    // ─── Simulated application files + manifest ─────────────────────────

    public void ResetApp()
    {
        if (Directory.Exists(AppDir))
            LabUtil.SafeDelete(AppDir, Dir);
        Directory.CreateDirectory(Path.Combine(AppDir, "plugins"));

        File.WriteAllText(Path.Combine(AppDir, "README.txt"),
            "This file stands in for the shipped application binary.\n" +
            "Change anything under app/ and run 'start' to see integrity fail.\n");
        File.WriteAllText(Path.Combine(AppDir, "settings.json"),
            "{\n  \"theme\": \"system\",\n  \"telemetry\": false\n}\n");
        File.WriteAllText(Path.Combine(AppDir, "plugins", "export.txt"),
            "simulated plugin payload\n");

        WriteManifest();
    }

    private void WriteManifest()
    {
        var files = EnumerateAppFiles()
            .Select(path => new ManifestFile
            {
                Path = RelativePath(path),
                Sha256 = LabUtil.Sha256File(path),
                Size = new FileInfo(path).Length,
            })
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();

        LabJson.Write(ManifestPath, new Manifest
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Files = files,
        });
    }

    /// <summary>Returns a human-readable list of integrity problems. Empty means OK.</summary>
    public List<string> VerifyAppFiles()
    {
        var issues = new List<string>();

        if (!Directory.Exists(AppDir))
        {
            issues.Add("missing: app/ (the whole program directory)");
            return issues;
        }

        Manifest? manifest = null;
        try
        {
            manifest = LabJson.Read<Manifest>(ManifestPath);
        }
        catch (LabException ex) when (ex.Code is "STATE_MISSING" or "STATE_CORRUPT")
        {
            issues.Add("missing or unreadable manifest.json (run: repair --target app)");
        }

        var expected = new HashSet<string>(
            manifest?.Files.Select(file => file.Path) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);

        if (manifest is not null)
        {
            foreach (var entry in manifest.Files)
            {
                var full = Path.Combine(Dir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    issues.Add($"missing: {entry.Path}");
                    continue;
                }

                var actual = LabUtil.Sha256File(full);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    issues.Add($"changed: {entry.Path}");
            }
        }

        foreach (var path in EnumerateAppFiles())
        {
            var relative = RelativePath(path);
            if (!expected.Contains(relative))
                issues.Add($"added: {relative}");
        }

        return issues;
    }

    public IReadOnlyList<string> ListAppFiles() =>
        EnumerateAppFiles().Select(RelativePath).OrderBy(path => path, StringComparer.Ordinal).ToList();

    public IReadOnlyDictionary<string, string> DescribeHardware()
    {
        var device = Device;
        var hashes = DeviceIdentity.ComponentHashes(device.MachineId);
        var digest = DeviceIdentity.CompositeDigest(Identity.Platform, hashes);
        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["digest"] = digest,
        };
        foreach (var (component, hash) in hashes)
            result[component] = hash[..16] + "...";
        return result;
    }

    private IEnumerable<string> EnumerateAppFiles() =>
        Directory.EnumerateFiles(AppDir, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                // Finder droppings are not program files.
                return name != ".DS_Store" && !name.StartsWith("._", StringComparison.Ordinal);
            });

    private string RelativePath(string fullPath) =>
        Path.GetRelativePath(Dir, fullPath).Replace('\\', '/');

    // ─── Tamper / repair helpers used by the scenarios ─────────────────

    /// <summary>Modify a shipped file and add a foreign one. Returns the touched paths.</summary>
    public IReadOnlyList<string> TamperApp()
    {
        var settings = Path.Combine(AppDir, "settings.json");
        if (!File.Exists(settings))
            throw new LabException("APP_FILES_CHANGED", "app/settings.json is missing; run: repair --target app");

        var text = File.ReadAllText(settings);
        File.WriteAllText(settings, text + "\n// tampered by Keygate.Lab\n");
        File.WriteAllText(Path.Combine(AppDir, "hacked.txt"), "not part of the original install\n");

        return new[] { "app/settings.json", "app/hacked.txt" };
    }

    /// <summary>Flip one character in the signed half of the token. Returns the broken token.</summary>
    public string TamperToken()
    {
        var license = License ?? throw new LabException("NO_TOKEN", "no saved token; run activate first.");
        if (!license.HasToken)
            throw new LabException("NO_TOKEN", "no saved token; run activate first.");

        var token = license.Token.Trim();
        var dot = token.IndexOf('.');
        if (dot < 4)
            throw new LabException("TOKEN_MALFORMED", "saved token has no payload.signature separator.");

        var chars = token.ToCharArray();
        var index = dot / 2;
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        license.Token = new string(chars);
        license.SavedAt = DateTimeOffset.UtcNow;
        LabJson.Write(LicensePath, license);
        return license.Token;
    }

    /// <summary>Rewrite install.json's identifier with another well-formed one.</summary>
    public (string Old, string New) TamperIdentity()
    {
        var identity = Identity;
        var old = identity.Identifier;

        identity.Identifier = identity.Scheme switch
        {
            "sha256" => "sha256:" + LabUtil.RandomHex(32),
            "uuid" => "uuid:" + Guid.NewGuid().ToString("D"),
            _ => DeviceIdentity.FormatKgid(identity.Platform, LabUtil.RandomHex(32)),
        };

        LabJson.Write(IdentityPath, identity);
        return (old, identity.Identifier);
    }

    /// <summary>Recompute the identifier from machineId (uuid identifiers are opaque).</summary>
    public (string Old, string New) RepairIdentity()
    {
        var device = Device;
        var identity = Identity;
        var old = identity.Identifier;

        identity.Identifier = identity.Scheme == "uuid"
            ? old
            : DeviceIdentity.Build(identity.Scheme, identity.Platform, device.MachineId);

        LabJson.Write(IdentityPath, identity);
        return (old, identity.Identifier);
    }

    /// <summary>
    /// Simulate moving this install to new hardware (or cloning a VM): a new
    /// machineId changes a kgid/sha256 identifier while the saved token stays
    /// behind. A uuid identifier is stored on disk, so it survives — the exact
    /// weakness strict mode exists to reject.
    /// </summary>
    public (string OldMachineId, string OldIdentifier, string NewIdentifier) RotateMachine()
    {
        var device = Device;
        var identity = Identity;

        var oldMachineId = device.MachineId;
        var oldIdentifier = identity.Identifier;

        device.PreviousMachineIds ??= new List<string>();
        device.PreviousMachineIds.Add(oldMachineId);
        if (device.PreviousMachineIds.Count > 20)
            device.PreviousMachineIds.RemoveAt(0);
        device.MachineId = LabUtil.RandomHex(16);

        identity.PreviousIdentifiers ??= new List<string>();
        identity.PreviousIdentifiers.Add(oldIdentifier);
        if (identity.PreviousIdentifiers.Count > 20)
            identity.PreviousIdentifiers.RemoveAt(0);

        if (identity.Scheme is not "uuid")
            identity.Identifier = DeviceIdentity.Build(identity.Scheme, identity.Platform, device.MachineId);

        LabJson.Write(DevicePath, device);
        LabJson.Write(IdentityPath, identity);

        return (oldMachineId, oldIdentifier, identity.Identifier);
    }

    public override string ToString() => $"{Name} ({Identifier})";
}
