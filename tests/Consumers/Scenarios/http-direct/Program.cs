using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using SmartPipe.Core;
using SmartPipe.Extensions.Http;

// A real loopback HTTP server drives the socket transport, so trimmed and NativeAOT
// publishes exercise SocketsHttpHandler rather than an in-process handler.
await using var server = new LoopbackHttpServer(request => request switch
{
    { Method: "GET", Path: "/source" } => new LoopbackResponse(200, "37"),
    { Method: "POST", Path: "/sink" } => new LoopbackResponse(202, ""),
    _ => new LoopbackResponse(404, ""),
});

var sourceHandler = new CountingHandler(new SocketsHttpHandler());
var sourceClient = new HttpClient(sourceHandler) { BaseAddress = server.BaseAddress };

var sinkHandler = new CountingHandler(new SocketsHttpHandler());
var clientFactory = new SingleClientFactory("consumer-sink", sinkHandler, server.BaseAddress);
var sourceRequestFactoryCalls = 0;
var sinkRequestFactoryCalls = 0;

var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp(
        new PipelineKey("consumer-http-direct"),
        sourceClient,
        (_, _) =>
        {
            sourceRequestFactoryCalls++;
            return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "/source"));
        },
        ReadIntegerAsync)
    .ToHttp(
        clientFactory,
        "consumer-sink",
        (envelope, _) =>
        {
            sinkRequestFactoryCalls++;
            return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "/sink")
            {
                Content = new StringContent(envelope.Payload.ToString(CultureInfo.InvariantCulture)),
            });
        });

await using (var run = await definition.StartAsync().ConfigureAwait(false))
{
    // Sink-backed runs publish only failures by default; successful items are observed at the sink.
    await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
    {
        if (!output.Result.IsSuccess)
            throw new InvalidOperationException("The direct HTTP pipeline emitted a failed result.");
    }

    await run.Completion.ConfigureAwait(false);
}

var requests = server.Requests;
if (requests.Count != 2 || requests[0].Path != "/source" || requests[1].Path != "/sink" || requests[1].Body != "37")
    throw new InvalidOperationException("The loopback server did not observe the expected requests.");
if (sourceHandler.SendCount != 1 || sourceHandler.DisposeCount != 0)
    throw new InvalidOperationException("The source must send once and leave its application-owned handler alive.");
if (clientFactory.CreateCount != 1 || sinkHandler.SendCount != 1 || sinkHandler.DisposeCount != 1)
    throw new InvalidOperationException("The factory sink must create and dispose one client after one send.");
if (sourceRequestFactoryCalls != 1 || sinkRequestFactoryCalls != 1)
    throw new InvalidOperationException("The HTTP request factories were not called exactly once.");

// A direct client remains usable after the adapter finishes; the adapter owns factory clients.
using (var borrowedResponse = await sourceClient.GetAsync("/source").ConfigureAwait(false))
{
    if (borrowedResponse.StatusCode != HttpStatusCode.OK || sourceHandler.SendCount != 2)
        throw new InvalidOperationException("The source adapter disposed the borrowed HttpClient.");
}

try
{
    await clientFactory.CreatedClient!.GetAsync("/after-dispose").ConfigureAwait(false);
    throw new InvalidOperationException("The sink adapter did not dispose its factory-created HttpClient.");
}
catch (ObjectDisposedException)
{
    // Expected: the factory-created client is owned by the sink adapter.
}

sourceClient.Dispose();
if (sourceHandler.DisposeCount != 1)
    throw new InvalidOperationException("The application-owned source client did not dispose its handler.");

Console.WriteLine("CONSUMER_OK http-direct");
return 0;

static async IAsyncEnumerable<int> ReadIntegerAsync(
    HttpResponseMessage response,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    yield return int.Parse(body, CultureInfo.InvariantCulture);
}

sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public int SendCount { get; private set; }
    public int DisposeCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SendCount++;
        return base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCount++;
        base.Dispose(disposing);
    }
}

sealed class SingleClientFactory(string expectedName, HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
{
    public int CreateCount { get; private set; }
    public HttpClient? CreatedClient { get; private set; }

    public HttpClient CreateClient(string name)
    {
        if (name != expectedName)
            throw new InvalidOperationException($"Unexpected HTTP client name: {name}");

        CreateCount++;
        CreatedClient = new HttpClient(handler) { BaseAddress = baseAddress };
        return CreatedClient;
    }
}

sealed record LoopbackRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

sealed record LoopbackResponse(int StatusCode, string Body, string ContentType = "text/plain; charset=utf-8");

/// <summary>A minimal HTTP/1.1 server on 127.0.0.1 that answers one request per connection.</summary>
sealed class LoopbackHttpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<LoopbackRequest, LoopbackResponse> _respond;
    private readonly List<LoopbackRequest> _requests = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public LoopbackHttpServer(Func<LoopbackRequest, LoopbackResponse> respond)
    {
        _respond = respond;
        _listener.Start();
        BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _acceptLoop = AcceptAsync();
    }

    public Uri BaseAddress { get; }

    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToArray();
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient connection;
            try
            {
                connection = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                // Stop() may surface as cancellation or as a socket error; either ends the loop.
                return;
            }

            using (connection)
                await HandleAsync(connection.GetStream()).ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NetworkStream stream)
    {
        var reader = new AsciiReader(stream);
        var requestLine = (await reader.ReadLineAsync().ConfigureAwait(false)).Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var line = await reader.ReadLineAsync().ConfigureAwait(false); line.Length != 0; line = await reader.ReadLineAsync().ConfigureAwait(false))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        byte[] body;
        if (headers.TryGetValue("Content-Length", out var length))
        {
            body = await reader.ReadExactAsync(int.Parse(length, CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            using var chunks = new MemoryStream();
            while (true)
            {
                var size = int.Parse(await reader.ReadLineAsync().ConfigureAwait(false), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (size == 0)
                {
                    await reader.ReadLineAsync().ConfigureAwait(false);
                    break;
                }

                chunks.Write(await reader.ReadExactAsync(size).ConfigureAwait(false));
                await reader.ReadLineAsync().ConfigureAwait(false);
            }

            body = chunks.ToArray();
        }
        else
        {
            body = [];
        }

        var request = new LoopbackRequest(requestLine[0], requestLine[1], headers, Encoding.UTF8.GetString(body));
        lock (_requests)
            _requests.Add(request);

        var response = _respond(request);
        var payload = Encoding.UTF8.GetBytes(response.Body);
        var head = $"HTTP/1.1 {response.StatusCode} Loopback\r\nContent-Type: {response.ContentType}\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(payload).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class AsciiReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        public async Task<string> ReadLineAsync()
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_start == _end && !await FillAsync().ConfigureAwait(false))
                    throw new IOException("The connection closed inside an HTTP line.");

                var value = (char)_buffer[_start++];
                if (value == '\n')
                    return line.ToString().TrimEnd('\r');
                line.Append(value);
            }
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var result = new byte[count];
            var written = 0;
            while (written < count)
            {
                if (_start == _end && !await FillAsync().ConfigureAwait(false))
                    throw new IOException("The connection closed inside an HTTP body.");

                var take = Math.Min(count - written, _end - _start);
                Array.Copy(_buffer, _start, result, written, take);
                _start += take;
                written += take;
            }

            return result;
        }

        private async Task<bool> FillAsync()
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer).ConfigureAwait(false);
            return _end > 0;
        }
    }
}
