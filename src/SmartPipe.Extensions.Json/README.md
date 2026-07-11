# SmartPipe.Extensions.Json

System.Text.Json-based sources, sinks, transforms, and dead-letter persistence
for SmartPipe.Core.

## Installation

```bash
dotnet add package SmartPipe.Extensions.Json --version 2.1.2
```

## Package graph

`SmartPipe.Extensions.Json` depends on `SmartPipe.Core` and
`Microsoft.Extensions.Logging.Abstractions`. It does not bring in Dapper,
Entity Framework Core, Mapster, CsvHelper, HTTP resilience, hosting,
health-check, or Newtonsoft.Json dependencies.

## Components

- `JsonFileSource<T>`
- `JsonFileSink<T>`
- `JsonTransform<TInput,TOutput>`
- `DeadLetterSource<T>`
- `DeadLetterSink<T>`

The related `JsonLinesDeadLetterSerializer<T>` remains part of
`SmartPipe.Core`.

## JSON backend

The package uses `System.Text.Json` from the .NET 10 shared framework, so an
additional `System.Text.Json` NuGet dependency is neither required nor pinned.
Newtonsoft.Json is intentionally not a dependency and is not selected at runtime.

## Trimming and NativeAOT

Reflection-based constructors are annotated for trimming and NativeAOT risk.
For trimmed or NativeAOT applications, use the overloads that accept
source-generated `JsonTypeInfo<T>` metadata.

## File formats

Options-based sources and sinks select `Array`, `Ndjson`, or
`BatchJsonLines`. Sources can also use `Auto`; collection-valued payloads must
choose an explicit format because a leading array is ambiguous. Existing sink
constructors preserve the SmartPipe 2.1.1 default: append one array per flushed
line.

Reads are strict by default. `MaxDepth` defaults to 64 and independently framed
records default to a 16 MiB encoded limit. `SkipAndLog` is available only when
a logger is supplied, so invalid records cannot disappear silently.

`DeadLetterSink<T>` serializes through Core's `IDeadLetterSerializer<T>`.
Seekable destinations roll back failed writes; a possibly partial
non-seekable write is not retried.

## Migration from SmartPipe.Extensions

The public namespaces remain unchanged. `SmartPipe.Extensions` 2.1.2 retains
type forwarders and a transitive dependency on this package so existing 2.x
source and binary consumers continue to resolve the moved types. New
applications should reference `SmartPipe.Extensions.Json` directly.

The forwarding dependency is the supported compatibility contract for the 2.x
line; no bridge-removal release is promised.
