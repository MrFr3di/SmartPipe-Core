# Bounded Output

Use bounded output when a caller reads `PipelineRun<T>.Outputs` and wants the
output stream to apply backpressure. The typed runtime uses automatic output
capacity when `OutputCapacity = null`: the nullable public option is preserved
for compatibility, and the runtime uses the bounded default capacity for typed
runs with or without a sink.

The output channel is single-reader by contract. Callers that need fan-out
must implement it explicitly (for example by reading outputs and re-publishing
through their own dispatcher).

Default `OutputPolicy` is `SuppressSuccessWhenSinkAttached`.

This is the safe default for sink-backed pipelines because successful outputs are not written to `PipelineRun<T>.Outputs` unless the caller explicitly opts into `EmitAll`.

Use `EmitAll` only when the caller actively consumes `PipelineRun<T>.Outputs`.

## Consume Outputs While The Run Is Active

When output emission is active and `OutputFullMode` is `Wait`, the runtime
writes to an attached sink before it publishes a success output. If nobody
reads the output stream, the pipeline can block after a sink write succeeds and
before the next item reaches the sink.

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

## Output Consumer Pipeline

For a pipeline where the caller owns the output stream, keep automatic capacity
or configure a capacity explicitly:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .Run();

await foreach (var output in run.Outputs.ReadAllAsync())
{
    if (output.Result)
        Use(output.Envelope);
}

await run.Completion;
```

In automatic mode, this output-only run uses bounded output backpressure.

## Sink-Only Pipeline

For a pipeline that only needs the sink side effect, suppress unread success
outputs:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
    })
    .To(sink);

await run.Completion;
```

The default `OutputCapacity = null` still uses bounded output capacity. The
sink-only path stays non-blocking by suppressing unread success outputs.

If a sink-attached pipeline needs failure outputs while ignoring success
outputs, choose an output policy that matches how much the caller actually
reads:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        OutputCapacity = 256,
        OutputFullMode = BoundedChannelFullMode.Wait,
        OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
    })
    .To(sink);

await run.Completion;
```

This keeps success outputs from blocking later sink writes while preserving
terminal failure outputs for a reader if one is attached.

## Slow Output Reader

`PipelineOutputPolicy.EmitAll` intentionally preserves output backpressure. If
the output reader is slow and `OutputFullMode = Wait`, the run can wait after a
sink write succeeds and before processing the next item:

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(parseStage)
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        OutputCapacity = 16,
        OutputFullMode = BoundedChannelFullMode.Wait,
        OutputPolicy = PipelineOutputPolicy.EmitAll,
    })
    .To(sink);

await foreach (var output in run.Outputs.ReadAllAsync())
{
    await SlowStoreAsync(output);
}

await run.Completion;
```

Use this mode when output retention/backpressure is intentional. For a
side-effect-only sink pipeline, prefer
`SuppressSuccessWhenSinkAttached`.

If a lossy `OutputFullMode` is selected, dropped output results increment
`smartpipe.output.items.dropped` and emit a best-effort `OutputDroppedEvent`.
Use `Wait` when output loss is not acceptable.

## Rules

- With sink-attached runs, either read `PipelineRun<T>.Outputs` or configure an
  output policy that suppresses enough results for the workload.
- For sink-backed pipelines, `PipelineResult.Success` means the sink write has
  completed successfully.
- Prefer `OutputFullMode.Wait` when losing outputs is not acceptable.
- Do not configure bounded output just to limit sink concurrency; sink work is
  controlled by the pipeline and stage design, not by ignoring outputs.
- Core does not contain connectors. Connector-specific backpressure belongs in
  application code, SmartPipe.Extensions, or future extension packages.
