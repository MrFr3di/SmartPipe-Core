# Graceful shutdown

Use the typed `PipelineRun<T>` lifecycle methods to choose how much work the
runtime should finish before stopping.

## Graceful drain

`DrainAsync` is the graceful path. It stops accepting new source items at the
next source boundary, waits for already accepted work, and throws
`TimeoutException` if the supplied timeout elapses.

```csharp
var run = PipelineBuilder
    .From(source)
    .Transform(stage)
    .To(sink);

try
{
    await run.DrainAsync(TimeSpan.FromSeconds(30), shutdownToken);
    await run.Completion;
}
catch (TimeoutException)
{
    await run.CancelAsync();
}
```

Drain cancels the source-read token. Cooperative sources blocked inside
`MoveNextAsync` can stop promptly; non-cooperative awaits may still need to
return on their own. Use `CancelAsync` or `AbortAsync` when shutdown must
interrupt stages, sinks, processing, or output readers too.

## Cooperative cancellation

`CancelAsync` requests cooperative cancellation. The run state becomes
`Cancelled`, the linked runtime token is cancelled, and the output channel is
completed with `OperationCanceledException`.

```csharp
await run.CancelAsync();

try
{
    await run.Completion;
}
catch (OperationCanceledException)
{
    // Expected during cooperative shutdown.
}
```

## Immediate abort

`AbortAsync` is the fastest stop path. It marks the run as `Aborted` and
completes outputs with `OperationCanceledException`. Use it when graceful drain
or cooperative cancellation is not appropriate.

```csharp
await run.AbortAsync();
```

## Dispose

`DisposeAsync` is idempotent. Disposing a running typed run cancels the runtime
and disposes runtime-owned source, transformer, sink, and observer components
once. Prefer an explicit drain or cancel first when the caller needs to observe
the shutdown reason.
