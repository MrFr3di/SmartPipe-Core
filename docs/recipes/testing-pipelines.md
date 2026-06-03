# Testing Pipelines

Prefer small typed pipeline tests that use real sources, transformers, sinks,
and observers. Avoid sleeping for timing-sensitive behavior when a clock or
channel signal can make the test deterministic.

## Success Path

```csharp
var sink = new CollectingSink<string>();

var run = PipelineBuilder
    .From(new ArraySource<int>(1, 2, 3))
    .Transform(new MapStage<int, string>(x => x.ToString(CultureInfo.InvariantCulture)))
    .To(sink);

var outputs = new List<PipelineOutput<string>>();
await foreach (var output in run.Outputs.ReadAllAsync())
    outputs.Add(output);

await run.Completion;

outputs.Select(x => x.Result.Value).Should().Equal("1", "2", "3");
sink.Payloads.Should().Equal("1", "2", "3");
```

Read `run.Outputs` before awaiting `run.Completion` when output is bounded.
This mirrors production use and avoids accidental backpressure deadlocks.

## Failure Policy

```csharp
var observer = new RecordingObserver();

var run = PipelineBuilder
    .From(new ArraySource<int>(42))
    .Transform(
        new FailingStage<int, string>(),
        new StageFailureOptions
        {
            OnPermanentFailure = FailureAction.Skip,
        })
    .WithObserver(observer)
    .Run();

var outputs = new List<PipelineOutput<string>>();
await foreach (var output in run.Outputs.ReadAllAsync())
    outputs.Add(output);

await run.Completion;

outputs.Should().BeEmpty();
observer.Events.OfType<StageFailedEvent>().Should().ContainSingle();
observer.Events.OfType<PipelineCompletedEvent>().Should().ContainSingle();
```

## Deterministic Time

Use `PipelineRuntimeOptions.Clock` for typed runtime timestamps and retry
scheduling tests.

```csharp
var clock = new ManualPipelineClock(
    new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero));

var run = PipelineBuilder
    .From(new ArraySource<int>(1))
    .Transform(stage)
    .WithRuntimeOptions(new PipelineRuntimeOptions { Clock = clock })
    .Run();
```

## Rules

- Test one runtime behavior per test: output, observer event, retry, timeout,
  dead-letter, drain, or disposal.
- Prefer fake sources and fake clocks to wall-clock sleeps.
- Assert both `run.Outputs` and `run.Completion` when failure can affect either
  channel completion or the run task.
- Keep connector tests outside Core. Core tests should exercise pipeline
  behavior through sources, transformers, sinks, observers, clocks, and options.
