# AOT And Trimming Compatibility

SmartPipe.Core typed runtime APIs are designed to be explicit and reflection
light. Reflection-based extension helpers are annotated when they are not safe
for trimming or NativeAOT.

Use source-generated JSON metadata as the primary path for JSON file and
dead-letter helpers:

```bash
dotnet add package SmartPipe.Extensions.Json --version 2.1.2
```

```csharp
var source = new JsonFileSource<Order>(
    "orders.ndjson",
    MyJsonContext.Default.Order,
    MyJsonContext.Default.ListOrder,
    new JsonFileSourceOptions { Format = JsonFileFormat.Ndjson });

var jsonSink = new JsonFileSink<Order>(
    "orders.jsonl",
    MyJsonContext.Default.Order,
    MyJsonContext.Default.ListOrder,
    new JsonFileSinkOptions { Format = JsonFileFormat.BatchJsonLines });

var sink = new DeadLetterSink<Order>(
    "dead-letter.jsonl",
    MyJsonContext.Default.DeadLetterEnvelopeOrder);
```

Prefer constructors that accept `JsonTypeInfo<T>` or `JsonTypeInfo<List<T>>`
when publishing trimmed or NativeAOT applications.

For explicit file layouts provide both item and batch metadata. For dead-letter
replay provide `JsonTypeInfo<DeadLetterEnvelope<T>>`; the legacy payload-only
overload remains for compatibility, while the envelope overload is the fully
streaming AOT path. Reflection-disabled consumers set this project property:

```xml
<PropertyGroup>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

| API | Reflection constructor | `JsonTypeInfo` constructor | NativeAOT path |
|---|---|---|---|
| `JsonFileSource<T>` | Annotated warning | Supported | Supported |
| `JsonFileSink<T>` | Annotated warning | Supported | Supported |
| `JsonTransform<TInput,TOutput>` | Annotated warning | Supported | Supported |
| `DeadLetterSource<T>` | Annotated warning | Supported | Supported |
| `DeadLetterSink<T>` | Annotated warning | Supported | Supported |

These five integrations are implemented by `SmartPipe.Extensions.Json`.
`JsonLinesDeadLetterSerializer<T>` remains in `SmartPipe.Core` and also exposes
a source-generated metadata constructor.

The legacy `JsonFileSink<T>` batch-metadata constructors and the default
`BatchJsonLines` format write one JSON array per flushed line. Explicit
`Ndjson` writes one value per line, while `Array` writes one root array. The
path-backed writer rolls back in-process write exceptions on its seekable file
stream, but it does not provide crash-atomic file replacement semantics.

Database helpers have source-safe paths:

- `DbSink<T>` reflection SQL generation is trimming risky. Provide explicit
  INSERT SQL, and use the explicit `DbConnection` ownership overloads for new
  code.
- `DapperSelector<T>` default mapping reflects over writable properties on
  `T`. Prefer the `Func<DbDataReader,T>` mapper overload for NativeAOT and
  trimming-sensitive applications.

The runtime does not add hidden persistence, dynamic plugin loading, or source
materialization for replay.

Channels, reflection-free Transforms rules, and the safe Logging options path are
trim and NativeAOT consumer-tested. `ValidationTransform<T>.TransformAsync` and
`ToFilter` are explicitly `RequiresUnreferencedCode`; use
`RuleValidationTransform<T>` instead when publishing trimmed or NativeAOT code.
