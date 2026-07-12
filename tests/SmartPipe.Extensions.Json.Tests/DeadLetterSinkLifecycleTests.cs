using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Json.Tests;

public sealed class DeadLetterSinkLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_ReentrantWriterDoesNotStartSecondCleanup()
    {
        var writer = new ReentrantDeadLetterLineWriter();
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, writer);
        writer.OnDispose = () => writer.ReentrantTask = sink.DisposeAsync().AsTask();

        var disposeTask = sink.DisposeAsync().AsTask();
        await disposeTask;

        writer.ReentrantTask.Should().BeSameAs(disposeTask);
        writer.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallersReceiveSameExceptionInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var expected = new IOException("writer dispose failed");
        var writer = new ReentrantDeadLetterLineWriter { DisposeFailure = expected, BlockDispose = true };
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, writer);

        var firstTask = sink.DisposeAsync().AsTask();
        await writer.DisposeEntered.Task.WaitAsync(ct);
        var secondTask = sink.DisposeAsync().AsTask();
        writer.ReleaseDispose();
        var first = await Assert.ThrowsAsync<IOException>(() => firstTask);
        var second = await Assert.ThrowsAsync<IOException>(() => secondTask);

        secondTask.Should().BeSameAs(firstTask);
        second.Should().BeSameAs(first);
        first.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task DisposeAsync_FaultedWriterCleanupIsNotRetriedAndRejectsFurtherUse()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ReentrantDeadLetterLineWriter { DisposeFailure = new IOException("writer dispose failed") };
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, writer);

        var firstTask = sink.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => firstTask);
        var secondTask = sink.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<IOException>(() => secondTask);

        secondTask.Should().BeSameAs(firstTask);
        writer.DisposeCount.Should().Be(1);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.InitializeAsync(ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.WriteAsync(Envelope("late"), ct));
    }

    [Fact]
    public async Task DisposeAsync_BeginningDisposalRejectsInitializeAndWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var writer = new ReentrantDeadLetterLineWriter { BlockDispose = true };
        var sink = new DeadLetterSink<string>("dummy.json", logger: null, writer);

        var disposeTask = sink.DisposeAsync().AsTask();
        await writer.DisposeEntered.Task.WaitAsync(ct);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.InitializeAsync(ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await sink.WriteAsync(Envelope("late"), ct));
        writer.ReleaseDispose();
        await disposeTask;
    }

    private static ProcessingEnvelope<DeadLetterEnvelope<string>> Envelope(string payload) =>
        ProcessingEnvelope<DeadLetterEnvelope<string>>.Create(
            new DeadLetterEnvelope<string>
            {
                SchemaVersion = 1,
                PipelineId = "pipe",
                RunId = "run",
                TraceId = 1,
                StageId = "stage",
                StageName = "Stage",
                OriginalPayload = payload,
                Metadata = MetadataBag.Empty,
                Error = new SmartPipeError("failure", ErrorType.Permanent),
                Attempt = 1,
                FailedAtUtc = DateTimeOffset.UnixEpoch,
            },
            "pipe",
            "run",
            1);

    private sealed class ReentrantDeadLetterLineWriter : IDeadLetterLineWriter
    {
        public Action? OnDispose { get; set; }
        public Task? ReentrantTask { get; set; }
        public Exception? DisposeFailure { get; set; }
        public int DisposeCount { get; private set; }
        public bool BlockDispose { get; set; }
        public TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource DisposeRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteRecordAsync(ReadOnlyMemory<byte> record, bool flushEachWrite, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            OnDispose?.Invoke();
            DisposeEntered.TrySetResult();
            if (BlockDispose)
                await DisposeRelease.Task;
            if (DisposeFailure is not null)
                throw DisposeFailure;
        }

        public void ReleaseDispose() => DisposeRelease.TrySetResult();
    }
}
