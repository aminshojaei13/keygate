using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Keygate.Lab;

public sealed class MiniRequest
{
    public string Method { get; }
    public string Path { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public byte[] Body { get; }

    public MiniRequest(string method, string path, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Path = path;
        Headers = headers;
        Body = body;
    }

    public string BodyText => Encoding.UTF8.GetString(Body);
}

public sealed class MiniResponse
{
    public int Status { get; init; } = 200;
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/json; charset=utf-8";
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// A deliberately tiny HTTP/1.1 server on a loopback TcpListener. It exists so
/// the lab can be a single self-contained process: no ASP.NET Core, no HTTP.sys
/// URL ACLs, no fixed port, no external server. One request per connection.
/// </summary>
public sealed class MiniHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<MiniRequest, MiniResponse> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";

    public MiniHttpServer(Func<MiniRequest, MiniResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            try
            {
                client.ReceiveTimeout = 5_000;
                client.SendTimeout = 5_000;

                var head = ReadHead(stream);
                if (head is null) return;

                var (method, path, headers) = head.Value;

                if (headers.TryGetValue("Expect", out var expect) &&
                    expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                {
                    var cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(cont, 0, cont.Length);
                    stream.Flush();
                }

                var body = ReadBody(stream, headers);
                if (body is null) return;

                var response = _handler(new MiniRequest(method, path, headers, body));
                WriteResponse(stream, response);
            }
            catch (IOException) { /* client went away */ }
            catch (SocketException) { /* client went away */ }
            catch (ObjectDisposedException) { /* shutting down */ }
            catch (Exception)
            {
                // A bad request must never take the mock server down.
                TryWriteError(stream);
            }
        }
    }

    private static (string Method, string Path, Dictionary<string, string> Headers)? ReadHead(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        var headerEnd = -1;

        while (buffer.Length < MaxHeaderBytes)
        {
            var n = stream.Read(one, 0, 1);
            if (n <= 0) return null;
            buffer.WriteByte(one[0]);

            if (buffer.Length < 4) continue;
            var data = buffer.GetBuffer();
            var len = (int)buffer.Length;
            if (data[len - 4] == '\r' && data[len - 3] == '\n' &&
                data[len - 2] == '\r' && data[len - 1] == '\n')
            {
                headerEnd = len;
                break;
            }
        }

        if (headerEnd < 0) return null;

        var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return null;

        var method = requestLine[0].ToUpperInvariant();
        var path = requestLine[1];
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return (method, path, headers);
    }

    private static byte[]? ReadBody(NetworkStream stream, IReadOnlyDictionary<string, string> headers)
    {
        var length = 0;
        if (headers.TryGetValue("Content-Length", out var raw) &&
            !int.TryParse(raw, out length))
        {
            return null;
        }

        if (length < 0 || length > MaxBodyBytes) return null;
        if (length == 0) return Array.Empty<byte>();

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(body, read, length - read);
            if (n <= 0) return null;
            read += n;
        }
        return body;
    }

    private static void WriteResponse(NetworkStream stream, MiniResponse response)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
            .Append(Reason(response.Status)).Append("\r\n");
        head.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        head.Append("Connection: close\r\n");
        head.Append("X-Powered-By: Keygate\r\n");
        foreach (var (key, value) in response.Headers)
            head.Append(key).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");

        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (response.Body.Length > 0)
            stream.Write(response.Body, 0, response.Body.Length);
        stream.Flush();
    }

    private static void TryWriteError(NetworkStream stream)
    {
        try
        {
            WriteResponse(stream, new MiniResponse
            {
                Status = 500,
                Body = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":{\"code\":\"INTERNAL\",\"message\":\"internal mock server error\"}}"),
            });
        }
        catch
        {
            // nothing left to do
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "OK",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _acceptLoop.Wait(2_000); } catch { /* shutdown race */ }
        _cts.Dispose();
    }
}
