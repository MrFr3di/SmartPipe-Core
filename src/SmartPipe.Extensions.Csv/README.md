# SmartPipe.Extensions.Csv

Strict, bounded CSV file sources and sinks for SmartPipe.Core, backed by CsvHelper 33.1.0.

## Installation

```bash
dotnet add package SmartPipe.Extensions.Csv --version 2.2.0
```

## Strict file profile

`CsvPipelineDefinitionBuilder.FromCsvFile` and `ToCsvFile` use a fixed RFC4180-style profile with a single-character delimiter, quoted multiline fields, explicit character limits, strict decoding, and file-only ownership. Definitions do no I/O; each run receives fresh runtime-owned components and a fresh CsvHelper mapping context. `SkipAndLog` requires an explicit borrowed `ILoggerFactory` and never logs row, header, field, or payload text.

The source bounds logical records, fields, and columns before CsvHelper maps them. Recovery occurs only after a proven record boundary; invalid encoding, header failure, ambiguous quoted EOF, I/O, and cancellation remain terminal. Activation and enumeration cancellation stay linked for the full enumeration lifetime.

## Mapping and compatibility

Use `CsvMapRegistration<T>.Auto`, `From<TMap>()`, or `FromFactory(...)`. A map factory creates a fresh map per activated run; caller-owned mutable maps are not retained.
