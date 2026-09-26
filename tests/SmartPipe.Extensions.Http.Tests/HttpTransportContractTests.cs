using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http.Tests;

public sealed partial class HttpPipelineComponentsTests
{
    private static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task BodyTimeout_UsesTheRunTimeProviderAndSurfacesTimeoutException()
    {
        var time = new FakeTimeProvider();
        var readerEntered = NewSignal();
        var readerCancelled = NewSignal();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("body-timeout"),
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow")),
                (_, token) => WaitForCancellationAsync(readerEntered, readerCancelled, token),
                new HttpSourceOptions { BodyTimeout = BodyTimeout })
            .Build();
        var run = await definition.StartAsync(Context(definition.Key, time));
        var drain = DrainAsync(run);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(BodyTimeout - TimeSpan.FromTicks(1));
        Assert.False(readerCancelled.Task.IsCompleted);
        time.Advance(TimeSpan.FromTicks(1));

        await readerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        Assert.IsType<TimeoutException>(error);
    }

    [Fact]
    public async Task BodyTimeout_IsTranslatedWhenTheReaderObservesItThroughItsOwnLinkedToken()
    {
        var time = new FakeTimeProvider();
        var readerEntered = NewSignal();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("linked-reader-timeout"),
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow")),
                (_, token) => WaitOnOwnLinkedTokenAsync(readerEntered, token),
                new HttpSourceOptions { BodyTimeout = BodyTimeout })
            .Build();
        var run = await definition.StartAsync(Context(definition.Key, time));
        var drain = DrainAsync(run);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(BodyTimeout);
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        var timeout = Assert.IsType<TimeoutException>(error);
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException);
    }

    [Fact]
    public async Task SinkErrorPreviewRead_IsBoundedByBodyTimeout()
    {
        var time = new FakeTimeProvider();
        var body = new BlockingReadStream();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(body),
        }));
        using var client = new HttpClient(handler);
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("sink-preview-timeout"),
                RuntimeSource([1]))
            .ToHttp(
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/write")),
                options: new HttpSinkOptions
                {
                    BodyTimeout = BodyTimeout,
                    ResponsePolicy = new HttpResponsePolicy { IncludeErrorBodyPreview = true },
                });
        var run = await definition.StartAsync(Context(definition.Key, time));
        var drain = DrainAsync(run);

        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(BodyTimeout);
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        Assert.IsType<TimeoutException>(error);
    }

    [Fact]
    public async Task CallerCancellationDuringSend_StaysCancellationAndReleasesOwnedResources()
    {
        var sendEntered = NewSignal();
        using var callerCancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, token) =>
        {
            sendEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Send must not complete.");
        });
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var requestContent = new TrackingContent("request");
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp(
                new PipelineKey("send-cancellation"),
                factory,
                "owned-client",
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/slow")
                {
                    Content = requestContent,
                }),
                ReadTextAsync)
            .Build();
        var run = await definition.StartAsync(callerCancellation.Token);
        var drain = DrainAsync(run);

        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(1, requestContent.DisposeCount);
        Assert.Equal(1, handler.DisposeCount);
    }

    [Fact]
    public async Task CancellationWithCleanupFailure_KeepsCancellationTypeTokenAndOrderedCauses()
    {
        var readerEntered = NewSignal();
        var readerCancelled = NewSignal();
        var clientDisposeFailure = new InvalidOperationException("client dispose");
        using var callerCancellation = new CancellationTokenSource();
        CancellationToken sourceOperationToken = default;
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            clientDisposeFailure);
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("cancel-with-cleanup-failure"),
                factory,
                "owned-client",
                (_, token) =>
                {
                    sourceOperationToken = token;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow"));
                },
                (_, token) => WaitForCancellationAsync(readerEntered, readerCancelled, token))
            .Build();
        var run = await definition.StartAsync(callerCancellation.Token);
        var drain = DrainAsync(run);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callerCancellation.Cancel();
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        var canceled = Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(sourceOperationToken, canceled.CancellationToken);
        var causes = Assert.IsType<AggregateException>(canceled.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(causes.InnerExceptions[0]);
        Assert.Same(clientDisposeFailure, causes.InnerExceptions[1]);
    }

    [Fact]
    public async Task BodyTimeoutWithCleanupFailure_KeepsTimeoutTypeAndOrderedCauses()
    {
        var time = new FakeTimeProvider();
        var readerEntered = NewSignal();
        var readerCancelled = NewSignal();
        var clientDisposeFailure = new InvalidOperationException("client dispose");
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
            clientDisposeFailure);
        var factory = new RecordingHttpClientFactory(() => new HttpClient(handler));
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("timeout-with-cleanup-failure"),
                factory,
                "owned-client",
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/slow")),
                (_, token) => WaitForCancellationAsync(readerEntered, readerCancelled, token),
                new HttpSourceOptions { BodyTimeout = BodyTimeout })
            .Build();
        var run = await definition.StartAsync(Context(definition.Key, time));
        var drain = DrainAsync(run);

        await readerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(BodyTimeout);
        var error = await CaptureRunFailureAsync(run, drain);
        await run.DisposeAsync();

        var timeout = Assert.IsType<TimeoutException>(error);
        var causes = Assert.IsType<AggregateException>(timeout.InnerException);
        Assert.IsType<TimeoutException>(causes.InnerExceptions[0]);
        Assert.Same(clientDisposeFailure, causes.InnerExceptions[1]);
    }

    [Fact]
    public async Task LateBodyTimeoutDuringReaderDisposal_DoesNotReplaceACompletedRead()
    {
        var time = new FakeTimeProvider();
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler);
        var reader = new LateCancellationReader(() => time.Advance(BodyTimeout));
        var sink = new CollectingSink<int>();
        var definition = HttpPipelineDefinitionBuilderExtensions.FromHttp<int>(
                new PipelineKey("late-cancellation"),
                client,
                (_, _) => ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Get, "https://example.test/values")),
                reader.ReadAsync,
                new HttpSourceOptions { BodyTimeout = BodyTimeout })
            .To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) => ValueTask.FromResult<IPipelineSink<int>>(sink)));

        var run = await definition.StartAsync(Context(definition.Key, time));
        await CompleteAndDisposeAsync(run);

        Assert.True(reader.DisposeThrewLateCancellation);
        Assert.Equal([7, 8], sink.Values);
    }

    [Fact]
    public async Task HandlerRetry_StopsAfterAnEarlySuccess()
    {
        var terminal = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var retry = new RetryOnceHandler { InnerHandler = terminal };
        // The handler chain is shared across writes, so each factory client leaves it alive.
        var factory = new RecordingHttpClientFactory(() => new HttpClient(retry, disposeHandler: false));
        var requestFactoryCalls = 0;
        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey("early-success"),
                RuntimeSource(["a", "b"]))
            .ToHttp(
                factory,
                "retry-client",
                (_, _) =>
                {
                    requestFactoryCalls++;
                    return ValueTask.FromResult(new HttpRequestMessage(HttpMethod.Post, "https://example.test/retry"));
                });

        await RunAndDisposeAsync(definition);

        Assert.Equal(2, requestFactoryCalls);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, terminal.SendCount);
    }

    [Fact]
    public void Composition_RejectsInvalidOptionsBeforeAnyIo()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("No send expected."));
        using var client = new HttpClient(handler);
        HttpRequestFactory<int> sinkRequests = (_, _) => throw new InvalidOperationException("No request expected.");
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> sourceRequests =
            (_, _) => throw new InvalidOperationException("No request expected.");

        void Source(HttpSourceOptions options) =>
            HttpPipelineComponents.Source<string>(client, sourceRequests, ReadTextAsync, options);
        void Sink(HttpSinkOptions options) =>
            HttpPipelineComponents.Sink(client, sinkRequests, options: options);

        Assert.ThrowsAny<ArgumentException>(() => Source(new HttpSourceOptions { OperationName = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(new HttpSourceOptions { BodyTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(new HttpSourceOptions { BodyTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(new HttpSourceOptions { BodyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue) }));
        Assert.Throws<ArgumentNullException>(() => Source(new HttpSourceOptions { ResponsePolicy = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source(new HttpSourceOptions { ResponsePolicy = new HttpResponsePolicy { MaxErrorBodyBytes = 0 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Sink(new HttpSinkOptions { ResponsePolicy = new HttpResponsePolicy { MaxErrorBodyBytes = -1 } }));
        Assert.ThrowsAny<ArgumentException>(() => Sink(new HttpSinkOptions { IdempotencyHeaderName = "" }));
        Assert.Throws<ArgumentException>(() => Sink(new HttpSinkOptions { IdempotencyHeaderName = "Bad Header" }));
        Assert.Throws<ArgumentException>(() => Sink(new HttpSinkOptions { IdempotencyHeaderName = "Content-Type" }));

        Source(new HttpSourceOptions { BodyTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1d) });
        Sink(new HttpSinkOptions { IdempotencyHeaderName = "X-Request-Key" });
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public void Composition_RejectsNullArguments()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) => throw new InvalidOperationException()));
        var factory = new RecordingHttpClientFactory(() => client);
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> sourceRequests =
            (_, _) => throw new InvalidOperationException();
        HttpRequestFactory<string> sinkRequests = (_, _) => throw new InvalidOperationException();

        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Source<string>((HttpClient)null!, sourceRequests, ReadTextAsync));
        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Source<string>(client, null!, ReadTextAsync));
        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Source<string>(client, sourceRequests, null!));
        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Source<string>((IHttpClientFactory)null!, "name", sourceRequests, ReadTextAsync));
        Assert.ThrowsAny<ArgumentException>(() => HttpPipelineComponents.Source<string>(factory, " ", sourceRequests, ReadTextAsync));
        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Sink((HttpClient)null!, sinkRequests));
        Assert.Throws<ArgumentNullException>(() => HttpPipelineComponents.Sink<string>(client, null!));
        Assert.ThrowsAny<ArgumentException>(() => HttpPipelineComponents.Sink(factory, "", sinkRequests));
        Assert.Equal(0, factory.CreateCount);
    }

    private static PipelineActivationContext Context(PipelineKey key, TimeProvider time) =>
        new(key, Guid.NewGuid(), timeProvider: time);

    private static async IAsyncEnumerable<int> WaitOnOwnLinkedTokenAsync(
        TaskCompletionSource entered,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cancelled = NewSignal();
        using var registration = own.Token.Register(() => cancelled.TrySetResult());
        entered.TrySetResult();
        await cancelled.Task.ConfigureAwait(false);
        own.Token.ThrowIfCancellationRequested();
        yield return 1;
    }

    /// <summary>Completes its items, then observes a body timeout that fires while it is being disposed.</summary>
    private sealed class LateCancellationReader(Action fireBodyTimeout)
    {
        public bool DisposeThrewLateCancellation { get; private set; }

        public IAsyncEnumerable<int> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
            new Values(this, cancellationToken);

        private void FireBodyTimeout(CancellationToken token)
        {
            fireBodyTimeout();
            DisposeThrewLateCancellation = token.IsCancellationRequested;
            token.ThrowIfCancellationRequested();
        }

        private sealed class Values(LateCancellationReader owner, CancellationToken token) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
        {
            private static readonly int[] Items = [7, 8];
            private int _index = -1;

            public int Current => Items[_index];

            public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(++_index < Items.Length);

            public ValueTask DisposeAsync()
            {
                owner.FireBodyTimeout(token);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CollectingSink<T> : IPipelineSink<T>
    {
        private readonly List<T> _values = [];

        public IReadOnlyList<T> Values => _values;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
        {
            lock (_values)
                _values.Add(envelope.Payload!);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = NewSignal();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            var cancelled = NewSignal();
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            await cancelled.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
