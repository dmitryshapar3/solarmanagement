using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SolarManagement.ProviderE2E;

public sealed record FixtureRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonElement Json
    {
        get { using var document = JsonDocument.Parse(Body); return document.RootElement.Clone(); }
    }
    public string Path => Uri.AbsolutePath;
    public string Header(string key) => Headers.TryGetValue(key, out var value) ? value : "";
}

public sealed record FixtureResponse(int StatusCode, string Body, string ContentType = "application/json")
{
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public static FixtureResponse Json(object body, int statusCode = 200)
        => new(statusCode, JsonSerializer.Serialize(body));
    public static FixtureResponse Raw(string body, int statusCode = 200) => new(statusCode, body);
}

/// <summary>Real HTTP socket server. Each fixture validates requests against its linked vendor specification.</summary>
public sealed class MockVendorServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<FixtureRequest, CancellationToken, Task<FixtureResponse>> _handler;
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _acceptLoop;
    public ConcurrentQueue<FixtureRequest> Requests { get; } = new();
    public Uri BaseUri { get; }
    public string Documentation { get; }

    public MockVendorServer(string documentation, Func<FixtureRequest, FixtureResponse> handler)
        : this(documentation, (request, _) => Task.FromResult(handler(request))) { }

    public MockVendorServer(string documentation, Func<FixtureRequest, CancellationToken, Task<FixtureResponse>> handler)
    {
        if (!Uri.TryCreate(documentation, UriKind.Absolute, out _)) throw new ArgumentException("Fixture requires a vendor specification URL.");
        Documentation = documentation;
        _handler = handler;
        _listener.Start();
        BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _connections.Add(HandleAsync(connection));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient connection)
    {
        using (connection)
        {
            try
            {
                var stream = connection.GetStream();
                var headerBytes = new List<byte>();
                var next = new byte[1];
                while (headerBytes.Count < 65536)
                {
                    await stream.ReadExactlyAsync(next, _shutdown.Token);
                    headerBytes.Add(next[0]);
                    if (headerBytes.Count >= 4 && headerBytes.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
                }
                var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.None);
                var requestLine = lines[0].Split(' ');
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1).Where(line => line.Contains(':')))
                {
                    var split = line.IndexOf(':');
                    headers[line[..split]] = line[(split + 1)..].Trim();
                }
                byte[] bytes;
                if (headers.TryGetValue("Transfer-Encoding", out var encoding))
                {
                    if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported fixture transfer encoding.");
                    using var content = new MemoryStream();
                    while (true)
                    {
                        var sizeLine = await ReadLineAsync(stream, _shutdown.Token);
                        var size = int.Parse(sizeLine.Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                        if (size == 0)
                        {
                            while ((await ReadLineAsync(stream, _shutdown.Token)).Length > 0) { }
                            break;
                        }
                        if (size < 0 || content.Length + size > 1024 * 1024) throw new InvalidDataException("Fixture body is oversized.");
                        var chunk = new byte[size];
                        await stream.ReadExactlyAsync(chunk, _shutdown.Token);
                        await content.WriteAsync(chunk, _shutdown.Token);
                        if ((await ReadLineAsync(stream, _shutdown.Token)).Length != 0) throw new InvalidDataException("Malformed fixture chunk terminator.");
                    }
                    bytes = content.ToArray();
                }
                else
                {
                    var length = headers.TryGetValue("Content-Length", out var text) ? int.Parse(text) : 0;
                    if (length is < 0 or > 1024 * 1024) throw new InvalidDataException("Fixture body is oversized.");
                    bytes = new byte[length];
                    await stream.ReadExactlyAsync(bytes, _shutdown.Token);
                }
                var original = new Uri(headers["X-Fixture-Original-Url"]);
                var request = new FixtureRequest(requestLine[0], original, headers, Encoding.UTF8.GetString(bytes));
                Requests.Enqueue(request);
                FixtureResponse response;
                try { response = await _handler(request, _shutdown.Token); }
                catch (Exception exception) when (!_shutdown.IsCancellationRequested)
                {
                    _failures.Enqueue(exception);
                    response = FixtureResponse.Json(new { fixtureError = "Request violated the documented protocol." }, 500);
                }
                var body = Encoding.UTF8.GetBytes(response.Body);
                var status = response.StatusCode == 200 ? "OK" : "Fixture response";
                var extra = string.Concat((response.Headers ?? new Dictionary<string, string>()).Select(pair =>
                    !pair.Key.Any(char.IsControl) && !pair.Value.Any(char.IsControl) ? pair.Key + ": " + pair.Value + "\r\n"
                        : throw new InvalidDataException("Invalid fixture response header.")));
                var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.StatusCode} {status}\r\nContent-Type: {response.ContentType}\r\n{extra}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(responseHeaders, _shutdown.Token);
                await stream.WriteAsync(body, _shutdown.Token);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (IOException) { /* A cancelled client may close an in-flight connection. */ }
        }
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var next = new byte[1];
        while (bytes.Count < 65536)
        {
            await stream.ReadExactlyAsync(next, ct);
            bytes.Add(next[0]);
            if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10) return Encoding.ASCII.GetString(bytes.Take(bytes.Count - 2).ToArray());
        }
        throw new InvalidDataException("Fixture HTTP line is oversized.");
    }

    public void AssertNoProtocolFailures()
    {
        if (_failures.TryDequeue(out var failure)) throw new InvalidOperationException("Vendor fixture validation failed. Source: " + Documentation, failure);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_connections);
        _shutdown.Dispose();
    }
}
