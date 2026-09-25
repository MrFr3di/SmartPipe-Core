using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
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
