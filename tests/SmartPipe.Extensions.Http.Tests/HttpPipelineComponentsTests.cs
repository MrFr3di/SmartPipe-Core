using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Http;
using SmartPipe.Core;
using SmartPipe.Extensions.Http;

namespace SmartPipe.Extensions.Http.Tests;

public sealed partial class HttpPipelineComponentsTests
{
    [Fact]
    public void Composition_IsLazyAndDoesNotPerformHttpIo()
    {
        var sourceCalls = 0;
        var sinkCalls = 0;
        var factory = new RecordingHttpClientFactory(() =>
        {
            throw new InvalidOperationException("The client factory must remain lazy during composition.");
        });
        using var directClient = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var source = HttpPipelineComponents.Source<int>(
            directClient,
            (_, _) =>
            {
                sourceCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/source"));
            },
            (_, _) => EmptyValues<int>());
        var sourceDefinition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
            new PipelineKey("http-source"),
            factory,
            "source",
            (_, _) =>
            {
                sourceCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/source"));
            },
            (_, _) => EmptyValues<int>()).Build();
        var sinkDefinition = PipelineDefinitionBuilder.From(
                new PipelineKey("http-sink"),
                PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
                    ValueTask.FromResult<IPipelineSource<int>>(new SequenceSource<int>([1]))))
            .ToHttp(
                factory,
                "sink",
                (envelope, _) =>
                {
                    sinkCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/sink"));
                });

        Assert.Equal(PipelineComponentOwnership.RuntimeOwned, source.Ownership);
        Assert.Equal(new PipelineKey("http-source"), sourceDefinition.Key);
        Assert.Equal(new PipelineKey("http-sink"), sinkDefinition.Key);
        Assert.Equal(0, sourceCalls);
        Assert.Equal(0, sinkCalls);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task FactorySource_CreatesClientOnlyForEnumerationAndKeepsResponseAliveUntilReaderCompletes()
    {
        var response = new TrackingResponse(HttpStatusCode.OK, new TrackingContent("body"));
        var handler = new RecordingHandler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var readerEntered = NewSignal();
        var releaseReader = NewSignal();
        var requestFactoryCalls = 0;
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
            new PipelineKey("factory-source"),
            factory,
            "read-client",
            (_, _) =>
            {
                requestFactoryCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/items"));
            },
            (_, _) => WaitForReaderAsync(readerEntered, releaseReader));
        var built = definition.Build();

        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, requestFactoryCalls);

        var run = await built.StartAsync();
        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, requestFactoryCalls);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(0, response.DisposeCount);

        var first = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Envelope!.Payload);
        Assert.Equal(0, handler.DisposeCount);

        releaseReader.TrySetResult();
        await CompleteAndDisposeAsync(run);

        Assert.Equal(1, handler.DisposeCount);
        Assert.Equal(1, response.DisposeCount);
    }

    [Fact]
    public async Task CancellingAfterFirstSourceValueDisposesReaderResponseAndFactoryClient()
    {
        var response = new TrackingResponse(HttpStatusCode.OK, new StringContent("body"));
        var handler = new RecordingHandler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var readerEntered = NewSignal();
        var readerDisposed = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
            new PipelineKey("early-reader-exit"),
            factory,
            "reader-client",
            (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/stream")),
            (_, token) => YieldThenBlockAsync(readerEntered, readerDisposed, token));
        var run = await definition.Build().StartAsync(cancellation.Token);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = await run.Outputs.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.Envelope!.Payload);
        Assert.Equal(0, handler.DisposeCount);

        cancellation.Cancel();
        var error = await CaptureRunFailureAsync(run, DrainAsync(run));
        await readerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.DisposeAsync();

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(1, response.DisposeCount);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task DirectSource_BorrowsClientAndCreatesOneRequestPerSourceEnumeration()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new RecordingHandler((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("one"),
            });
        });
        var client = new HttpClient(handler);
        var requestFactoryCalls = 0;
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<string>(
            new PipelineKey("direct-source"),
            client,
            (_, _) =>
            {
                requestFactoryCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/items"));
            },
            ReadTextAsync);

        Assert.Equal(0, handler.SendCount);
        await RunAndDisposeAsync(definition.Build());

        Assert.Equal(1, requestFactoryCalls);
        Assert.Equal(1, handler.SendCount);
        Assert.Single(requests);
        Assert.Equal(0, handler.DisposeCount);
        client.Dispose();
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task Sink_CreatesANewRequestForEachWriteAndLeavesDirectClientOwnedByCaller()
    {
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var handler = new RecordingHandler(async (request, token) =>
        {
            requests.Add(request);
            bodies.Add(await request.Content!.ReadAsStringAsync(token));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var client = new HttpClient(handler);
        var factoryCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("sink-writes"),
                RuntimeSource(["first", "second"]))
            .ToHttp(
                client,
                (envelope, _) =>
                {
                    factoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(
                        HttpMethod.Post,
                        "https://example.test/items")
                    {
                        Content = new StringContent(envelope.Payload),
                    });
                });

        await RunAndDisposeAsync(definition);

        Assert.Equal(2, factoryCalls);
        Assert.Equal(2, handler.SendCount);
        Assert.Equal(new[] { "first", "second" }, bodies);
        Assert.Equal(2, requests.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(0, handler.DisposeCount);
        client.Dispose();
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task FactorySink_CreatesAndDisposesOneClientForEachWrite()
    {
        var handlers = new List<RecordingHandler>();
        var factory = new RecordingHttpClientFactory(() =>
        {
            var handler = new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            handlers.Add(handler);
            return new HttpClient(handler);
        });
        var requestFactoryCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("factory-sink"),
                RuntimeSource([1, 2]))
            .ToHttp(
                factory,
                "write-client",
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/items"));
                });

        Assert.Equal(0, factory.CreateCount);
        await RunAndDisposeAsync(definition);

        Assert.Equal(2, requestFactoryCalls);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, factory.CreatedClients.Count);
        Assert.All(handlers, handler => Assert.Equal(1, handler.DisposeCount));
    }

    [Fact]
    public async Task RelativeUriWithoutBaseAddress_FailsBeforeHandlerSendAndDisposesRequest()
    {
        var requestContent = new TrackingContent("payload");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("invalid-uri"),
                RuntimeSource([1]))
            .ToHttp(client, (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "/relative")
            {
                Content = requestContent,
            }));

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, requestContent.DisposeCount);
    }

    [Fact]
    public async Task RelativeUriWithBaseAddress_IsResolvedBeforeSend()
    {
        Uri? observedUri = null;
        var handler = new RecordingHandler((request, _) =>
        {
            observedUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/api/") };
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("base-address"),
                RuntimeSource([1]))
            .ToHttp(client, (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "items")));

        await RunAndDisposeAsync(definition);

        Assert.Equal(new Uri("https://example.test/api/items"), observedUri);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task InvalidHeaderValueFailsBeforeHandlerSendAndDisposesRequest()
    {
        var requestContent = new TrackingContent("payload");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("invalid-header"),
                RuntimeSource([1]))
            .ToHttp(client, (_, _) =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")
                {
                    Content = requestContent,
                };
                Assert.True(request.Headers.TryAddWithoutValidation("X-Unsafe", "value\r\nInjected: yes"));
                return ValueTask.FromResult(request);
            });

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, requestContent.DisposeCount);
    }

    [Fact]
    public async Task ErrorPreview_IsOptInBoundedAndReportsTruncation()
    {
        var bodyStream = new CountingReadStream(Encoding.UTF8.GetBytes("abcdefgh"));
        var content = new StreamContent(bodyStream);
        var response = new TrackingResponse(HttpStatusCode.BadGateway, content);
        var handler = new RecordingHandler((_, _) => Task.FromResult<HttpResponseMessage>(response));
        using var client = new HttpClient(handler);
        var readerCalls = 0;
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<string>(
                new PipelineKey("preview"),
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/fail")),
                (_, _) =>
                {
                    readerCalls++;
                    return EmptyValues<string>();
                },
                new HttpSourceOptions
                {
                    ResponsePolicy = new HttpResponsePolicy
                    {
                        IncludeErrorBodyPreview = true,
                        MaxErrorBodyBytes = 4,
                    },
                })
            .Build();

        var error = await RunSourceExpectingFailureAndDisposeAsync(definition);
        var status = Assert.IsType<HttpResponseStatusException>(error);

        Assert.Equal(HttpStatusCode.BadGateway, status.StatusCode);
        Assert.Equal("abcd", status.ErrorBodyPreview);
        Assert.True(status.ErrorBodyPreviewTruncated);
        Assert.InRange(bodyStream.BytesRead, 4, 5);
        Assert.Equal(1, response.DisposeCount);
        Assert.Equal(0, readerCalls);
    }

    [Fact]
    public async Task DefaultErrorPolicyDoesNotReadErrorBody()
    {
        var bodyStream = new CountingReadStream(Encoding.UTF8.GetBytes("sensitive body"));
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StreamContent(bodyStream) }));
        using var client = new HttpClient(handler);
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<string>(
                new PipelineKey("no-preview"),
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/fail")),
                ReadTextAsync)
            .Build();

        var error = await RunSourceExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<HttpResponseStatusException>(error);
        Assert.Equal(0, bodyStream.BytesRead);
    }

    [Fact]
    public async Task CallerCancellationDuringBodyReadRemainsOperationCanceledException()
    {
        var readerEntered = NewSignal();
        var readerCancelled = NewSignal();
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken sourceOperationToken = default;
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("caller-cancellation"),
                client,
                (_, token) =>
                {
                    sourceOperationToken = token;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow"));
                },
                (_, token) => WaitForCancellationAsync(readerEntered, readerCancelled, token),
                new HttpSourceOptions { BodyTimeout = TimeSpan.FromSeconds(10) })
            .Build();
        var run = await definition.StartAsync(callerCancellation.Token);
        var drain = DrainAsync(run);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        await readerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await CaptureRunFailureAsync(run, drain);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(sourceOperationToken, ((OperationCanceledException)error).CancellationToken);
    }

    [Fact]
    public async Task HandlerRetry_ReusesOneRequestAndOneIdempotencyKeyAcrossBottomSends()
    {
        var sentKeys = new List<string?>();
        var terminal = new RecordingHandler((request, _) =>
        {
            sentKeys.Add(request.Headers.GetValues("X-Request-Key").SingleOrDefault());
            return Task.FromResult(new HttpResponseMessage(sentKeys.Count == 1
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK));
        });
        var retry = new RetryOnceHandler { InnerHandler = terminal };
        var factory = new RecordingHttpClientFactory(() => new HttpClient(retry));
        var requestFactoryCalls = 0;
        var selectorCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("handler-retry"),
                RuntimeSource(["payload"]))
            .ToHttp(
                factory,
                "retry-client",
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/retry"));
                },
                _ =>
                {
                    selectorCalls++;
                    return "stable-key";
                },
                new HttpSinkOptions { IdempotencyHeaderName = "X-Request-Key" });

        await RunAndDisposeAsync(definition);

        Assert.Equal(1, requestFactoryCalls);
        Assert.Equal(1, selectorCalls);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(2, terminal.SendCount);
        Assert.Equal(1, terminal.DisposeCount);
        Assert.Equal(new[] { "stable-key", "stable-key" }, sentKeys);
    }

    [Fact]
    public async Task ConflictingExistingIdempotencyHeaderFailsBeforeSend()
    {
        var requestContent = new TrackingContent("body");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("idempotency-conflict"),
                RuntimeSource([1]))
            .ToHttp(
                client,
                (_, _) =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")
                    {
                        Content = requestContent,
                    };
                    request.Headers.Add("Idempotency-Key", "existing");
                    return ValueTask.FromResult(request);
                },
                _ => "selected");

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(1, requestContent.DisposeCount);
    }

    public static TheoryData<string> InvalidIdempotencyKeys => new()
    {
        "",
        "contains space",
        "non-ascii-é",
        new string('x', 256),
    };

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public async Task InvalidIdempotencyKeyFailsBeforeSend(string key)
    {
        var requestContent = new TrackingContent("body");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var requestFactoryCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("invalid-key"),
                RuntimeSource([1]))
            .ToHttp(
                client,
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")
                    {
                        Content = requestContent,
                    });
                },
                _ => key);

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(0, handler.SendCount);
        Assert.Equal(requestFactoryCalls, requestContent.DisposeCount);
    }

    [Fact]
    public async Task MatchingExistingIdempotencyHeaderIsPreservedExactlyOnce()
    {
        var valuesSeen = new List<string>();
        var handler = new RecordingHandler((request, _) =>
        {
            valuesSeen.AddRange(request.Headers.GetValues("Idempotency-Key"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("matching-key"),
                RuntimeSource([1]))
            .ToHttp(
                client,
                (_, _) =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/write");
                    request.Headers.Add("Idempotency-Key", "same-key");
                    return ValueTask.FromResult(request);
                },
                _ => "same-key");

        await RunAndDisposeAsync(definition);

        Assert.Equal(new[] { "same-key" }, valuesSeen);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task NullSinkPayloadDoesNotCreateRequestOrSend()
    {
        var requestFactoryCalls = 0;
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("null-payload"),
                RuntimeSource<string?>([null]))
            .ToHttp(
                client,
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/write"));
                },
                _ => "must-not-be-selected");

        await RunAndDisposeAsync(definition);

        Assert.Equal(0, requestFactoryCalls);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void Builders_PreserveTypesPipelineKeyAndDoNotInvokeFactories()
    {
        var requestFactoryCalls = 0;
        var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var source = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
            new PipelineKey("typed-builders"),
            client,
            (_, _) =>
            {
                requestFactoryCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/items"));
            },
            (_, _) => EmptyValues<int>());
        PipelineDefinition<int, string> definition = source
            .Transform(new PipelineStageKey("format"), RuntimeTransformer<int, string>())
            .ToHttp(
                client,
                (envelope, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/out")
                    {
                        Content = new StringContent(envelope.Payload),
                    });
                });
        PipelineDefinition<int, int> initialDefinition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("initial-builder"),
                client,
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/source"));
                },
                (_, _) => EmptyValues<int>())
            .ToHttp(client, (_, _) =>
            {
                requestFactoryCalls++;
                return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/out"));
            });

        Assert.Equal(new PipelineKey("typed-builders"), definition.Key);
        Assert.Equal("format", Assert.Single(definition.Stages).Key.Value);
        Assert.Equal(typeof(int), definition.Stages[0].InputType);
        Assert.Equal(typeof(string), definition.Stages[0].OutputType);
        Assert.Equal(new PipelineKey("initial-builder"), initialDefinition.Key);
        Assert.Empty(initialDefinition.Stages);
        Assert.Equal(0, requestFactoryCalls);
        client.Dispose();
    }

    [Fact]
    public async Task PrimaryStatusAndCleanupFailuresAreReportedInOwnershipOrder()
    {
        var responseDisposeFailure = new InvalidOperationException("response dispose");
        var requestDisposeFailure = new InvalidOperationException("request dispose");
        var clientDisposeFailure = new InvalidOperationException("client dispose");
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new TrackingContent("body", responseDisposeFailure),
        }), clientDisposeFailure);
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var requestContent = new TrackingContent("request", requestDisposeFailure);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("cleanup-order"),
                RuntimeSource([1]))
            .ToHttp(
                factory,
                "owned-client",
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")
                {
                    Content = requestContent,
                }));

        var error = await RunExpectingFailureAndDisposeAsync(definition);
        var aggregate = Assert.IsType<AggregateException>(error);

        Assert.IsType<HttpResponseStatusException>(aggregate.InnerExceptions[0]);
        Assert.Same(responseDisposeFailure, aggregate.InnerExceptions[1]);
        Assert.Same(requestDisposeFailure, aggregate.InnerExceptions[2]);
        Assert.Same(clientDisposeFailure, aggregate.InnerExceptions[3]);
        Assert.Equal(1, requestContent.DisposeCount);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task SingleCleanupFailureWithoutPrimaryIsRethrownUnchanged()
    {
        var requestDisposeFailure = new InvalidOperationException("request dispose");
        var requestContent = new TrackingContent("request", requestDisposeFailure);
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("single-cleanup-failure"),
                RuntimeSource([1]))
            .ToHttp(client, (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")
            {
                Content = requestContent,
            }));

        var error = await RunExpectingFailureAndDisposeAsync(definition);

        Assert.Same(requestDisposeFailure, error);
    }

    private static PipelineComponent<IPipelineSource<T>> RuntimeSource<T>(IReadOnlyList<T> values) =>
        PipelineComponent.RuntimeOwned<IPipelineSource<T>>((_, _) =>
            ValueTask.FromResult<IPipelineSource<T>>(new SequenceSource<T>(values)));

    private static PipelineComponent<IPipelineTransformer<TInput, TOutput>> RuntimeTransformer<TInput, TOutput>() =>
        PipelineComponent.RuntimeOwned<IPipelineTransformer<TInput, TOutput>>((_, _) =>
            ValueTask.FromResult<IPipelineTransformer<TInput, TOutput>>(new IdentityTransformer<TInput, TOutput>()));

    private static async Task RunAndDisposeAsync<T>(PipelineDefinition<T, T> definition)
    {
        var run = await definition.StartAsync();
        await CompleteAndDisposeAsync(run);
    }

    private static async Task RunAndDisposeAsync<T>(PipelineDefinition<T, T> definition, CancellationToken token)
    {
        var run = await definition.StartAsync(token);
        await CompleteAndDisposeAsync(run);
    }

    private static async Task CompleteAndDisposeAsync<T>(PipelineRun<T> run)
    {
        await using (run.ConfigureAwait(false))
        {
            var drain = DrainAsync(run);
            await Task.WhenAll(run.Completion, drain).ConfigureAwait(false);
        }
    }

    private static async Task<Exception> RunExpectingFailureAndDisposeAsync<T>(PipelineDefinition<T, T> definition)
    {
        var run = await definition.StartAsync();
        await using (run.ConfigureAwait(false))
        {
            var drain = DrainAsync(run);
            var error = await CaptureRunFailureAsync(run, drain).ConfigureAwait(false);
            return error;
        }
    }

    private static async Task<Exception> RunSourceExpectingFailureAndDisposeAsync<T>(PipelineDefinition<T, T> definition)
    {
        var run = await definition.StartAsync();
        await using (run.ConfigureAwait(false))
        {
            var drain = DrainAsync(run);
            return await CaptureRunFailureAsync(run, drain).ConfigureAwait(false);
        }
    }

    private static async Task<Exception> CaptureRunFailureAsync<T>(PipelineRun<T> run, Task drain)
    {
        Exception? error = null;
        try
        {
            await run.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        try
        {
            await drain.ConfigureAwait(false);
        }
        catch (Exception exception) when (error is null)
        {
            error = exception;
        }
        catch
        {
            // Preserve the run's primary failure when the output channel mirrors it.
        }

        return error ?? throw new Xunit.Sdk.XunitException("Expected the pipeline run to fail.");
    }

    private static async Task DrainAsync<T>(PipelineRun<T> run)
    {
        await foreach (var _ in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async IAsyncEnumerable<T> EmptyValues<T>([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private static async IAsyncEnumerable<int> WaitForReaderAsync(
        TaskCompletionSource entered,
        TaskCompletionSource release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        entered.TrySetResult();
        yield return 1;
        await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return 2;
    }

    private static async IAsyncEnumerable<int> YieldThenBlockAsync(
        TaskCompletionSource entered,
        TaskCompletionSource disposed,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unblock = NewSignal();
        using var registration = cancellationToken.Register(() => unblock.TrySetResult());
        try
        {
            entered.TrySetResult();
            yield return 1;
            await unblock.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            disposed.TrySetResult();
        }
    }

    private static async IAsyncEnumerable<int> WaitForCancellationAsync(
        TaskCompletionSource entered,
        TaskCompletionSource cancellationObserved,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        entered.TrySetResult();
        using var registration = cancellationToken.Register(() => cancellationObserved.TrySetResult());
        await cancellationObserved.Task.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        yield return 1;
    }

    private static async IAsyncEnumerable<string> ReadTextAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SequenceSource<T>(IReadOnlyList<T> values) : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var value in values)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(value);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityTransformer<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<TOutput>> TransformAsync(
            ProcessingEnvelope<TInput> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<TOutput>.Success((TOutput)(object?)envelope.Payload!));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        Exception? disposeFailure = null) : HttpMessageHandler
    {
        private int _sendCount;
        private int _disposeCount;

        public int SendCount => Volatile.Read(ref _sendCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            return send(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
                if (disposeFailure is not null)
                    throw disposeFailure;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RecordingHttpClientFactory(Func<HttpClient> create) : IHttpClientFactory
    {
        private readonly List<HttpClient> _createdClients = [];

        public int CreateCount => _createdClients.Count;
        public IReadOnlyList<HttpClient> CreatedClients => _createdClients;

        public HttpClient CreateClient(string name)
        {
            var client = create();
            _createdClients.Add(client);
            return client;
        }
    }

    private sealed class RetryOnceHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var first = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (first.IsSuccessStatusCode)
                return first;

            first.Dispose();
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TrackingResponse : HttpResponseMessage
    {
        private int _disposeCount;

        public TrackingResponse(HttpStatusCode statusCode, HttpContent content) : base(statusCode)
        {
            Content = content;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Interlocked.Increment(ref _disposeCount);
            base.Dispose(disposing);
        }

    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly Exception? _disposeFailure;
        private int _disposeCount;

        public TrackingContent(string value, Exception? disposeFailure = null)
        {
            _bytes = Encoding.UTF8.GetBytes(value);
            _disposeFailure = disposeFailure;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain")
            {
                CharSet = "utf-8",
            };
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = _bytes.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref _disposeCount);
                if (_disposeFailure is not null)
                    throw _disposeFailure;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class CountingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private int _bytesRead;

        public int BytesRead => Volatile.Read(ref _bytesRead);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (read.IsCompletedSuccessfully)
                Interlocked.Add(ref _bytesRead, read.Result);
            return read;
        }
    }
}
