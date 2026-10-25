namespace Keygate.Lab;

/// <summary>
/// Either a real Keygate server (--server) or the built-in mock. The mock runs
/// in this process for the duration of one command, so state is persisted on
/// disk and every run is independent.
/// </summary>
public sealed class LabSession : IDisposable
{
    public string Url { get; }
    public MockServer? Mock { get; }

    public bool IsMock => Mock is not null;
    public MockServerState? MockState => Mock?.State;

    private LabSession(string url, MockServer? mock)
    {
        Url = url;
        Mock = mock;
    }

    public static LabSession Start(LabPaths paths, string? serverUrl)
    {
        if (!string.IsNullOrWhiteSpace(serverUrl))
            return new LabSession(LicenseApi.NormalizeBaseUrl(serverUrl), null);

        paths.EnsureRoot();
        var state = MockServerStore.LoadOrCreate(paths.MockState);
        var mock = new MockServer(paths.MockState, state);
        return new LabSession(mock.Url, mock);
    }

    public void Dispose() => Mock?.Dispose();
}
