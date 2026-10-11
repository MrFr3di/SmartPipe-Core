using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class RetryAdmissionRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task StageDeadlineDuringRetryDelay_RefundsForNextEnvelopeAndPreservesOriginalFailure()
    {
        var clock = new AdmissionTestTimeProvider();
        var starts = 0;
        var callbacks = 0;
        var observer = new Events();
        var run = PipelineBuilder.From(new Source(2)).Transform(new RecoveringTransformer(() => starts++), new StageFailureOptions
        {
            Retry = new RetryPolicy(2, TimeSpan.FromSeconds(1), onRetry: (_, _, _) => callbacks++),
            Timeout = new TimeoutPolicy { StageTimeout = TimeSpan.FromSeconds(3) },
        }).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            Clock = new TimeProviderPipelineClock(clock),
            RetryBudget = new RetryBudgetOptions(1, 0),
        }).WithObserver(observer).Run();
        try
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(5));
            var first = await run.Outputs.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.True(first.Result.IsFailure);
            Assert.Equal("initial", first.Result.Error!.Value.Message);
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await run.Completion.WaitAsync(Deadline);
            Assert.True(run.Outputs.TryRead(out var second));
            Assert.True(second.Result.IsSuccess);
            Assert.Equal(3, starts);
            Assert.Equal(1, callbacks);
            Assert.Single(observer.Recorded.OfType<RetryExhaustedEvent>());
            Assert.Single(observer.Recorded.OfType<RetryAttemptedEvent>());
            Assert.Equal(3, observer.Recorded.OfType<StageStartedEvent>().Count());
        }
        finally { await run.DisposeAsync(); }
    }

    [Fact]
    public async Task TwoStagesShareBudget_ReusableDefinitionStartsHaveIndependentState()
    {
        var starts = 0;
        var callbacks = 0;
        var predicates = 0;
        var policy = new StageFailureOptions
        {
            Retry = new RetryPolicy(2, TimeSpan.FromTicks(1),
                retryOn: _ => { Interlocked.Increment(ref predicates); return true; },
                onRetry: (_, _, _) => Interlocked.Increment(ref callbacks))
            { Jitter = RetryJitterMode.Full },
        };
        var builder = PipelineDefinitionBuilder.From(new PipelineKey("shared-budget"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new Source(1))))
            .WithRuntimeOptions(new PipelineRuntimeOptions { RetryBudget = new RetryBudgetOptions(1, 0) });
        var component = PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
            ValueTask.FromResult<IPipelineTransformer<int, int>>(new RecoveringTransformer(() => Interlocked.Increment(ref starts))));
        var definition = builder.Transform(new PipelineStageKey("stage-0"), component, policy)
            .Transform(new PipelineStageKey("stage-1"), component, policy).Build();
        var runs = await Task.WhenAll(definition.StartAsync(), definition.StartAsync());
        try
        {
            await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(Deadline);
            Assert.Equal(6, starts); // Each run: stage 1 initial + retry, stage 2 initial only.
            Assert.Equal(2, callbacks);
            Assert.Equal(4, predicates);
            foreach (var run in runs)
            {
                Assert.True(run.Outputs.TryRead(out var output));
                Assert.True(output.Result.IsFailure);
                Assert.False(run.Outputs.TryRead(out _));
            }
        }
        finally { foreach (var run in runs) await run.DisposeAsync(); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task FirstAttemptsAreFree_AdditionalStartsShareRunBudget(int concurrency)
    {
        var transformer = new FailingTransformer();
        var observer = new Events();
        var predicates = 0;
        var run = PipelineBuilder.From(new Source(20)).Transform(transformer, new StageFailureOptions
        {
            Retry = new RetryPolicy(10, TimeSpan.FromTicks(1), retryOn: _ => { Interlocked.Increment(ref predicates); return true; }),
            OnRetryExhausted = FailureAction.EmitFailureResult,
            OnPermanentFailure = FailureAction.FaultPipeline,
        }).WithRuntimeOptions(new PipelineRuntimeOptions
        {
            MaxConcurrency = concurrency,
            RetryBudget = new RetryBudgetOptions(3, 0),
        }).WithObserver(observer).Run();
        try
        {
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(23, transformer.Calls);
            Assert.Equal(23, predicates);
            Assert.Equal(3, observer.Recorded.OfType<RetryAttemptedEvent>().Count());
            Assert.Equal(20, observer.Recorded.OfType<RetryExhaustedEvent>().Count());
            var outputs = new List<PipelineOutput<int>>();
            while (run.Outputs.TryRead(out var output)) outputs.Add(output);
            Assert.Equal(20, outputs.Count);
            Assert.All(outputs, output => Assert.Equal(transformer.Error, output.Result.Error));
        }
        finally { await run.DisposeAsync(); }
    }

    internal sealed class FailingTransformer : IPipelineTransformer<int, int>
    {
        public int Calls;
        public SmartPipeError Error { get; } = new("original", ErrorType.Transient);
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return ValueTask.FromResult(StageResult<int>.Failure(Error));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecoveringTransformer(Action started) : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            started();
            return ValueTask.FromResult(envelope.Attempt == 0
                ? StageResult<int>.Failure(new SmartPipeError("initial", ErrorType.Transient))
                : StageResult<int>.Success(envelope.Payload));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Source(int count) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); yield return ProcessingEnvelope<int>.Create(i); }
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class Events : IPipelineObserver
    {
        public System.Collections.Concurrent.ConcurrentQueue<PipelineEvent> Recorded { get; } = new();
        public ValueTask OnEventAsync(PipelineEvent evt, CancellationToken ct = default)
        {
            Recorded.Enqueue(evt);
            return ValueTask.CompletedTask;
        }
    }
}
