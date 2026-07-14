using System.Text;
using System.Text.Json;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Json.Tests.Sinks;

public sealed class JsonFileSinkLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_ReentrantFlushReturnsSharedTask()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new ReentrantDisposeStream();
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10);
        stream.OnFlush = () => stream.ReentrantTask = sink.DisposeAsync().AsTask();
        await sink.WriteAsync(Envelope("buffered"), ct);

        var disposeTask = sink.DisposeAsync().AsTask();
        await disposeTask;

        stream.ReentrantTask.Should().BeSameAs(disposeTask);
    }

    [Fact]
    public async Task DisposeAsync_ReentrantOwnedStreamDisposeDoesNotStartSecondCleanup()
    {
        var stream = new ReentrantDisposeStream();
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10, leaveOpen: false);
        stream.OnDispose = () => stream.ReentrantTask = sink.DisposeAsync().AsTask();

        var disposeTask = sink.DisposeAsync().AsTask();
        await disposeTask;

        stream.ReentrantTask.Should().BeSameAs(disposeTask);
        stream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallersReceiveSameExceptionInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new IOException("flush failed");
        var stream = new ReentrantDisposeStream { FlushFailure = expected, BlockFlush = true };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10);

        var firstTask = sink.DisposeAsync().AsTask();
        await stream.FlushEntered.Task.WaitAsync(ct);
        var secondTask = sink.DisposeAsync().AsTask();
        stream.ReleaseFlush();
        var first = await Assert.ThrowsAsync<IOException>(() => firstTask);
        var second = await Assert.ThrowsAsync<IOException>(() => secondTask);

        secondTask.Should().BeSameAs(firstTask);
        second.Should().BeSameAs(first);
        first.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task DisposeAsync_FaultedCleanupIsNotRetriedAndRejectsFurtherUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new ReentrantDisposeStream { FlushFailure = new IOException("flush failed") };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10);

        var firstTask = sink.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => firstTask);
        var secondTask = sink.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => secondTask);

        secondTask.Should().BeSameAs(firstTask);
        stream.FlushCount.Should().Be(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.InitializeAsync(ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.WriteAsync(Envelope("late"), ct));
    }

    [Fact]
    public async Task DisposeAsync_BeginningDisposalRejectsInitializeAndWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new ReentrantDisposeStream { BlockFlush = true };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10);

        var disposeTask = sink.DisposeAsync().AsTask();
        await stream.FlushEntered.Task.WaitAsync(ct);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.InitializeAsync(ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.WriteAsync(Envelope("late"), ct));
        stream.ReleaseFlush();
        await disposeTask;
    }

    [Fact]
    public async Task DisposeAsync_FailedBufferedFlushRollsBackWrittenBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new ReentrantDisposeStream { FailWriteAfterBytes = 3 };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10, leaveOpen: false);
        await sink.WriteAsync(Envelope("buffered"), ct);

        await Assert.ThrowsAsync<IOException>(() => sink.DisposeAsync().AsTask());

        stream.ToArray().Should().BeEmpty();
        stream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_ArrayFinalizationFailureStillDisposesOwnedStream()
    {
        var expected = new IOException("array finalization failed");
        var stream = new ReentrantDisposeStream
        {
            FailWriteAfterBytes = 1,
            WriteFailure = expected,
        };
        var sink = new JsonFileSink<string>(
            "dummy.json",
            stream,
            new JsonFileSinkOptions
            {
                Format = JsonFileFormat.Array,
                OpenMode = JsonFileOpenMode.Create,
            },
            leaveOpen: false);

        var thrown = await Assert.ThrowsAsync<IOException>(() => sink.DisposeAsync().AsTask());

        thrown.Should().BeSameAs(expected);
        stream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_FlushFailureStillDisposesOwnedStream()
    {
        var expected = new IOException("flush failed");
        var stream = new ReentrantDisposeStream { FlushFailure = expected };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10, leaveOpen: false);

        var thrown = await Assert.ThrowsAsync<IOException>(() => sink.DisposeAsync().AsTask());

        thrown.Should().BeSameAs(expected);
        stream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DualFailureAggregatesFinalizationBeforeCleanup()
    {
        var flushFailure = new IOException("flush failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var stream = new ReentrantDisposeStream
        {
            FlushFailure = flushFailure,
            DisposeFailure = disposeFailure,
        };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 10, leaveOpen: false);

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => sink.DisposeAsync().AsTask());

        thrown.InnerExceptions.Should().Equal(flushFailure, disposeFailure);
        stream.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task WriteAsync_FailedFlushPreservesBufferedItemsForSuccessfulRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        var stream = new ReentrantDisposeStream { FailWriteAfterBytes = 3 };
        var sink = new JsonFileSink<string>("dummy.json", stream, flushInterval: 1);

        await Assert.ThrowsAsync<IOException>(async () => await sink.WriteAsync(Envelope("first"), ct));
        await sink.WriteAsync(Envelope("second"), ct);

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var line = await reader.ReadLineAsync(ct);
        JsonSerializer.Deserialize<string[]>(line!).Should().Equal("first", "second");
        await sink.DisposeAsync();
    }

    private static ProcessingEnvelope<string> Envelope(string value) =>
        ProcessingEnvelope<string>.Create(value, "pipe", "run", 1);

    private sealed class ReentrantDisposeStream : MemoryStream
    {
        private bool _writeFailed;

        public Action? OnFlush { get; set; }
        public Action? OnDispose { get; set; }
        public Task? ReentrantTask { get; set; }
        public Exception? FlushFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public Exception? WriteFailure { get; set; }
        public int? FailWriteAfterBytes { get; set; }
        public int FlushCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool BlockFlush { get; set; }
        public TaskCompletionSource FlushEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource FlushRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_writeFailed && FailWriteAfterBytes is int bytes)
            {
                _writeFailed = true;
                Write(buffer.Span[..Math.Min(bytes, buffer.Length)]);
                throw WriteFailure ?? new IOException("partial write failed");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCount++;
            OnFlush?.Invoke();
            FlushEntered.TrySetResult();
            if (BlockFlush)
                await FlushRelease.Task.WaitAsync(cancellationToken);
            if (FlushFailure is not null)
                throw FlushFailure;
        }

        public void ReleaseFlush() => FlushRelease.TrySetResult();

        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            OnDispose?.Invoke();
            base.Dispose(true);
            GC.SuppressFinalize(this);
            if (DisposeFailure is not null)
                return ValueTask.FromException(DisposeFailure);
            return ValueTask.CompletedTask;
        }
    }
}
