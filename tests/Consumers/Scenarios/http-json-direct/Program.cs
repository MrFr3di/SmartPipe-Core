using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.Core;
using SmartPipe.Extensions.Http.Json;

// A real loopback HTTP server drives the socket transport, so trimmed and NativeAOT
// publishes exercise SocketsHttpHandler and the source-generated JSON codecs together.
await using var server = new LoopbackHttpServer(request => request switch
{
    { Method: "GET", Path: "/orders.ndjson" } => new LoopbackResponse(
        200,
        "{\"Id\":1,\"Name\":\"first\"}\r\n\n{\"Id\":2,\"Name\":\"second\"}",
        "application/x-ndjson"),
    { Method: "GET", Path: "/orders.json" } => new LoopbackResponse(
        200,
        "[{\"Id\":3,\"Name\":\"third\"}]",
        "application/json"),
    { Method: "POST", Path: "/orders" } => new LoopbackResponse(202, ""),
    _ => new LoopbackResponse(404, ""),
});

using var sourceClient = new HttpClient(new SocketsHttpHandler()) { BaseAddress = server.BaseAddress };
using var sinkHandler = new SocketsHttpHandler();
var clientFactory = new HandlerClientFactory("orders", sinkHandler, server.BaseAddress);

var definition = HttpJsonPipelineDefinitionBuilderExtensions.FromHttpNdjson(
        new PipelineKey("consumer-http-json-direct"),
        sourceClient,
        (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "/orders.ndjson")),
        ConsumerJsonContext.Default.ConsumerOrder)
    .ToHttpJson(
        clientFactory,
        "orders",
        new Uri(server.BaseAddress, "/orders"),
        ConsumerJsonContext.Default.ConsumerOrder,
        envelope => $"order-{envelope.Payload.Id}");

await using (var run = await definition.StartAsync().ConfigureAwait(false))
{
    // Sink-backed runs publish only failures by default; successful items are observed at the sink.
    await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
    {
        if (!output.Result.IsSuccess)
            throw new InvalidOperationException("The HTTP JSON pipeline emitted a failed result.");
    }

    await run.Completion.ConfigureAwait(false);
}

var posts = server.Requests.Where(request => request.Method == "POST").ToArray();
var postedOrders = posts
    .Select(request => JsonSerializer.Deserialize(request.Body, ConsumerJsonContext.Default.ConsumerOrder)
        ?? throw new InvalidOperationException("The HTTP JSON sink posted a null payload."))
    .ToArray();
if (server.Requests.Count(request => request.Method == "GET") != 1 || posts.Length != 2 || clientFactory.CreateCount != 2)
    throw new InvalidOperationException("The HTTP JSON pipeline did not send the expected requests.");
if (!postedOrders.Select(order => order.Name).SequenceEqual(["first", "second"])
    || !posts.Select(request => request.Headers["Idempotency-Key"]).SequenceEqual(["order-1", "order-2"])
    || posts.Any(request => !request.Headers["Content-Type"].StartsWith("application/json", StringComparison.Ordinal)))
    throw new InvalidOperationException("The HTTP JSON sink posted unexpected payloads, headers, or idempotency keys.");

var arrayReader = HttpJsonResponseReaders.JsonArray(
    ConsumerJsonContext.Default.ConsumerOrder,
    new HttpJsonArrayOptions { MaxUnframedBytes = 1024 });
using (var arrayResponse = await sourceClient.GetAsync("/orders.json", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
{
    var arrayValues = new List<ConsumerOrder>();
    await foreach (var order in arrayReader(arrayResponse, CancellationToken.None).ConfigureAwait(false))
        arrayValues.Add(order);
    if (arrayValues.Count != 1 || arrayValues[0] != new ConsumerOrder(3, "third"))
        throw new InvalidOperationException("The HTTP JSON array reader produced an unexpected value.");
}

var boundedReader = HttpJsonResponseReaders.JsonArray(
    ConsumerJsonContext.Default.ConsumerOrder,
    new HttpJsonArrayOptions { MaxUnframedBytes = 8 });
using (var oversizedResponse = await sourceClient.GetAsync("/orders.json", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
{
    try
    {
        await foreach (var _ in boundedReader(oversizedResponse, CancellationToken.None).ConfigureAwait(false))
        {
        }

        throw new InvalidOperationException("The HTTP JSON array reader did not enforce its byte limit.");
    }
    catch (JsonException)
    {
        // Expected: the total body bound is enforced while reading.
    }
}

Console.WriteLine("CONSUMER_OK http-json-direct");
return 0;

internal sealed record ConsumerOrder(int Id, string Name);

[JsonSerializable(typeof(ConsumerOrder))]
internal sealed partial class ConsumerJsonContext : JsonSerializerContext;

sealed class HandlerClientFactory(string expectedName, HttpMessageHandler handler, Uri baseAddress) : IHttpClientFactory
{
    public int CreateCount { get; private set; }

    public HttpClient CreateClient(string name)
    {
        if (name != expectedName)
            throw new InvalidOperationException($"Unexpected HTTP client name: {name}");

        CreateCount++;
        return new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
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
