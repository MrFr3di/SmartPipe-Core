using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class ProcessingSupervisorRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Drain_PreservesAcceptedWorkAndJoinsBeforeAnyCleanup(int concurrency)
    {
        var operation = new HeldOperations();
        var source = new WaitingSource(operation);
        var transformer = new HeldTransformer(operation);
        var sink = new CountingSink(operation);
        var run = PipelineBuilder.From(source).Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = concurrency })
            .To(sink);
        try
        {
            await operation.BothEntered.Task.WaitAsync(Deadline);
            await source.Waiting.Task.WaitAsync(Deadline);
            var drain = run.DrainAsync(Deadline).AsTask();
            await source.Stopped.Task.WaitAsync(Deadline);
            Assert.False(drain.IsCompleted);
            Assert.False(run.Completion.IsCompleted);
            Assert.Equal(0, operation.Disposals);
            Assert.Equal(0, operation.WorkerCancellations);
            operation.Release.TrySetResult();
            await drain.WaitAsync(Deadline);
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(PipelineRunState.Completed, run.State);
            Assert.Equal(2, operation.Exited);
            Assert.Equal(2, sink.Writes);
            Assert.Equal(3, operation.Disposals);
            Assert.False(operation.CleanupWhileActive);
            Assert.Equal(0, operation.WorkerCancellations);
            Assert.False(run.Outputs.TryRead(out _));
        }
        finally
        {
            operation.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class HeldOperations
    {
        public TaskCompletionSource BothEntered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public int Entered;
        public int Exited;
        public int Disposals;
        public int WorkerCancellations;
        public bool CleanupWhileActive;

        public ValueTask DisposeComponent()
        {
            if (Volatile.Read(ref Entered) != Volatile.Read(ref Exited))
                CleanupWhileActive = true;
            Interlocked.Increment(ref Disposals);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WaitingSource(HeldOperations operation) : IPipelineSource<int>
    {
        public TaskCompletionSource Waiting { get; } = Signal();
        public TaskCompletionSource Stopped { get; } = Signal();
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ProcessingEnvelope<int>.Create(1);
            yield return ProcessingEnvelope<int>.Create(2);
            var stopped = Signal();
            using var registration = ct.Register(() =>
            {
                Stopped.TrySetResult();
                stopped.TrySetCanceled(ct);
            });
            Waiting.TrySetResult();
            await stopped.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => operation.DisposeComponent();
    }

    private sealed class HeldTransformer(HeldOperations operation) : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            using var registration = ct.Register(() => Interlocked.Increment(ref operation.WorkerCancellations));
            if (Interlocked.Increment(ref operation.Entered) == 2)
                operation.BothEntered.TrySetResult();
            try
            {
                await operation.Release.Task.ConfigureAwait(false);
                return StageResult<int>.Success(envelope.Payload);
            }
            finally { Interlocked.Increment(ref operation.Exited); }
        }

        public ValueTask DisposeAsync() => operation.DisposeComponent();
    }

    private sealed class CountingSink(HeldOperations operation) : IPipelineSink<int>
    {
        public int Writes;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => operation.DisposeComponent();
    }
}
