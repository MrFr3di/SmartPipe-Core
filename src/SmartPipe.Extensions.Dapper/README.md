# SmartPipe.Extensions.Dapper

Explicit-SQL Dapper sources and sinks for SmartPipe.Core, backed by Dapper.

## Installation

```bash
dotnet add package SmartPipe.Extensions.Dapper --version 2.2.0
```

## Explicit-SQL profile

`DapperPipelineComponents.QuerySource<T>`, `CommandSink<T>`, and `BatchCommandSink<T>`, together with
`DapperPipelineDefinitionBuilder.FromQuery<T>` and the typed `ToCommand`/`ToBatchCommand` extensions,
require explicit SQL. Auto-generated INSERT statements remain a legacy path only, because identifier
quoting, schema, generated columns, and dialect differ between providers.

Composing a component or definition performs no I/O: it does not open a connection, create a command,
begin a transaction, or execute SQL. Each run borrows a `DbDataSource` or a caller-supplied connection
factory, opens a fresh connection, and owns that connection until it is disposed exactly once. The
package exposes no `leaveOpen` option, no external `DbTransaction` injection, and no shared-connection
contract.

The query source streams the first result set through `DbDataReader.ReadAsync` and materializes each
mapped row before advancing. Passing an explicit `Func<DbDataReader, T>` row mapper is the
reflection-free mapping path. Multiple result sets and result buffering are outside this package's
scope.

## Batch and transaction contract

A batch is one preformed bounded `IReadOnlyList<T>` envelope: the sink snapshots the list membership and
its parameter references, then performs one awaited `ExecuteAsync` with that sequence. Dapper or the
provider may execute N commands from the sequence, so this is neither a bulk API nor a single-SQL
guarantee. The package never collects items across envelopes, never buffers a tail, and never executes
SQL from `DisposeAsync`. An empty batch performs no SQL and opens no transaction, and a payload larger
than `MaxBatchItems` is rejected before any SQL is sent.

`DapperBatchTransactionMode` is chosen explicitly per batch sink. `PerBatch` is the recommended choice
when the provider supports it: the sink begins a transaction with the caller token, executes once,
commits, then disposes the transaction and the connection. An execute or commit failure triggers
`RollbackAsync(CancellationToken.None)` and non-cancelable cleanup, and a commit that may have reached
the server keeps an unknown commit outcome — the package never retries it and never promises
exactly-once delivery. A non-null `IsolationLevel` combined with `None` is rejected while composing the
component, before any I/O; a null `IsolationLevel` delegates selection to the provider.

## Logging

The new surface logs operation identity, pipeline key, run identity, outcome, duration, and row or
affected-row counts. SQL text, parameter values, connection strings, payloads, and exception messages
are excluded from default logs.

## Mapping and compatibility

The legacy `DapperSelector<T>` and `DbSink<T>` types remain valid in this leaf assembly under their
original `SmartPipe.Extensions.Selectors` and `SmartPipe.Extensions.Sinks` namespaces, and are exposed
from `SmartPipe.Extensions` through type forwarding, which retains its direct Dapper dependency while
those forwarders exist. That legacy surface keeps its shipped behaviour, including its own logging and
reflection-based SQL generation. New code should reference this package directly and use the
explicit-SQL components.

## Trimming and NativeAOT

Dapper runtime row mapping and parameter binding use reflection and runtime code generation, so the
executable entry points are annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode`. This
package intentionally makes no blanket NativeAOT compatibility claim; heed those diagnostics, or supply
an explicit row mapper and an explicit parameter projector at the call site.
