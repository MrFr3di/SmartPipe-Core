#nullable enable
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Polly;
using Polly.Retry;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sinks;

public partial class HttpSinkTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenHttpClientIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpSink<object>(null!, "http://test.com"));
    }

    [Fact]
    public async Task WriteAsync_PostsToEndpoint_SingleItem()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(client, "http://test.com");

        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });
        await sink.WriteAsync(result);

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task WriteAsync_ThrowsHttpRequestException_WhenResponseIsNotSuccessful()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(client, "http://test.com");

        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => sink.WriteAsync(result).AsTask());

        Assert.Equal(System.Net.HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task WriteAsync_DoesNotPost_WhenPayloadIsNull()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem?>(client, "http://test.com");

        var result = ProcessingEnvelope<TestItem?>.Create(null);
        await sink.WriteAsync(result);

        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task InitializeAsync_ReturnsCompletedTask()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(client, "http://test.com");

        // InitializeAsync should complete without exception
        await sink.InitializeAsync();
    }

    [Fact]
    public async Task WriteAsync_WithResiliencePipeline_CallsPipeline()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var client = new HttpClient(mockHandler.Object);

        // Create a simple resilience pipeline
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 1
            })
            .Build();

        var sink = new HttpSink<TestItem>(client, "http://test.com", pipeline);

        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });
        await sink.WriteAsync(result);

        // Pipeline should have been called, which means HTTP call was made
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }

    [Fact]
    public async Task WriteAsync_DoesNotRetryHttpExceptionsWithoutResiliencePipeline()
    {
        var attempts = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                attempts++;
                throw new HttpRequestException("transient failure");
            });

        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(client, "http://test.com");
        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });

        await Assert.ThrowsAsync<HttpRequestException>(() => sink.WriteAsync(result).AsTask());

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WriteAsync_UsesOnlyConfiguredResilienceRetryBudget()
    {
        var attempts = 0;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns(() =>
            {
                attempts++;
                if (attempts < 3)
                    throw new HttpRequestException("transient failure");

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            });

        var client = new HttpClient(mockHandler.Object);
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2
            })
            .Build();
        var sink = new HttpSink<TestItem>(client, "http://test.com", pipeline);
        var result = ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" });

        await sink.WriteAsync(result);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task WriteAsync_UsesJsonTypeInfo()
    {
        string? body = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(async (message, _) =>
            {
                body = await message.Content!.ReadAsStringAsync();
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            });

        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(
            client,
            "http://test.com",
            HttpSinkTestJsonContext.Default.TestItem);

        await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" }));

        Assert.Contains("\"Value\":\"test\"", body);
    }

    [Fact]
    public async Task WriteAsync_CanAttachIdempotencyKeyFromTraceId()
    {
        HttpRequestMessage? request = null;
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((message, _) => request = message)
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));

        var client = new HttpClient(mockHandler.Object);
        var sink = new HttpSink<TestItem>(
            client,
            "http://test.com",
            resilience: null,
            useTraceIdIdempotencyKey: true);

        await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(
            new TestItem { Value = "test" },
            "pipeline",
            "run",
            42));

        Assert.NotNull(request);
        Assert.True(request!.Headers.TryGetValues("Idempotency-Key", out var values));
        Assert.Equal("42", Assert.Single(values));
    }

    [Fact]
    public async Task WriteAsync_UsesHttpClientFactory()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var client = new HttpClient(mockHandler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("orders")).Returns(client);
        var sink = new HttpClientFactorySink<TestItem>(
            factory.Object,
            "http://test.com",
            clientName: "orders");

        await sink.WriteAsync(ProcessingEnvelope<TestItem>.Create(new TestItem { Value = "test" }));

        factory.Verify(x => x.CreateClient("orders"), Times.Once);
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    private class TestItem
    {
        public string? Value { get; set; }
    }

    [JsonSerializable(typeof(TestItem))]
    private sealed partial class HttpSinkTestJsonContext : JsonSerializerContext;
}
