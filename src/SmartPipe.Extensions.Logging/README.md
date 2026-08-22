# SmartPipe.Extensions.Logging

Logging sinks for SmartPipe.Core.

`LoggerSink<T>(ILogger<LoggerSink<T>>)` remains the legacy raw-payload
compatibility path. It keeps the existing Information-level message and
structured `TraceId`/`Value` fields.

Use the additive options constructor for a safe default:

```csharp
var sink = new LoggerSink<Order>(
    logger,
    new LoggerSinkOptions<Order>());
```

The default `LoggerSinkPayloadMode.None` records the trace identifier without
the payload. `Formatted` accepts a caller-owned formatter and truncates its
result to `MaximumFormattedPayloadLength`; the formatter is skipped when
Information logging is disabled. `UnsafeRaw` is an explicit opt-in to the
legacy raw-payload event.
