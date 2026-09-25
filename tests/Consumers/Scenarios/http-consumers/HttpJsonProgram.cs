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
    if (!arrayValues.SequenceEqual([new ConsumerOrder(3, "third")]))
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
