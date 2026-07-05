# AOT And Trimming Compatibility

SmartPipe.Core typed runtime APIs are designed to be explicit and reflection
light. Reflection-based extension helpers are annotated when they are not safe
for trimming or NativeAOT.

Use source-generated JSON metadata for HTTP, JSON file, and dead-letter helpers:

```csharp
var httpSelector = new HttpSelector<Order>(
    client,
    "https://api.example.test/orders",
    MyJsonContext.Default.ListOrder);

var jsonSink = new JsonFileSink<Order>(
    "orders.jsonl",
    MyJsonContext.Default.ListOrder);

var sink = new DeadLetterSink<Order>(
    "dead-letter.jsonl",
    MyJsonContext.Default.DeadLetterEnvelopeOrder);
```

Prefer constructors that accept `JsonTypeInfo<T>` or `JsonTypeInfo<List<T>>`
when publishing trimmed or NativeAOT applications.

`HttpSelector<T>` reflection JSON constructors are annotated for trimming and
NativeAOT risk. Prefer `JsonTypeInfo<List<T>>` for buffered JSON arrays and
`JsonTypeInfo<T>` with `HttpSelectorStreamingMode` for streaming JSON arrays or
NDJSON.

`JsonFileSink<T>` writes one JSON array per line for each flushed batch. Its
path-backed writer rolls back in-process write exceptions on seekable streams,
but it does not provide crash-atomic file replacement semantics.

Database helpers have source-safe paths:

- `DbSink<T>` reflection SQL generation is trimming risky. Provide explicit
  INSERT SQL, and use the explicit `DbConnection` ownership overloads for new
  code.
- `DapperSelector<T>` default mapping reflects over writable properties on
  `T`. Prefer the `Func<DbDataReader,T>` mapper overload for NativeAOT and
  trimming-sensitive applications.

The runtime does not add hidden persistence, dynamic plugin loading, or source
materialization for replay.
