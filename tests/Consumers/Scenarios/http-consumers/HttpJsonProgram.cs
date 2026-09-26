using System.Text.Json;
using SmartPipe.Consumers.Http;
using SmartPipe.Core;
using SmartPipe.Extensions.Http.Json;

const string OrdersJsonPath = "/orders.json";

// A real loopback HTTP server drives the socket transport, so trimmed and NativeAOT
// publishes exercise SocketsHttpHandler and the source-generated JSON codecs together.
await using var server = new LoopbackHttpServer(request => request switch
{
    { Method: "GET", Path: "/orders.ndjson" } => new LoopbackResponse(
        200,
        "{\"Id\":1,\"Name\":\"first\"}\r\n\n{\"Id\":2,\"Name\":\"second\"}",
        "application/x-ndjson"),
    { Method: "GET", Path: OrdersJsonPath } => new LoopbackResponse(
        200,
        "[{\"Id\":3,\"Name\":\"third\"}]",
        "application/json"),
    { Method: "POST", Path: "/orders" } => new LoopbackResponse(202, ""),
    _ => new LoopbackResponse(404, ""),
});

using var sourceClient = new HttpClient(new SocketsHttpHandler()) { BaseAddress = server.BaseAddress };
using var sinkHandler = new SocketsHttpHandler();
var clientFactory = new NamedClientFactory("orders", sinkHandler, server.BaseAddress, disposeHandler: false);

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

await ConsumerPipeline.RunToCompletionAsync(definition, "The HTTP JSON pipeline emitted a failed result.")
    .ConfigureAwait(false);

var posts = server.Requests.Where(request => request.Method == "POST").ToArray();
var postedOrders = posts
    .Select(request => JsonSerializer.Deserialize(request.Body, ConsumerJsonContext.Default.ConsumerOrder)
        ?? throw new InvalidOperationException("The HTTP JSON sink posted a null payload."))
    .ToArray();
ConsumerCheck.Require(
    server.Requests.Count(request => request.Method == "GET") == 1 && posts.Length == 2 && clientFactory.CreateCount == 2,
    "The HTTP JSON pipeline did not send the expected requests.");
ConsumerCheck.Require(
    postedOrders.Select(order => order.Name).SequenceEqual(["first", "second"])
        && posts.Select(request => request.Headers["Idempotency-Key"]).SequenceEqual(["order-1", "order-2"])
        && posts.All(request => request.Headers["Content-Type"].StartsWith("application/json", StringComparison.Ordinal)),
    "The HTTP JSON sink posted unexpected payloads, headers, or idempotency keys.");

var arrayReader = HttpJsonResponseReaders.JsonArray(
    ConsumerJsonContext.Default.ConsumerOrder,
    new HttpJsonArrayOptions { MaxUnframedBytes = 1024 });
using (var arrayResponse = await sourceClient.GetAsync(OrdersJsonPath, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
{
    var arrayValues = new List<ConsumerOrder>();
    await foreach (var order in arrayReader(arrayResponse, CancellationToken.None).ConfigureAwait(false))
        arrayValues.Add(order);
    ConsumerCheck.Require(
        arrayValues.SequenceEqual([new ConsumerOrder(3, "third")]),
        "The HTTP JSON array reader produced an unexpected value.");
}

var boundedReader = HttpJsonResponseReaders.JsonArray(
    ConsumerJsonContext.Default.ConsumerOrder,
    new HttpJsonArrayOptions { MaxUnframedBytes = 8 });
using (var oversizedResponse = await sourceClient.GetAsync(OrdersJsonPath, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
{
    var boundedItems = 0;
    try
    {
        await foreach (var _ in boundedReader(oversizedResponse, CancellationToken.None).ConfigureAwait(false))
            boundedItems++;

        throw new InvalidOperationException(
            $"The HTTP JSON array reader did not enforce its byte limit after {boundedItems} item(s).");
    }
    catch (JsonException)
    {
        // Expected: the total body bound is enforced while reading.
    }
}

Console.WriteLine("CONSUMER_OK http-json-direct");
return 0;
