#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Serialization;
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

[Trait("Category", "CorrectnessRegression")]
public partial class HttpSelectorTests
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

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
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
            await foreach (var item in selector.ReadEnvelopesAsync()) { }
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

        await foreach (var item in selector.ReadEnvelopesAsync()) { }
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

        var items = new List<ProcessingEnvelope<string?>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
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

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
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

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
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

        await foreach (var item in selector.ReadEnvelopesAsync()) { }

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
    public async Task ReadAsync_LogsAbsoluteUriWithoutUserInfoQueryOrFragment()
    {
        var client = CreateJsonClient("[]");
        var logger = new ListLogger<HttpSelector<string>>();
        var selector = new HttpSelector<string>(
            client,
            "https://user:password@example.com:8443/orders/list?api_key=secret#frag",
            HttpSelectorTestJsonContext.Default.ListString,
            pipeline: null,
            logger);

        await foreach (var item in selector.ReadEnvelopesAsync()) { }

        var logs = string.Join('\n', logger.Messages);
        Assert.Contains("https://example.com:8443/orders/list", logs);
        Assert.DoesNotContain("user", logs);
        Assert.DoesNotContain("password", logs);
        Assert.DoesNotContain("api_key", logs);
        Assert.DoesNotContain("secret", logs);
        Assert.DoesNotContain("frag", logs);
    }

    [Fact]
    public async Task ReadAsync_LogsRelativeUriWithoutQueryOrFragment()
    {
        var client = CreateJsonClient("[]");
        client.BaseAddress = new Uri("https://example.com");
        var logger = new ListLogger<HttpSelector<string>>();
        var selector = new HttpSelector<string>(
            client,
            "/orders?api_key=secret#frag",
            HttpSelectorTestJsonContext.Default.ListString,
            pipeline: null,
            logger);

        await foreach (var item in selector.ReadEnvelopesAsync()) { }

        var logs = string.Join('\n', logger.Messages);
        Assert.Contains("/orders", logs);
        Assert.DoesNotContain("api_key", logs);
        Assert.DoesNotContain("secret", logs);
        Assert.DoesNotContain("frag", logs);
    }

    [Fact]
    public async Task ReadAsync_LogsUnparseableUriPlaceholderWithoutOriginalValue()
    {
        var logger = new ListLogger<HttpSelector<string>>();
        var selector = new HttpSelector<string>(
            new HttpClient(),
            "https://[::1/orders?api_key=secret#frag",
            HttpSelectorTestJsonContext.Default.ListString,
            pipeline: null,
            logger);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var item in selector.ReadEnvelopesAsync()) { }
        });

        var logs = string.Join('\n', logger.Messages);
        Assert.Contains("[unparseable-uri]", logs);
        Assert.DoesNotContain("https://[::1", logs);
        Assert.DoesNotContain("api_key", logs);
        Assert.DoesNotContain("secret", logs);
        Assert.DoesNotContain("frag", logs);
    }

    [Fact]
    public void ReflectionJsonConstructors_AreAnnotatedForTrimAndAot()
    {
        AssertReflectionConstructorIsAnnotated(typeof(HttpClient), typeof(string));
        AssertReflectionConstructorIsAnnotated(typeof(HttpClient), typeof(string), typeof(ResiliencePipeline));
        AssertReflectionConstructorIsAnnotated(
            typeof(HttpClient),
            typeof(string),
            typeof(ILogger<HttpSelector<string>>));
        AssertReflectionConstructorIsAnnotated(
            typeof(HttpClient),
            typeof(string),
            typeof(ResiliencePipeline),
            typeof(ILogger<HttpSelector<string>>));
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
            await foreach (var item in selector.ReadEnvelopesAsync(cts.Token))
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

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
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

        var items = new List<ProcessingEnvelope<TestComplexType>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal(1, items[0].Payload?.Id);
        Assert.Equal("Test", items[0].Payload?.Name);
    }

    [Fact]
    public async Task ReadAsync_UsesJsonTypeInfo()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[{\"Id\":2,\"Name\":\"Generated\"}]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<TestComplexType>(
            client,
            "http://test.com",
            HttpSelectorTestJsonContext.Default.ListTestComplexType);

        var items = new List<ProcessingEnvelope<TestComplexType>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Equal(2, items[0].Payload.Id);
        Assert.Equal("Generated", items[0].Payload.Name);
    }

    [Fact]
    public async Task ReadAsync_StreamsJsonArray_WithItemJsonTypeInfo()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("[\"alpha\",\"beta\"]")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(
            client,
            "http://test.com",
            HttpSelectorTestJsonContext.Default.String,
            HttpSelectorStreamingMode.JsonArray);

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            items.Add(item);

        Assert.Equal(["alpha", "beta"], items.Select(x => x.Payload).ToArray());
    }

    [Fact]
    public async Task ReadAsync_StreamsNdjson_WithItemJsonTypeInfo()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("\"alpha\"\n\n\"beta\"\n")
            });

        var client = new HttpClient(mockHandler.Object);
        var selector = new HttpSelector<string>(
            client,
            "http://test.com",
            HttpSelectorTestJsonContext.Default.String,
            HttpSelectorStreamingMode.Ndjson);

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            items.Add(item);

        Assert.Equal(["alpha", "beta"], items.Select(x => x.Payload).ToArray());
    }

    [Fact]
    public async Task ReadAsync_UsesHttpClientFactory()
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
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("orders")).Returns(client);
        var selector = new HttpClientFactorySelector<string>(
            factory.Object,
            "http://test.com",
            clientName: "orders");

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("test", items[0].Payload);
        factory.Verify(x => x.CreateClient("orders"), Times.Once);
    }

    [Fact]
    public async Task ReadAsync_UsesHttpClientFactory_ForStreamingNdjson()
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent("\"from-factory\"\n")
            });
        var client = new HttpClient(mockHandler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("orders")).Returns(client);
        var selector = new HttpClientFactorySelector<string>(
            factory.Object,
            "http://test.com",
            HttpSelectorTestJsonContext.Default.String,
            HttpSelectorStreamingMode.Ndjson,
            clientName: "orders",
            pipeline: null,
            logger: null);

        var items = new List<ProcessingEnvelope<string>>();
        await foreach (var item in selector.ReadEnvelopesAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("from-factory", items[0].Payload);
        factory.Verify(x => x.CreateClient("orders"), Times.Once);
    }

    private static HttpClient CreateJsonClient(string content)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(content)
            });
        return new HttpClient(mockHandler.Object);
    }

    private static void AssertReflectionConstructorIsAnnotated(params Type[] parameterTypes)
    {
        var constructor = typeof(HttpSelector<string>).GetConstructor(parameterTypes);
        Assert.NotNull(constructor);
        Assert.NotNull(constructor.GetCustomAttribute<RequiresUnreferencedCodeAttribute>());
        Assert.NotNull(constructor.GetCustomAttribute<RequiresDynamicCodeAttribute>());
    }

    private class TestComplexType
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ListLogger<TCategory> : ILogger<TCategory>
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(List<TestComplexType>))]
    private sealed partial class HttpSelectorTestJsonContext : JsonSerializerContext;
}
