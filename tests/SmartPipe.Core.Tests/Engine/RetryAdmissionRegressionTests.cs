using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class RetryAdmissionRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

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
