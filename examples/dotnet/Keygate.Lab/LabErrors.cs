using System.Text.Json;

namespace Keygate.Lab;

/// <summary>
/// A local, actionable failure (bad state, tampered file, no token, ...).
/// The Code is stable and printed by the CLI so tests can assert on it.
/// </summary>
public sealed class LabException : Exception
{
    public string Code { get; }

    public LabException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// An error envelope returned by a Keygate server (mock or real).
/// Mirrors Keygate.Sample's exception so the two examples behave the same.
/// </summary>
public sealed class KeygateApiException : Exception
{
    public int Status { get; }
    public string Code { get; }
    public JsonElement? Details { get; }

    public bool IsNetwork => Code == "NETWORK_ERROR";

    public KeygateApiException(int status, string code, string message, JsonElement? details = null)
        : base(message)
    {
        Status = status;
        Code = code;
        Details = details;
    }

    /// <summary>Actionable hint for each documented SDK error code.</summary>
    public string Friendly() => Code switch
    {
        "LICENSE_NOT_FOUND" =>
            "The key is unknown, unusable, or this identifier is not activated. " +
            "After a machine change the identifier changes, so ask support to release the old binding " +
            "(strict mode) or activate again in tolerant mode.",
        "DEVICE_CHANGED" =>
            "Strict device binding allows one device per licence. Deactivate the old device in the admin " +
            "panel, or run 'server reset-binding' in mock mode, then activate the new identifier.",
        "DEACTIVATION_DISABLED" =>
            "Strict device binding disables client-side deactivation on purpose. " +
            "Release the device from the admin panel (mock: 'server reset-binding').",
        "IDENTIFIER_SCHEME_UNSUPPORTED" =>
            "Strict mode needs a hardware-derived identifier: kgid:v1:<platform>:<64hex> or sha256:<64hex>. " +
            "A random UUID/opaque identifier cannot detect a machine change.",
        "ACTIVATION_LIMIT" =>
            "All activation slots are in use. Deactivate another device (or run 'server reset' in mock mode) and retry.",
        "FEATURE_NOT_AVAILABLE" => "This product does not support device activation.",
        "LOCKED_OUT" => "Too many failed attempts. Wait for the retry window and try again.",
        "NETWORK_ERROR" =>
            "The server is unreachable. Check --server, or omit it to use the built-in mock.",
        "UPDATES_EXPIRED" => "The maintenance period ended; this release is not downloadable.",
        "RELEASE_YANKED" => "That release was withdrawn. Pick another version.",
        "PLATFORM_NOT_AVAILABLE" => "No artifact exists for this platform.",
        "STORAGE_DISABLED" => "Release storage is not configured on the server.",
        "BAD_RESPONSE" => "The endpoint did not return a Keygate JSON envelope. Is --server pointing at Keygate?",
        _ => Message,
    };
}
