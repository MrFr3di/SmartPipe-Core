using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http.Json.Tests;

public sealed class HttpJsonCompositionTests
{
    [Fact]
    public async Task Post_CreatesAFreshJsonRequestForEachWrite()
    {
        var factory = HttpJsonRequestContent.Post(
            new Uri("https://example.test/orders"),
            CompositionJsonContext.Default.CompositionOrder);

        using var first = await factory(Envelope(new CompositionOrder(1, "first")), CancellationToken.None);
        using var second = await factory(Envelope(new CompositionOrder(2, "second")), CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Equal(new Uri("https://example.test/orders"), first.RequestUri);
        Assert.Equal("application/json", first.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"Id\":1,\"Name\":\"first\"}", await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("{\"Id\":2,\"Name\":\"second\"}", await second.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Post_ObservesCancellationBeforeCreatingARequest()
    {
        var factory = HttpJsonRequestContent.Post(
            new Uri("https://example.test/orders"),
            CompositionJsonContext.Default.CompositionOrder);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => factory(Envelope(new CompositionOrder(1, "first")), cancellation.Token).AsTask());
    }

    [Fact]
    public void Factories_RejectNullMetadataAndUri()
    {
        Assert.Throws<ArgumentNullException>(() => HttpJsonResponseReaders.JsonArray<int>(null!));
        Assert.Throws<ArgumentNullException>(() => HttpJsonResponseReaders.Ndjson<int>(null!));
        Assert.Throws<ArgumentNullException>(() => HttpJsonRequestContent.Post(null!, CompositionJsonContext.Default.CompositionOrder));
        Assert.Throws<ArgumentNullException>(() => HttpJsonRequestContent.Post<CompositionOrder>(new Uri("https://example.test/"), null!));
    }

    [Fact]
    public async Task Readers_SnapshotMetadataPreserveConvertersAndLeaveCallerOptionsUnchanged()
    {
        var callerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = CompositionJsonContext.Default,
            MaxDepth = 32,
            Converters = { new UpperCaseStringConverter() },
        };
        var callerTypeInfo = (JsonTypeInfo<string>)callerOptions.GetTypeInfo(typeof(string));

        var arrayReader = HttpJsonResponseReaders.JsonArray(callerTypeInfo, new HttpJsonArrayOptions { MaxDepth = 4 });
        var ndjsonReader = HttpJsonResponseReaders.Ndjson(callerTypeInfo, new HttpNdjsonOptions { MaxDepth = 4 });

        Assert.Equal(32, callerOptions.MaxDepth);
        Assert.Same(callerOptions, callerTypeInfo.Options);

        using var arrayResponse = Response("[\"a\",\"b\"]");
        Assert.Equal(["A", "B"], await ReadAllAsync(arrayReader, arrayResponse));
        using var ndjsonResponse = Response("\"c\"\n\"d\"\n");
        Assert.Equal(["C", "D"], await ReadAllAsync(ndjsonReader, ndjsonResponse));
        Assert.Equal(32, callerOptions.MaxDepth);
    }

    [Fact]
    public void Readers_RejectMetadataWithoutAResolver()
    {
        var typeInfo = JsonMetadataServices.CreateValueInfo<int>(new JsonSerializerOptions(), JsonMetadataServices.Int32Converter);

        Assert.Throws<ArgumentException>(() => HttpJsonResponseReaders.JsonArray(typeInfo));
    }

    [Fact]
    public async Task Builders_ComposeWithoutIoAndRunNdjsonSourceIntoJsonSink()
    {
        var sourceHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"Id\":1,\"Name\":\"one\"}\n{\"Id\":2,\"Name\":\"two\"}\n", Encoding.UTF8),
        });
        var sinkHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted));
        using var sourceClient = new HttpClient(sourceHandler);
        using var sinkClient = new HttpClient(sinkHandler);

        var definition = HttpJsonPipelineDefinitionBuilderExtensions
            .FromHttpNdjson(
                new PipelineKey("http-json-builder"),
                sourceClient,
                static (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/orders")),
                CompositionJsonContext.Default.CompositionOrder)
            .ToHttpJson(
                sinkClient,
                new Uri("https://example.test/archive"),
                CompositionJsonContext.Default.CompositionOrder,
                static envelope => $"order-{envelope.Payload.Id}");

        Assert.Equal("http-json-builder", definition.Key.Value);
        Assert.Empty(sourceHandler.Requests);
        Assert.Empty(sinkHandler.Requests);

        await using (var run = await definition.StartAsync(TestContext.Current.CancellationToken))
        {
            await foreach (var _ in run.Outputs.ReadAllAsync(TestContext.Current.CancellationToken))
            {
            }

            await run.Completion;
        }

        Assert.Single(sourceHandler.Requests);
        Assert.Equal(["order-1", "order-2"], sinkHandler.Requests.Select(request => request.IdempotencyKey));
        Assert.Equal(
            ["{\"Id\":1,\"Name\":\"one\"}", "{\"Id\":2,\"Name\":\"two\"}"],
            sinkHandler.Requests.Select(request => request.Body));
    }

    private static ProcessingEnvelope<T> Envelope<T>(T payload) =>
        ProcessingEnvelope<T>.Create(payload, "http-json-tests", "run", 1);

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private static async Task<List<T>> ReadAllAsync<T>(HttpResponseReader<T> reader, HttpResponseMessage response)
    {
        var values = new List<T>();
        await foreach (var value in reader(response, TestContext.Current.CancellationToken))
            values.Add(value);
        return values;
    }

    private sealed record RecordedRequest(string? IdempotencyKey, string? Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(key, body));
            return respond(request);
        }
    }

    private sealed class UpperCaseStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString()!.ToUpperInvariant();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}

internal sealed record CompositionOrder(int Id, string Name);

[JsonSerializable(typeof(CompositionOrder))]
[JsonSerializable(typeof(string))]
internal sealed partial class CompositionJsonContext : JsonSerializerContext;
