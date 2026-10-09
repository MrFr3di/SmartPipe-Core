using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
[Trait("Category", "ConcurrencyRegression")]
public sealed class AdaptiveFailureAccountingRegressionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(FailureAction.EmitFailureResult, false, false)]
    [InlineData(FailureAction.EmitFailureResult, true, false)]
    [InlineData(FailureAction.EmitFailureResult, false, true)]
    [InlineData(FailureAction.EmitFailureResult, true, true)]
    [InlineData(FailureAction.DeadLetter, false, false)]
    [InlineData(FailureAction.DeadLetter, true, false)]
    [InlineData(FailureAction.DeadLetter, false, true)]
    [InlineData(FailureAction.DeadLetter, true, true)]
    [InlineData(FailureAction.Skip, false, false)]
    [InlineData(FailureAction.Skip, true, false)]
    [InlineData(FailureAction.Skip, false, true)]
    [InlineData(FailureAction.Skip, true, true)]
    [InlineData(FailureAction.StopPipeline, false, false)]
    [InlineData(FailureAction.StopPipeline, true, false)]
    [InlineData(FailureAction.StopPipeline, false, true)]
    [InlineData(FailureAction.StopPipeline, true, true)]
    [InlineData(FailureAction.FaultPipeline, false, false)]
    [InlineData(FailureAction.FaultPipeline, true, false)]
    [InlineData(FailureAction.FaultPipeline, false, true)]
    [InlineData(FailureAction.FaultPipeline, true, true)]
    public async Task TerminalFailure_ReducesLimitRegardlessOfPolicy(FailureAction action, bool retry, bool secondStage)
    {
        var clock = new ManualClock();
        var transformer = new ResultTransformer(clock, retry ? "Transient" : "Failure");
        var serializer = new CapturingSerializer();
        using var stream = new MemoryStream();
        var options = new StageFailureOptions
        {
            OnPermanentFailure = action,
            OnRetryExhausted = action,
            Retry = retry ? new RetryPolicy(1, TimeSpan.FromTicks(1)) : null,
        };
        var stages = new List<ITypedPipelineStage>();
        if (secondStage)
            stages.Add(new TypedPipelineStage<int, int>(new ResultTransformer(clock, "Success"), 0));
        stages.Add(new TypedPipelineStage<int, int>(transformer, stages.Count, options,
            action == FailureAction.DeadLetter ? new StageDeadLetterOptions<int>(stream, serializer) : null));
        var executor = CreateExecutor(clock, stages);
        var run = executor.Start();
        try
        {
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            if (action == FailureAction.FaultPipeline)
                Assert.IsType<PipelineFailureActionException>(error);
            else
                Assert.Null(error);
            Assert.Equal(2, executor.CurrentAdaptiveConcurrency);
            Assert.Equal(retry ? 2 : 1, transformer.Calls);
            var outputs = new List<PipelineOutput<int>>();
            while (run.Outputs.TryRead(out var output))
                outputs.Add(output);
            Assert.Equal(action is FailureAction.EmitFailureResult or FailureAction.DeadLetter or FailureAction.StopPipeline ? 1 : 0, outputs.Count);
            Assert.All(outputs, output => Assert.False(output.Result.IsSuccess));
            Assert.Equal(action == FailureAction.DeadLetter ? 1 : 0, serializer.Writes);
        }
        finally
        {
            await executor.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("Success", 4)]
    [InlineData("Filtered", 4)]
    [InlineData("Recover", 4)]
    [InlineData("Cancelled", 2)]
    [InlineData("TimedOut", 2)]
    [InlineData("Throw", 2)]
    public async Task FinalOutcome_DistinguishesControlFlowAndRecoveredRetry(string kind, int expectedLimit)
    {
        var clock = new ManualClock();
        var transformer = new ResultTransformer(clock, kind);
        var stage = new TypedPipelineStage<int, int>(transformer, 0, new StageFailureOptions
        {
            Retry = kind == "Recover" ? new RetryPolicy(1, TimeSpan.FromTicks(1)) : null,
        });
        var executor = CreateExecutor(clock, [stage]);
        var run = executor.Start();
        try
        {
            await run.Completion.WaitAsync(Deadline);
            Assert.Equal(expectedLimit, executor.CurrentAdaptiveConcurrency);
            Assert.Equal(kind == "Recover" ? 2 : 1, transformer.Calls);
        }
        finally
        {
            await executor.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task ExternalShutdown_DoesNotCreateFailurePressureSample()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualClock();
        var transformer = new ResultTransformer(clock, "Shutdown") { RequestStop = cancellation.Cancel };
        var executor = CreateExecutor(clock, [new TypedPipelineStage<int, int>(transformer, 0)], cancellation.Token);
        var run = executor.Start();
        try
        {
            var error = await Record.ExceptionAsync(async () => await run.Completion.WaitAsync(Deadline));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.Equal(3, executor.CurrentAdaptiveConcurrency);
        }
        finally
        {
            await executor.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    private static TypedPipelineExecutor<int, int> CreateExecutor(ManualClock clock, IReadOnlyList<ITypedPipelineStage> stages, CancellationToken ct = default)
    {
        var graph = new ActivatedPipelineGraph<int, int>
        {
            Source = new SingleSource(),
            Stages = stages,
            Observers = [],
            Lifetime = new PipelineActivationLedger(),
        };
        return new TypedPipelineExecutor<int, int>(new PipelineKey("adaptive-failure-tests"), Guid.NewGuid(), graph,
            new PipelineRuntimeOptions
            {
                MaxConcurrency = 4,
                Clock = clock,
                AdaptiveParallelism = new AdaptiveParallelismOptions
                {
                    Enabled = true,
                    MinConcurrency = 1,
                    MaxConcurrency = 4,
                    InitialConcurrency = 3,
                    TargetLatency = TimeSpan.FromMilliseconds(100),
                    DeadZone = TimeSpan.FromMilliseconds(5),
                    EvaluationInterval = TimeSpan.FromTicks(1),
                    AdjustmentCooldown = TimeSpan.FromTicks(1),
                    MinimumFailureSamples = 1,
                    MinSmoothingFactor = 1,
                },
            }, LineageMode.Off, false,
            Channel.CreateBounded<PipelineOutput<int>>(8), ct,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class ManualClock : IPipelineClock
    {
        private long _ticks;
        public void Advance() => Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond);
        public DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public long GetTimestamp() => Interlocked.Read(ref _ticks);
        public TimeSpan GetElapsedTime(long start, long end) => TimeSpan.FromTicks(end - start);
    }

    private sealed class ResultTransformer(ManualClock clock, string kind) : IPipelineTransformer<int, int>
    {
        public int Calls { get; private set; }
        public Action? RequestStop { get; init; }
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Calls++;
            clock.Advance();
            if (kind == "Shutdown")
            {
                RequestStop!();
                ct.ThrowIfCancellationRequested();
            }
            var error = new SmartPipeError("test failure", kind is "Transient" or "Recover" ? ErrorType.Transient : ErrorType.Permanent);
            return ValueTask.FromResult(kind switch
            {
                "Success" => StageResult<int>.Success(envelope.Payload),
                "Filtered" => StageResult<int>.Filtered(),
                "Recover" when Calls > 1 => StageResult<int>.Success(envelope.Payload),
                "Cancelled" => StageResult<int>.Cancelled(),
                "TimedOut" => StageResult<int>.TimedOut(error),
                "Throw" => throw new InvalidOperationException("test transformer exception"),
                _ => StageResult<int>.Failure(error),
            });
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleSource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            yield return ProcessingEnvelope<int>.Create(1, "adaptive-failure-tests", "run", 1);
            await Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSerializer : IDeadLetterSerializer<int>
    {
        public int Writes { get; private set; }
        public ValueTask WriteAsync(DeadLetterEnvelope<int> envelope, Stream stream, CancellationToken ct = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
        public async IAsyncEnumerable<DeadLetterEnvelope<int>> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
