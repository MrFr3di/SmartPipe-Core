using System.Globalization;
using System.Runtime.CompilerServices;
using SmartPipe.Consumers.Http;
using SmartPipe.Core;
using SmartPipe.Extensions.Http;

const string SourcePath = "/source";
const string SinkPath = "/sink";

// A real loopback HTTP server drives the socket transport, so trimmed and NativeAOT
// publishes exercise SocketsHttpHandler rather than an in-process handler.
await using var server = new LoopbackHttpServer(request => request switch
{
    { Method: "GET", Path: SourcePath } => new LoopbackResponse(200, "37"),
    { Method: "POST", Path: SinkPath } => new LoopbackResponse(202, ""),
    _ => new LoopbackResponse(404, ""),
});

var sourceHandler = new CountingHandler(new SocketsHttpHandler());
var sourceClient = new HttpClient(sourceHandler) { BaseAddress = server.BaseAddress };

var sinkHandler = new CountingHandler(new SocketsHttpHandler());
var clientFactory = new NamedClientFactory("consumer-sink", sinkHandler, server.BaseAddress, disposeHandler: true);
var sourceRequestFactoryCalls = 0;
var sinkRequestFactoryCalls = 0;

var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp(
        new PipelineKey("consumer-http-direct"),
        sourceClient,
        (_, _) =>
        {
            sourceRequestFactoryCalls++;
            return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, SourcePath));
        },
        ReadIntegerAsync)
    .ToHttp(
        clientFactory,
        "consumer-sink",
        (envelope, _) =>
        {
            sinkRequestFactoryCalls++;
            return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, SinkPath)
            {
                Content = new StringContent(envelope.Payload.ToString(CultureInfo.InvariantCulture)),
            });
        });

await ConsumerPipeline.RunToCompletionAsync(definition, "The direct HTTP pipeline emitted a failed result.")
    .ConfigureAwait(false);

HttpConsumerChecks.VerifyRequests(server.Requests, SourcePath, SinkPath);
HttpConsumerChecks.VerifyAdapterOwnership(sourceHandler, sinkHandler, clientFactory);
ConsumerCheck.Require(
    sourceRequestFactoryCalls == 1 && sinkRequestFactoryCalls == 1,
    "The HTTP request factories were not called exactly once.");

// A direct client remains usable after the adapter finishes; the adapter owns factory clients.
await HttpConsumerChecks.VerifyBorrowedClientAliveAsync(sourceClient, sourceHandler, SourcePath).ConfigureAwait(false);
await HttpConsumerChecks.VerifyFactoryClientDisposedAsync(clientFactory).ConfigureAwait(false);

sourceClient.Dispose();
ConsumerCheck.Require(
    sourceHandler.DisposeCount == 1,
    "The application-owned source client did not dispose its handler.");

Console.WriteLine("CONSUMER_OK http-direct");
return 0;

static async IAsyncEnumerable<int> ReadIntegerAsync(
    HttpResponseMessage response,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    yield return int.Parse(body, CultureInfo.InvariantCulture);
}
