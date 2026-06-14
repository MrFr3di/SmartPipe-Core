#nullable enable
using System.Net.Http;
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

public class HttpSinkTests
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

    private class TestItem
    {
        public string? Value { get; set; }
    }
}
