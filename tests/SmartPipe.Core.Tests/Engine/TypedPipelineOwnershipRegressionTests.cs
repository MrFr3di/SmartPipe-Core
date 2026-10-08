using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class TypedPipelineOwnershipRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(2, false)]
    [InlineData(8, false)]
    [InlineData(2, true)]
    [InlineData(8, true)]
    public async Task ProducerFault_JoinsActiveOperationBeforeDisposingComponents(int concurrency, bool blockSink)
    {
        var operation = new HeldOperation();
        var failure = new IOException("source failed while worker was active");
        var source = new FaultAfterEntrySource(operation, failure);
        var transformer = new HeldTransformer(operation, !blockSink);
        var sink = new HeldSink(operation, blockSink);
        var run = PipelineBuilder.From(source).Transform(transformer)
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = concurrency })
            .To(sink);

        try
        {
            await operation.Entered.Task.WaitAsync(Deadline);
            var signal = await Task.WhenAny(operation.CancellationObserved.Task, operation.Disposed.Task)
                .WaitAsync(Deadline);
            Assert.Same(operation.CancellationObserved.Task, signal);
            Assert.False(operation.Disposed.Task.IsCompleted);
            Assert.False(run.Completion.IsCompleted);
            operation.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Same(failure, error);
            Assert.True(operation.Exited.Task.IsCompleted);
            Assert.False(operation.DisposedWhileActive);
            Assert.Equal(1, transformer.DisposeCount);
            Assert.Equal(1, sink.DisposeCount);
            Assert.Equal(PipelineRunState.Faulted, run.State);
        }
        finally
        {
            operation.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerFault_RetainsCallbackOrIndependentWorkerFailure(bool throwCallback)
    {
        var secondary = new InvalidOperationException("secondary worker stop failure");
        var operation = new HeldOperation
        {
            CallbackFailure = throwCallback ? secondary : null,
            WorkerFailure = throwCallback ? null : secondary,
        };
        var primary = new IOException("source failed");
        var run = PipelineBuilder.From(new FaultAfterEntrySource(operation, primary))
            .Transform(new HeldTransformer(operation, false))
            .WithRuntimeOptions(new PipelineRuntimeOptions { MaxConcurrency = 2 })
            .To(new HeldSink(operation, true));
        try
        {
            await operation.CancellationObserved.Task.WaitAsync(Deadline);
            Assert.False(operation.Disposed.Task.IsCompleted);
            operation.Release.TrySetResult();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            var combined = Assert.IsType<AggregateException>(error);
            Assert.Collection(combined.Flatten().InnerExceptions,
                first => Assert.Same(primary, first),
                second => Assert.Same(secondary, second));
            Assert.False(operation.DisposedWhileActive);
        }
        finally
        {
            operation.Release.TrySetResult();
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ProducerFault_UnblocksBoundedOutputWithoutConsumer()
    {
        var source = new BackpressureFaultSource();
        var run = PipelineBuilder.From(source)
            .Transform(new OutputBarrierTransformer(source))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 2,
                OutputCapacity = 1,
            }).Run();
        try
        {
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.Same(source.Failure, error);
            Assert.Equal(PipelineRunState.Faulted, run.State);
        }
        finally
        {
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    public async Task EnumeratorCleanup_PreservesOriginalExceptions(int concurrency, bool failRead, bool failCleanup)
    {
        var readError = failRead ? new IOException("read failed") : null;
        var cleanupError = failCleanup ? new InvalidOperationException("cursor close failed") : null;
        var source = new FailingEnumeratorSource(readError, cleanupError);
        var run = PipelineBuilder.From(source).Transform(static value => value).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = concurrency,
        }).Run();
        try
        {
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            if (failRead && failCleanup)
            {
                var combined = Assert.IsType<AggregateException>(error);
                Assert.Collection(combined.Flatten().InnerExceptions,
                    first => Assert.Same(readError, first),
                    second => Assert.Same(cleanupError, second));
            }
            else
            {
                Assert.Same((Exception?)readError ?? cleanupError, error);
            }
            Assert.Equal(1, source.EnumeratorDisposeCount);
            Assert.Equal(PipelineRunState.Faulted, run.State);
        }
        finally
        {
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EnumeratorCleanup_AfterCancellation_FaultsWithBothCauses(int concurrency)
    {
        var cleanup = new InvalidOperationException("cursor cleanup failed after cancellation");
        var source = new FailingEnumeratorSource(null, cleanup, waitForCancellation: true);
        var run = PipelineBuilder.From(source).Transform(static value => value).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = concurrency,
        }).Run();
        try
        {
            await source.ReadEntered.Task.WaitAsync(Deadline);
            await run.CancelAsync();
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            var combined = Assert.IsType<AggregateException>(error);
            Assert.Collection(combined.Flatten().InnerExceptions,
                first => Assert.IsAssignableFrom<OperationCanceledException>(first),
                second => Assert.Same(cleanup, second));
            Assert.Equal(PipelineRunState.Faulted, run.State);
            Assert.Equal(1, source.EnumeratorDisposeCount);
        }
        finally
        {
            await run.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private sealed class HeldOperation
    {
        public TaskCompletionSource Entered { get; } = NewSignal();
        public TaskCompletionSource CancellationObserved { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public TaskCompletionSource Exited { get; } = NewSignal();
        public TaskCompletionSource Disposed { get; } = NewSignal();
        public bool DisposedWhileActive { get; private set; }
        public Exception? CallbackFailure { get; init; }
        public Exception? WorkerFailure { get; init; }

        public async ValueTask ExecuteAsync(CancellationToken ct)
        {
            using var registration = CallbackFailure is { } callbackFailure
                ? ct.Register(() => throw callbackFailure)
                : default;
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                await Release.Task;
                Exited.TrySetResult();
                if (WorkerFailure is not null)
                    throw WorkerFailure;
                throw;
            }
        }

        public void RecordDispose()
        {
            if (Entered.Task.IsCompleted && !Exited.Task.IsCompleted)
                DisposedWhileActive = true;
            Disposed.TrySetResult();
        }
    }

    private sealed class FaultAfterEntrySource(HeldOperation operation, Exception failure) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ProcessingEnvelope<int>.Create(1);
            await operation.Entered.Task.WaitAsync(ct);
            throw failure;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldTransformer(HeldOperation operation, bool hold) : IPipelineTransformer<int, int>
    {
        public int DisposeCount { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            if (hold)
                await operation.ExecuteAsync(ct);
            return StageResult<int>.Success(envelope.Payload);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            operation.RecordDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HeldSink(HeldOperation operation, bool hold) : IPipelineSink<int>
    {
        public int DisposeCount { get; private set; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            hold ? operation.ExecuteAsync(ct) : ValueTask.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            operation.RecordDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingEnumeratorSource(
        Exception? readError,
        Exception? cleanupError,
        bool waitForCancellation = false) : IPipelineSource<int>
    {
        private readonly Exception? _readError = readError;
        private readonly Exception? _cleanupError = cleanupError;
        private readonly bool _waitForCancellation = waitForCancellation;
        public int EnumeratorDisposeCount { get; private set; }
        public TaskCompletionSource ReadEntered { get; } = NewSignal();
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(CancellationToken ct = default) => new Sequence(this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Sequence(FailingEnumeratorSource owner) : IAsyncEnumerable<ProcessingEnvelope<int>>
        {
            public IAsyncEnumerator<ProcessingEnvelope<int>> GetAsyncEnumerator(CancellationToken ct = default) => new Enumerator(owner, ct);
        }
        private sealed class Enumerator(FailingEnumeratorSource owner, CancellationToken ct) : IAsyncEnumerator<ProcessingEnvelope<int>>
        {
            public ProcessingEnvelope<int> Current => throw new InvalidOperationException("No current item.");
            public async ValueTask<bool> MoveNextAsync()
            {
                owner.ReadEntered.TrySetResult();
                if (owner._waitForCancellation)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                if (owner._readError is not null)
                    throw owner._readError;
                return false;
            }
            public ValueTask DisposeAsync()
            {
                owner.EnumeratorDisposeCount++;
                if (owner._cleanupError is not null)
                    throw owner._cleanupError;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BackpressureFaultSource : IPipelineSource<int>
    {
        public Exception Failure { get; } = new IOException("source failed with bounded output");
        public TaskCompletionSource SecondTransform { get; } = NewSignal();
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return ProcessingEnvelope<int>.Create(1);
            yield return ProcessingEnvelope<int>.Create(2);
            // Both workers have reached processing. One output fills capacity,
            // the other may be waiting to publish; fault must release that wait.
            await SecondTransform.Task.WaitAsync(ct);
            throw Failure;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OutputBarrierTransformer(BackpressureFaultSource source) : IPipelineTransformer<int, int>
    {
        private int _calls;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
                source.SecondTransform.TrySetResult();
            return ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
