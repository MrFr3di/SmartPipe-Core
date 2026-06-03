# Bounded Output

Use bounded output when a caller reads `PipelineRun<T>.Outputs` and wants the
output stream to apply backpressure. Leave output unbounded for sink-only worker
pipelines.

## Consume Outputs While The Run Is Active

When `OutputCapacity` is set and `OutputFullMode` is `Wait`, the runtime writes
to `PipelineRun<T>.Outputs` before it writes to an attached sink. If nobody
reads the output stream, the pipeline can block before the sink receives the
item.

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        OutputCapacity = 256,
        OutputFullMode = BoundedChannelFullMode.Wait,
    })
    .To(sink);

var consumeOutputs = Task.Run(async () =>
{
    await foreach (var output in run.Outputs.ReadAllAsync())
    {
        if (!output.Result)
        {
            LogFailure(output.Result.Error);
            continue;
        }

        Use(output.Envelope);
    }
});

await run.Completion;
await consumeOutputs;
```

## Sink-Only Workers

For a pipeline that only needs the sink side effect, keep the default:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .To(sink);

await run.Completion;
```

The default `OutputCapacity = null` uses an unbounded output channel and
preserves existing behavior.

## Rules

- Use `OutputCapacity` only when something reads `PipelineRun<T>.Outputs`.
- Prefer `OutputFullMode.Wait` when losing outputs is not acceptable.
- Do not configure bounded output just to limit sink concurrency; sink work is
  controlled by the pipeline and stage design, not by ignoring outputs.
- Core does not contain connectors. Connector-specific backpressure belongs in
  application code, SmartPipe.Extensions, or future extension packages.
