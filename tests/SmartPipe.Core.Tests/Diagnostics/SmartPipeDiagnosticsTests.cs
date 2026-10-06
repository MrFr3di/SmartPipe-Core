#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Diagnostics;

public class SmartPipeDiagnosticsTests
{
    [Fact]
    public void SmartPipeDiagnostics_MeterName_IsReleaseContract()
        => SmartPipeDiagnostics.MeterName.Should().Be("SmartPipe.Core");

    [Fact]
    public void SmartPipeDiagnostics_ActivitySourceName_IsReleaseContract()
        => SmartPipeDiagnostics.ActivitySourceName.Should().Be("SmartPipe.Core");

    [Fact]
    public void SmartPipeMeter_Name_AliasesSmartPipeDiagnosticsMeterName()
        => SmartPipeMeter.Name.Should().Be(SmartPipeDiagnostics.MeterName);

    [Fact]
    public void SmartPipeMeter_Meter_UsesCanonicalMeterName()
        => SmartPipeMeter.Meter.Name.Should().Be(SmartPipeDiagnostics.MeterName);

    [Fact]
    public void SmartPipeActivitySource_Name_AliasesSmartPipeDiagnosticsActivitySourceName()
        => SmartPipeActivitySource.Name.Should().Be(SmartPipeDiagnostics.ActivitySourceName);

    [Fact]
    public void SmartPipeActivitySource_Source_UsesCanonicalActivitySourceName()
        => SmartPipeActivitySource.Source.Name.Should().Be(SmartPipeDiagnostics.ActivitySourceName);

    [Fact]
    public void SmartPipeMeter_InstrumentManifest_IsFrozenReleaseContract()
    {
        var instruments = CapturePublishedInstruments();

        instruments.Should().BeEquivalentTo(new[]
        {
            ("smartpipe.items.processed", "items"),
            ("smartpipe.items.failed", "items"),
            ("smartpipe.items.filtered", "items"),
            ("smartpipe.items.dropped", "items"),
            ("smartpipe.output.items.dropped", "items"),
            ("smartpipe.observer.events.dropped", "events"),
            ("smartpipe.items.retried", "items"),
            ("smartpipe.items.deadlettered", "items"),
            ("smartpipe.items.duplicates_filtered", "items"),
            ("smartpipe.stage.duration", "ms"),
            ("smartpipe.sink.duration", "ms"),
        });
    }

    [Fact]
    public void SmartPipeMeter_InstrumentKinds_AreFrozenReleaseContract()
    {
        var instruments = CapturePublishedInstruments();

        instruments.Where(i => i.Name.StartsWith("smartpipe.stage.", StringComparison.Ordinal)
            || i.Name.StartsWith("smartpipe.sink.", StringComparison.Ordinal))
            .Should().OnlyContain(i => i.IsHistogram);
        instruments.Where(i => !i.IsHistogram)
            .Should().OnlyContain(i => i.IsCounter);
    }

    [Fact]
    public async Task SmartPipeActivitySource_ActivityManifest_IsFrozenReleaseContract()
    {
        var stoppedActivities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SmartPipeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivities.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        await using var pipelineRun = PipelineBuilder
            .FromFactory<int>(_ => new DiagnosticsTestSource(77))
            .TransformFactory<int>(_ => new DiagnosticsTestTransformer())
            .ToFactory(_ => new DiagnosticsTestSink());
        await pipelineRun.Completion;

        var operations = stoppedActivities
            .Where(activity => activity.Source.Name == SmartPipeDiagnostics.ActivitySourceName)
            .Select(activity => activity.OperationName)
            .ToArray();
        operations.Should().Contain("Pipeline.Run");
        operations.Should().Contain("Transform");
    }

    private static (string Name, string? Unit, bool IsCounter, bool IsHistogram)[] CapturePublishedInstruments()
    {
        var published = new ConcurrentQueue<(string Name, string? Unit, bool IsCounter, bool IsHistogram)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name != SmartPipeDiagnostics.MeterName)
                    return;
                published.Enqueue((
                    instrument.Name,
                    instrument.Unit,
                    instrument.GetType().IsAssignableTo(typeof(Counter<long>)),
                    instrument.GetType().IsAssignableTo(typeof(Histogram<double>))));
                meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.Start();

        var recorder = new SmartPipeMetricsRecorder();
        recorder.RecordProcessed(1.5);
        recorder.RecordFailed();
        recorder.RecordFiltered();
        recorder.RecordItemDropped();
        recorder.RecordOutputDropped();
        recorder.RecordObserverEventDropped();
        recorder.RecordDuplicate();
        recorder.RecordRetry();
        recorder.RecordDeadLetter();
        recorder.RecordSinkDuration(0.5);

        var snapshot = published.ToArray();
        snapshot.Should().NotBeEmpty("the SmartPipe meter must publish its instruments");
        return snapshot;
    }

    private sealed class DiagnosticsTestSource(params int[] items) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<int>.Create(item, "diagnostics-test", "run", 1);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DiagnosticsTestTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DiagnosticsTestSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
