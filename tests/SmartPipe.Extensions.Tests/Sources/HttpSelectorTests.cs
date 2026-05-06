#nullable enable
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Polly;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests.Sources;

public class HttpSelectorTests
{
    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenHttpClientIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpSelector<string>(null!, "http://test.com"));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenRequestUriIsNull()
    {
        var client = new HttpClient();
        Assert.Throws<ArgumentNullException>(() => new HttpSelector<string>(client, null!));
    }

    [Fact]
    public void Constructor_WithLogger_SetsProperties()
    {
        var client = new HttpClient();
        var mockLogger = new Mock<ILogger<HttpSelector<string>>>();
        
        var selector = new HttpSelector<string>(client, "http://test.com", logger: mockLogger.Object);
        
        Assert.NotNull(selector);
    }

    [Fact]
    public void Constructor_WithResiliencePipeline_SetsProperties()
    {
        var client = new HttpClient();
        var pipeline = new ResiliencePipelineBuilder().Build(); // Empty pipeline
        
        var selector = new HttpSelector<string>(client, "http://test.com", pipeline: pipeline);
        
        Assert.NotNull(selector);
    }

    [Fact]
    public async Task ReadAsync_ReturnsContent_ForValidUrl()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"test\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");

        var items = new List<ProcessingContext<string>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal("test", items[0].Payload);
    }

    [Fact]
    public async Task ReadAsync_ThrowsHttpRequestException_ForNotFound()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.NotFound
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var item in selector.ReadAsync()) { }
        });
    }

    [Fact]
    public async Task ReadAsync_SendsCorrectUri()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                Assert.Equal("http://test.com/", req.RequestUri!.ToString());
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");

        await foreach (var item in selector.ReadAsync()) { }
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenResponseIsNull()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("null")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string?>(client, "http://test.com");

        var items = new List<ProcessingContext<string?>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmpty_WhenResponseIsEmptyArray()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");

        var items = new List<ProcessingContext<string>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Empty(items);
    }

    [Fact]
    public async Task ReadAsync_WithResiliencePipeline_ExecutesPipeline()
    {
        var callCount = 0;
        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new()
            {
                MaxRetryAttempts = 3,
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>()
            })
            .Build();

        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                callCount++;
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"test\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com", pipeline: pipeline);

        var items = new List<ProcessingContext<string>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal("test", items[0].Payload);
        Assert.True(callCount > 0);
    }

    [Fact]
    public async Task ReadAsync_LogsInformation_WhenLoggerProvided()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"test\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var mockLogger = new Mock<ILogger<HttpSelector<string>>>();
        var selector = new HttpSelector<string>(client, "http://test.com", logger: mockLogger.Object);

        await foreach (var item in selector.ReadAsync()) { }

        // Verify that LogInformation was called at least once
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReadAsync_ThrowsCancellation_WhenTokenCancelled()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"test\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in selector.ReadAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutError()
    {
        var client = new HttpClient();
        var selector = new HttpSelector<string>(client, "http://test.com");
        
        await selector.DisposeAsync();
        
        Assert.True(true); // If we get here, test passed
    }

    [Fact]
    public async Task ReadAsync_WithMultipleItems_ReturnsAllItems()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"item1\",\"item2\",\"item3\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(client, "http://test.com");

        var items = new List<ProcessingContext<string>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Equal(3, items.Count);
        Assert.Equal("item1", items[0].Payload);
        Assert.Equal("item2", items[1].Payload);
        Assert.Equal("item3", items[2].Payload);
    }

    [Fact]
    public async Task ReadAsync_WithComplexType_DeserializesCorrectly()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", 
                ItExpr.IsAny<HttpRequestMessage>(), 
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[{\"Id\":1,\"Name\":\"Test\"}]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<TestComplexType>(client, "http://test.com");

        var items = new List<ProcessingContext<TestComplexType>>();
        await foreach (var item in selector.ReadAsync())
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal(1, items[0].Payload?.Id);
        Assert.Equal("Test", items[0].Payload?.Name);
    }

    private class TestComplexType
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
