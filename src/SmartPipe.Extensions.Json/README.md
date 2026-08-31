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
- `JsonPipelineComponents`, `JsonPipelineDefinitionBuilder`, and
  `JsonPipelineDefinitionBuilderExtensions`

The related `JsonLinesDeadLetterSerializer<T>` remains part of
`SmartPipe.Core`.

## JSON backend

The package uses `System.Text.Json` from the .NET 10 shared framework, so an
additional `System.Text.Json` NuGet dependency is neither required nor pinned.
Newtonsoft.Json is not a dependency and is not selected at runtime.

Line-framed input uses the internal, bounded UTF-8 reader linked from
`src/Shared/JsonFraming/Utf8LineRecordReader.cs`. The reader is BCL-only and
knows only about LF/CRLF boundaries, BOM bytes, and the configured record-size
limit. JSON validation, path diagnostics, and invalid-record policy remain in
this package; the framer is not a public API or a separate package.

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

Reads are strict by default. `MaxDepth` defaults to 64 for reflection and
source-generated paths. Explicitly line-framed records (`Ndjson`,
`BatchJsonLines`) have a separate 16 MiB per-record limit
(`MaxRecordSizeBytes`); root arrays and auto-detected legacy top-level value
sequences use a 256 MiB unframed input limit (`MaxUnframedInputSizeBytes`).
`SkipAndLog` requires a logger and is supported only when the source is reading
independently line-framed records. `JsonFileSource<T>` defaults to `Auto` and
also accepts explicit `Ndjson` or `BatchJsonLines`; dead-letter `Auto` recovery
depends on whether it detects a framed stream rather than a root array.

Append mode preserves existing bytes. If a non-empty destination has no final
LF, the sink inserts one before the next record; an existing partial row is not
rewritten, and its later replay behavior is determined by the source format and
invalid-record policy.

Injected append streams must be readable, seekable, and writable and are
validated during initialization before any record is written. Boundary checks
restore the incoming position, then writes append at the stream end. Success
leaves the position at the new end; successful rollback restores the old end.
Injected streams remain open on sink disposal, while path-owned streams are
disposed.

`DeadLetterSink<T>` serializes through Core's `IDeadLetterSerializer<T>`.
Append destinations must also be writable. Failed writes roll back to
the pre-record checkpoint; if rollback itself fails, the error reports that the
destination may contain a partial record.

Concurrent or reentrant disposal calls share the same asynchronous completion,
so the underlying stream is finalized once and every caller observes the same
result.

## Migration from SmartPipe.Extensions

The public namespaces remain unchanged. `SmartPipe.Extensions` 2.1.2 retains
type forwarders and a transitive dependency on this package so existing 2.x
source and binary consumers continue to resolve the moved types. New
applications should reference `SmartPipe.Extensions.Json` directly.

The forwarding dependency is the supported compatibility contract for the 2.x
line; no bridge-removal release is promised.
