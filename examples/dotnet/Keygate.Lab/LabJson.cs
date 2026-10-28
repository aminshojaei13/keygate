using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keygate.Lab;

/// <summary>
/// One place for JSON settings. State files are pretty-printed so they can be
/// inspected and hand-edited during a test; HTTP payloads and signed tokens are
/// compact, exactly like Go's json.Marshal.
/// </summary>
public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            throw new LabException("STATE_MISSING", $"state file not found: {path}");
        }
        catch (DirectoryNotFoundException)
        {
            throw new LabException("STATE_MISSING", $"state directory not found: {path}");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(text, Options);
            if (value is null)
                throw new LabException("STATE_CORRUPT", $"state file is empty: {path}");
            return value;
        }
        catch (JsonException ex)
        {
            throw new LabException("STATE_CORRUPT", $"cannot parse {path}: {ex.Message}");
        }
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Atomic-ish replace: a killed process must not leave half a state file.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }

    public static byte[] ToUtf8<T>(T value, bool pretty = false) =>
        JsonSerializer.SerializeToUtf8Bytes(value, pretty ? Options : Compact);

    public static string ToText<T>(T value, bool pretty = false) =>
        JsonSerializer.Serialize(value, pretty ? Options : Compact);
}
