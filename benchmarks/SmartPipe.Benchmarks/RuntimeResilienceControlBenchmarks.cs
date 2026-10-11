using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

public enum ResilienceControlScenario { Unlimited, Budget, TimedWait, TimedReject }

[MemoryDiagnoser]
[BenchmarkCategory("RuntimeIo", "ResilienceControls")]
public class RuntimeResilienceControlBenchmarks
{
    private const int Items = 1000;
    [ParamsAllValues] public ResilienceControlScenario Scenario { get; set; }
    [Params(false, true)] public bool AsyncTransform { get; set; }

    [Benchmark(OperationsPerInvoke = Items)]
    public async Task ProcessItems() => await RunAsync(false).ConfigureAwait(false);

    internal async Task<ControlResult> RunAsync(bool instrument)
    {
        var starts = instrument ? new long[Items] : null;
        var latencies = instrument ? new double[Items] : null;
        var transformer = new Transformer(AsyncTransform);
        var admission = Scenario switch
        {
            ResilienceControlScenario.TimedWait => new TimedAttemptAdmissionOptions(4, TimedAttemptAdmissionMode.Wait),
            ResilienceControlScenario.TimedReject => new TimedAttemptAdmissionOptions(4, TimedAttemptAdmissionMode.Reject),
            _ => null,
        };
        var definition = PipelineDefinitionBuilder.From(new PipelineKey("resilience-control"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new Source(starts))))
            .WithLineageMode(LineageMode.Off).WithRuntimeOptions(new PipelineRuntimeOptions
            {
                MaxConcurrency = 8,
                RetryBudget = Scenario == ResilienceControlScenario.Unlimited ? null : new RetryBudgetOptions(100, 0),
                TimedAttemptAdmission = admission,
            }).Transform(new PipelineStageKey("fault-stream"),
                PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) => ValueTask.FromResult<IPipelineTransformer<int, int>>(transformer)),
                new StageFailureOptions
                {
                    Retry = new RetryPolicy(2, TimeSpan.FromTicks(1)) { Jitter = RetryJitterMode.Full },
                    Timeout = admission is null ? null : new TimeoutPolicy { AttemptTimeout = TimeSpan.FromMinutes(1) },
                }).Build();
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        var outputs = 0;
        var successes = 0;
        var rejections = 0;
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
        {
            outputs++;
            if (output.Result.IsSuccess) successes++;
            if (output.Result.Error?.Category == "TimedAttemptAdmission") rejections++;
            if (latencies is not null && starts is not null)
            {
                var index = checked((int)output.Result.TraceId - 1);
                latencies[index] = Stopwatch.GetElapsedTime(starts[index]).TotalMicroseconds;
            }
        }
        await run.Completion.ConfigureAwait(false);
        ValidateResult(run, transformer, admission, outputs, successes, rejections);
        double? p99 = null;
        if (latencies is not null) { Array.Sort(latencies); p99 = latencies[(int)Math.Ceiling(Items * 0.99) - 1]; }
        return new ControlResult(Scenario, AsyncTransform, transformer.Initial, transformer.Retries, transformer.Peak, admission is null ? 0 : transformer.Peak,
            successes, rejections, run.Metrics.ItemsRetried, p99);
    }

    private void ValidateResult(PipelineRun<int> run, Transformer transformer, TimedAttemptAdmissionOptions? admission,
        int outputs, int successes, int rejections)
    {
        var expectedRetries = Scenario == ResilienceControlScenario.Unlimited ? 200 : 100;
        if (outputs != Items || run.State != PipelineRunState.Completed || transformer.Active != 0
            || transformer.Retries > expectedRetries || (admission is not null && transformer.Peak > 4)
            || (Scenario != ResilienceControlScenario.TimedReject
                && (transformer.Initial != Items || transformer.Retries != expectedRetries || successes != 800 + expectedRetries))
            || (Scenario == ResilienceControlScenario.TimedReject && rejections == 0 && AsyncTransform && transformer.Peak > 4))
            throw new InvalidOperationException("Fault-stream invocation, retry, output or timed-cap invariant failed.");
    }

    internal static async Task WriteDiagnosticsAsync(string path, int warmupRuns = 1, int repetitions = 3)
    {
        var results = new List<ControlResult>();
        foreach (var scenario in Enum.GetValues<ResilienceControlScenario>())
            foreach (var asynchronous in new[] { false, true })
            {
                var benchmark = new RuntimeResilienceControlBenchmarks { Scenario = scenario, AsyncTransform = asynchronous };
                for (var warmup = 0; warmup < warmupRuns; warmup++) await benchmark.RunAsync(false).ConfigureAwait(false);
                for (var repetition = 0; repetition < repetitions; repetition++) results.Add(await benchmark.RunAsync(true).ConfigureAwait(false));
            }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    internal sealed record ControlResult(ResilienceControlScenario Scenario, bool AsyncTransform, int InitialStarts,
        int RetryStarts, int PeakActiveExecutions, int PeakActiveTimed, int SuccessfulOutputs, int AdmissionRejections, long ScheduledRetries, double? ItemP99Microseconds);

    private sealed class Source(long[]? starts) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < Items; i++)
            {
                ct.ThrowIfCancellationRequested();
                var envelope = ProcessingEnvelope<int>.Create(i, "resilience-control", "run", (ulong)i + 1);
                if (starts is not null) starts[i] = Stopwatch.GetTimestamp();
                yield return envelope;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Transformer(bool asynchronous) : IPipelineTransformer<int, int>
    {
        public int Initial, Retries, Active, Peak;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            if (envelope.Attempt == 0) Interlocked.Increment(ref Initial); else Interlocked.Increment(ref Retries);
            var active = Interlocked.Increment(ref Active);
            int peak;
            do { peak = Volatile.Read(ref Peak); } while (active > peak && Interlocked.CompareExchange(ref Peak, active, peak) != peak);
            try
            {
                if (asynchronous) await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return envelope.Attempt == 0 && envelope.Payload % 5 == 0
                    ? StageResult<int>.Failure(new SmartPipeError("fixed fault stream", ErrorType.Transient))
                    : StageResult<int>.Success(envelope.Payload);
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
