# AOT And Trimming Compatibility

SmartPipe.Core typed runtime APIs are designed to be explicit and reflection
light. Reflection-based extension helpers are annotated when they are not safe
for trimming or NativeAOT.

`SmartPipe.Extensions.Csv` deliberately has no blanket NativeAOT claim.
CsvHelper object mapping builds expression trees and compiled delegates at
runtime, so strict CSV mapping entry points expose the applicable trimming and
dynamic-code diagnostics. No hidden reflection fallback or blanket warning
suppression is provided.

`SmartPipe.Extensions.Mapster` makes no blanket trimming or NativeAOT claim either. Its runtime mapping
entry points — `MapsterPipelineComponents.Transform<TInput,TOutput>` and both `MapWithMapster` builder
overloads — carry `RequiresUnreferencedCode` and `RequiresDynamicCode`, because Mapster builds expression
trees and compiles them at runtime. Two measured facts bound the claim: a trimmed publish reports the
aggregate `IL2104` for the `Mapster` and `Mapster.Core` assemblies, and executing composition under
`TrimMode=link` fails inside `Mapster.TypeAdapterConfig.GetMapFunction`. The trimming-safe route is a
hand-written or source-generated mapper passed to `PipelineTransformer.FromFunc`; the
`mapster-trim-diagnostic` consumer publishes and runs that route under `TrimMode=link`. The forwarded
legacy `MapsterTransform<TInput,TOutput>` keeps its shipped annotations.

`SmartPipe.Extensions.Http` declares the `transport-full` contract: the transport is trimming- and
NativeAOT-compatible, proven by the `http-trim` and `http-nativeaot` consumers that publish and run a
direct-client source and a factory-client sink. Application request factories, response readers, and
handlers stay outside the claim. `SmartPipe.Extensions.Http.Json` declares `full-json-type-info`: its
array and NDJSON readers and JSON request content accept only source-generated `JsonTypeInfo<T>`, and the
`http-json-trim` and `http-json-nativeaot` consumers publish and run that path with reflection
serialization disabled.

`SmartPipe.Extensions.Polly` declares the `verified` contract for the decorator over `Polly.Core`: the
`polly-trim` and `polly-nativeaot` consumers publish and run a Core pipeline whose stage is decorated with
a Polly retry over an owned inner transform, plus a direct decorator with a borrowed inner transform and
an exception mapper. Application strategies, callbacks, Polly registry packages, and other dynamic code
stay outside the claim.

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

For canonical typed definitions, use `JsonPipelineDefinitionBuilder.FromJsonFile`
or `FromJsonDeadLetterFile`, then `TransformJson` and `ToJsonFile` from
`SmartPipe.Extensions.Json`. Those adapters require source-generated metadata,
snapshot it into private resolver-backed options, and keep component activation
and logger creation lazy and trimming-safe.

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
- `SmartPipe.Extensions.Dapper` annotates its explicit-SQL entry points with
  `RequiresUnreferencedCode` and `RequiresDynamicCode`, because Dapper row
  mapping and parameter binding use reflection and runtime code generation.
  Supplying an explicit `Func<DbDataReader,T>` row mapper removes the
  application's own mapping reflection, but the package makes no blanket
  NativeAOT claim.

The runtime does not add hidden persistence, dynamic plugin loading, or source
materialization for replay.

Channels, reflection-free Transforms rules, and the safe Logging options path are
trim and NativeAOT consumer-tested. `ValidationTransform<T>.TransformAsync` and
`ToFilter` are explicitly `RequiresUnreferencedCode`; use
`RuleValidationTransform<T>` instead when publishing trimmed or NativeAOT code.

`SmartPipe.Extensions.EntityFrameworkCore` makes no blanket trimming or NativeAOT claim: query shape,
provider, compiled models, and generated query delegates are evaluated by the consumer. The forwarded
legacy `EfCoreSelector<T>` resolves its entity set through `DbContext.Set<T>()`, which is
trimming-unsafe; its narrow internal suppression documents that boundary, and the factory-based
`EfCorePipelineComponents` sources are the supported alternative.
