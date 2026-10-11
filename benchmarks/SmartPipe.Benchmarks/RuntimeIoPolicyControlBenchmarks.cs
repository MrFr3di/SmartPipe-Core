using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

public enum RuntimeIoPolicyControl { EmitAll, EmitFailuresOnly, BufferedReliable, BufferedBestEffort, Telemetry, FullLineage }

[MemoryDiagnoser]
[BenchmarkCategory("RuntimeIo", "PolicyControls")]
public class RuntimeIoPolicyControlBenchmarks
{
    private const int Items = 1000;
    [ParamsAllValues] public RuntimeIoPolicyControl Scenario { get; set; }

    [Benchmark(OperationsPerInvoke = Items)]
    public async Task ProcessMixedItems()
    {
        using var activities = Scenario == RuntimeIoPolicyControl.Telemetry ? new ActivityListener
        {
            ShouldListenTo = source => source.Name == SmartPipeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        } : null;
        if (activities is not null) ActivitySource.AddActivityListener(activities);
        using var meter = Scenario == RuntimeIoPolicyControl.Telemetry ? new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SmartPipeDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            },
        } : null;
        meter?.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        meter?.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
        meter?.Start();
        var sink = new Sink();
        var observer = new Observer();
        var observed = Scenario is RuntimeIoPolicyControl.BufferedReliable or RuntimeIoPolicyControl.BufferedBestEffort;
        var options = new PipelineRuntimeOptions
        {
            MaxConcurrency = 8,
            OutputPolicy = Scenario switch
            {
                RuntimeIoPolicyControl.EmitAll => PipelineOutputPolicy.EmitAll,
                RuntimeIoPolicyControl.EmitFailuresOnly => PipelineOutputPolicy.EmitFailuresOnly,
                _ => PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            },
            ObserverDispatch = new ObserverDispatchOptions
            {
                Mode = Scenario switch
                {
                    RuntimeIoPolicyControl.BufferedReliable => ObserverDispatchMode.BufferedReliable,
                    RuntimeIoPolicyControl.BufferedBestEffort => ObserverDispatchMode.BufferedBestEffort,
                    _ => ObserverDispatchMode.Inline,
                },
                Capacity = 10_000,
            },
        };
        var builder = PipelineDefinitionBuilder.From(new PipelineKey("policy-control"),
            PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) => ValueTask.FromResult<IPipelineSource<int>>(new Source())))
            .WithRuntimeOptions(options).WithLineageMode(Scenario == RuntimeIoPolicyControl.FullLineage ? LineageMode.Full : LineageMode.Off)
            .Transform(new PipelineStageKey("policy-skip"), PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(new PolicySkipTransformer())),
                new StageFailureOptions { OnPermanentFailure = FailureAction.Skip })
            .Transform(new PipelineStageKey("mixture"), PipelineComponent.RuntimeOwned<IPipelineTransformer<int, int>>((_, _) =>
                ValueTask.FromResult<IPipelineTransformer<int, int>>(new Transformer())));
        if (observed) builder = builder.WithObserver(observer);
        var definition = builder.To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>((_, _) => ValueTask.FromResult<IPipelineSink<int>>(sink)));
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        var counts = new int[4];
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false)) counts[(int)output.Result.Kind]++;
        await run.Completion.ConfigureAwait(false);
        var expectedSuccess = Scenario == RuntimeIoPolicyControl.EmitAll ? Items / 4 : 0;
        var expectedControl = Scenario == RuntimeIoPolicyControl.EmitFailuresOnly ? 0 : Items / 4;
        if (run.State != PipelineRunState.Completed || sink.Count != Items / 4 || counts[0] != expectedSuccess
            || counts[1] != Items / 4 || counts[2] != expectedControl || counts[3] != 0
            || run.Metrics.ItemsProcessed != Items / 4 || run.Metrics.ItemsFailed != Items / 4 || run.Metrics.ItemsFiltered != Items / 4
            || run.Metrics.ObserverEventsDropped != 0 || (observed && observer.Successes != Items))
            throw new InvalidOperationException("Policy, mixed terminal, sink, telemetry or observer control failed.");
    }

    private sealed class Source : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            for (var i = 0; i < Items; i++) { ct.ThrowIfCancellationRequested(); yield return ProcessingEnvelope<int>.Create(i); }
            await Task.CompletedTask.ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Transformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public async ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            return (envelope.Payload % 4) switch
            {
                0 => StageResult<int>.Success(envelope.Payload),
                1 => StageResult<int>.Failure(new SmartPipeError("fixed failure", ErrorType.Permanent)),
                2 => StageResult<int>.Filtered(),
                _ => throw new InvalidOperationException("Policy-skipped item reached the next stage."),
            };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class PolicySkipTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.FromResult(envelope.Payload % 4 == 3
                ? StageResult<int>.Failure(new SmartPipeError("policy skip", ErrorType.Permanent))
                : StageResult<int>.Success(envelope.Payload));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Sink : IPipelineSink<int>
    {
        public int Count;
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Count);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Observer : IPipelineObserver
    {
        public int Successes;
        public ValueTask OnEventAsync(PipelineEvent evt, CancellationToken ct = default)
        {
            if (evt is StageSucceededEvent) Interlocked.Increment(ref Successes);
            return ValueTask.CompletedTask;
        }
    }
}
