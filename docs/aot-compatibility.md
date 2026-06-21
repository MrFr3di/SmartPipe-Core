# AOT And Trimming Compatibility

SmartPipe.Core typed runtime APIs are designed to be explicit and reflection
light. Reflection-based extension helpers are annotated when they are not safe
for trimming or NativeAOT.

Use source-generated JSON metadata for JSON file and dead-letter helpers:

```csharp
var sink = new DeadLetterSink<Order>(
    "dead-letter.jsonl",
    MyJsonContext.Default.DeadLetterEnvelopeOrder);
```

Prefer constructors that accept `JsonTypeInfo<T>` or `JsonTypeInfo<List<T>>`
when publishing trimmed or NativeAOT applications.

The runtime does not add hidden persistence, dynamic plugin loading, or source
materialization for replay.
