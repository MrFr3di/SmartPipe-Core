using SmartPipe.Core;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SmartPipe.Core.Tests.Engine;

[Trait("Category", "CorrectnessRegression")]
public sealed class ObserverInterestRegressionTests
{
    [Theory]
    [InlineData(ObserverDispatchMode.Inline, false)]
    [InlineData(ObserverDispatchMode.BufferedReliable, false)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort, false)]
    [InlineData(ObserverDispatchMode.Inline, true)]
    [InlineData(ObserverDispatchMode.BufferedReliable, true)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort, true)]
    public async Task NoObservers_PreservesTelemetryAndSinkFailure(ObserverDispatchMode mode, bool sinkFails)
    {
        var pipelineId = "observer-interest-" + Guid.NewGuid().ToString("N");
        var stopped = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        var pipelineActivityStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measurements = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SmartPipeDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("smartpipe.pipeline_id"), pipelineId))
                {
                    stopped.Enqueue(activity);
                    if (activity.OperationName == "Pipeline.Run")
                        pipelineActivityStopped.TrySetResult();
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meter = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == SmartPipeDiagnostics.MeterName) listener.EnableMeasurementEvents(instrument);
            },
        };
        meter.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "smartpipe.pipeline_id" && Equals(tag.Value, pipelineId)) measurements.Enqueue(instrument.Name);
        });
        meter.Start();
        var sink = new TestSink(sinkFails);
        var run = PipelineBuilder.From(new RetryAdmissionRegressionTests.Source(1))
            .Transform(PipelineTransformer.FromFunc<int, int>((value, _) => ValueTask.FromResult(value)))
            .WithPipelineId(pipelineId).WithRuntimeOptions(new PipelineRuntimeOptions
            {
                ObserverDispatch = new ObserverDispatchOptions { Mode = mode, Capacity = 1 },
            }).To(sink);
        try
        {
            var error = await Record.ExceptionAsync(() => run.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
            if (sinkFails) Assert.Same(sink.Error, error);
            else Assert.Null(error);
            Assert.Equal(1, sink.Calls);
            Assert.False(run.Outputs.TryRead(out _));
            Assert.Equal(sinkFails ? 0 : 1, run.Metrics.ItemsProcessed);
            Assert.Equal(0, run.Metrics.ObserverEventsDropped);
            Assert.DoesNotContain("smartpipe.observer.events.dropped", measurements);
            Assert.Equal(!sinkFails, measurements.Contains("smartpipe.items.processed"));
            // Completion is published before RunCoreAsync disposes its outer Activity.
            // Wait for ActivityStopped rather than racing the final diagnostic callback.
            await pipelineActivityStopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(stopped, activity => activity.OperationName == "Transform");
            Assert.Contains(stopped, activity => activity.OperationName == "Pipeline.Run"
                && activity.Status == (sinkFails ? ActivityStatusCode.Error : ActivityStatusCode.Ok));
        }
        finally { await run.DisposeAsync(); }
    }
    [Theory]
    [InlineData(ObserverDispatchMode.Inline)]
    [InlineData(ObserverDispatchMode.BufferedReliable)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort)]
    public async Task InitialRegistrationInterest_IsImmutableAfterObserverRemoval(ObserverDispatchMode mode)
    {
        var observer = new ThrowingObserver();
        await using var dispatcher = PipelineObserverDispatcher.Create(
            [new PipelineObserverRegistration(observer, FailurePolicy: ObserverFailurePolicy.RemoveObserver)],
            new ObserverDispatchOptions { Mode = mode }, SystemPipelineClock.Instance);
        Assert.True(dispatcher.HasObservers);
        var evt = new PipelineStartedEvent("pipeline", "run", DateTimeOffset.UtcNow);
        await dispatcher.EmitAsync(evt, default);
        await dispatcher.FlushAsync(default);
        await dispatcher.EmitAsync(evt, default);
        await dispatcher.FlushAsync(default);
        Assert.Equal(1, observer.Calls);
        Assert.True(dispatcher.HasObservers);
        await dispatcher.CompleteAsync(default);
    }

    [Theory]
    [InlineData(ObserverDispatchMode.Inline)]
    [InlineData(ObserverDispatchMode.BufferedReliable)]
    [InlineData(ObserverDispatchMode.BufferedBestEffort)]
    public async Task EmptyRegistration_HasNoInterestButLifecycleStillCompletes(ObserverDispatchMode mode)
    {
        await using var dispatcher = PipelineObserverDispatcher.Create([], new ObserverDispatchOptions { Mode = mode }, SystemPipelineClock.Instance);
        Assert.False(dispatcher.HasObservers);
        await dispatcher.StopCallbacksAsync();
        await dispatcher.FlushAsync(default);
        await dispatcher.CompleteAsync(default);
    }

    private sealed class ThrowingObserver : IPipelineObserver
    {
        public int Calls;
        public ValueTask OnEventAsync(PipelineEvent evt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            throw new IOException("remove");
        }
    }

    private sealed class TestSink(bool fails) : IPipelineSink<int>
    {
        public int Calls;
        public IOException Error { get; } = new("sink failure");
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
        {
            Calls++;
            if (fails) throw Error;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
