# SmartPipe.Extensions.Csv

Strict, bounded CSV file sources and sinks for SmartPipe.Core, backed by CsvHelper 33.1.0.

## Installation

```bash
dotnet add package SmartPipe.Extensions.Csv --version 2.2.0
```

## Strict file profile

`CsvPipelineDefinitionBuilder.FromCsvFile` and `ToCsvFile` use a fixed RFC4180-style profile with a single-character delimiter, quoted multiline fields, explicit character limits, strict decoding, and file-only ownership. Definitions do no I/O; each run receives fresh runtime-owned components and a fresh CsvHelper mapping context. `SkipAndLog` requires an explicit borrowed `ILoggerFactory` and never logs row, header, field, or payload text.

The source bounds logical records, fields, and columns before CsvHelper maps them. Recovery occurs only after a proven record boundary; invalid encoding, header failure, ambiguous quoted EOF, I/O, and cancellation remain terminal. Activation and enumeration cancellation stay linked for the full enumeration lifetime.

The strict file component owns its internal CsvReader/CsvWriter wrapper, bounded bridge or record writer, and file reader/stream. Those wrappers are kept open for component cleanup, which releases each owned resource once (using atomic exchange where cleanup paths can converge), attempts every release, and keeps a primary operation failure ahead of cleanup failures. This is an internal lifecycle guarantee; the public API exposes no stream or `leaveOpen` option.

The sink stages one character-bounded record, supports create or append, validates append encoding and header semantics, preserves an existing BOM exactly once (including BOM-only files), and rolls a failed record back to its file checkpoint. It does not promise whole-file atomicity or concurrent same-file writers.

## Mapping and compatibility

Use `CsvMapRegistration<T>.Auto`, `From<TMap>()`, or `FromFactory(...)`. A map factory creates a fresh map per activated run; caller-owned mutable maps are not retained.

The legacy `CsvFileSource<T>`, `CsvFileSink<T>`, and `CsvTransform<TInput,TOutput>` types remain valid in this leaf assembly and are exposed from `SmartPipe.Extensions` through type forwarding, which retains its direct CsvHelper dependency while those forwarders exist. New code should reference this package directly.

## Trimming and NativeAOT

CsvHelper object mapping uses reflection, expression trees, and compiled delegates. This package intentionally does not advertise blanket NativeAOT compatibility; heed the RUC/RDC diagnostics on executable mapping entry points.
