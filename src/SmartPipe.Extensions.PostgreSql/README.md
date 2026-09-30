# SmartPipe.Extensions.PostgreSql

PostgreSQL-native pipeline components for [SmartPipe.Core](../SmartPipe.Core/README.md), built on
[Npgsql](https://www.npgsql.org/): binary `COPY` streaming in both directions, and `LISTEN`/`NOTIFY` as an asynchronous
notification source.

The package is named after **PostgreSQL** because PostgreSQL capabilities are the product contract. It is implemented on
**Npgsql** because Npgsql is the .NET data provider used to reach those capabilities. The application owns the
`NpgsqlDataSource`; SmartPipe borrows it.

```text
SmartPipe.Extensions.PostgreSql
    ├── binary COPY TO STDOUT   → IPipelineSource<T>
    ├── binary COPY FROM STDIN  → IPipelineSink<IReadOnlyList<T>>
    ├── LISTEN / NOTIFY         → IPipelineSource<PostgreSqlNotification>
    └── NpgsqlDataSource        → application-owned provider boundary
```

Binary COPY sources, binary COPY batch sinks and `LISTEN` notification sources require an
application-owned data source with `Multiplexing=false` (the Npgsql default). All three factories reject
multiplexing when the descriptor is composed, before opening a connection or starting protocol work.
Use a separate non-multiplexing data source for COPY and LISTEN if other application work uses multiplexing.
SmartPipe never changes or disposes the borrowed data source.

In the pinned Npgsql 10.0.3 provider, starting COPY on a multiplexing connection synchronously binds a
physical connector with an infinite timeout and no cancellation token. If the pool is occupied, this
binding can block despite caller cancellation or the configured connection timeout. The composition
check prevents this configuration from reaching that provider path. Npgsql also does not support
`WaitAsync` for LISTEN in multiplexing mode.

## What this package adds beyond Dapper and EF Core

Ordinary `SELECT`/`INSERT`/`UPDATE`/`DELETE` work belongs to `SmartPipe.Extensions.Dapper`; ORM and `IQueryable`
behaviour belongs to `SmartPipe.Extensions.EntityFrameworkCore`. This package exists only for behaviour those generic
integrations cannot express without leaking provider specifics:

| Capability | Why it is provider-native |
|---|---|
| Binary `COPY` out | PostgreSQL's server-side bulk transfer protocol, typed and streamed through `NpgsqlBinaryExporter`. |
| Binary `COPY` in | One COPY operation per batch, completed by `NpgsqlBinaryImporter.CompleteAsync`. |
| `LISTEN` / `NOTIFY` | PostgreSQL's transaction-aware asynchronous signalling, delivered through the Npgsql `Notification` event. |

There is **no dependency arrow** between Dapper, EF Core and this package. They can share one application-owned
`NpgsqlDataSource`, and each run of each component obtains its own connection.

## The canonical boundary: an application-owned `NpgsqlDataSource`

Every factory takes an already-configured `NpgsqlDataSource`. SmartPipe never accepts a connection string and never
constructs, mutates or disposes the data source.

The application stays responsible for host/database selection, credentials, TLS and certificates, GSS, cloud identity
and token refresh, pool sizes, multi-host/failover, load balancing, type mappings and Npgsql plugins.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);
```

The data source is long-lived and thread-safe. SmartPipe treats it as borrowed infrastructure.

## Binary COPY OUT

```csharp
var definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("orders-archive-export"),
        PostgreSqlPipelineComponents.BinaryCopySource<OrderRow>(
            dataSource,
            "COPY (SELECT id, number, total FROM orders_archive ORDER BY id) TO STDOUT (FORMAT BINARY)",
            static async (exporter, columnCount, cancellationToken) =>
                new OrderRow(
                    await exporter.ReadAsync<long>(cancellationToken).ConfigureAwait(false),
                    await exporter.ReadAsync<string>(cancellationToken).ConfigureAwait(false),
                    await exporter.ReadAsync<decimal>(cancellationToken).ConfigureAwait(false)),
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = 3 }))
    .Build();
```

Rules the source guarantees:

- no whole-result materialisation and no detached prefetch queue — backpressure is naturally pull-based;
- `InitializeAsync` opens one connection and starts no COPY; the export begins only when enumeration begins, so a
  downstream activation failure does not start unnecessary server work;
- the row callback is invoked exactly once per PostgreSQL row and must consume or skip every column before returning;
- the callback borrows the exporter only until its returned value task completes: it must not dispose, retain or
  concurrently use the cursor;
- the column count arrives as the callback's `int` argument; `ExpectedColumnCount` is checked before the callback runs;
- one enumeration per activated source instance — repeat or concurrent enumeration is rejected;
- if the consumer stops early, or the callback fails, or the run is cancelled, the exporter is cancelled and then
  disposed, and the original failure stays primary over any cleanup failure.

## Binary COPY IN: one batch envelope is one complete COPY

```csharp
// batchSource is a PipelineComponent<IPipelineSource<IReadOnlyList<OrderBatch>>>.
var definition = PipelineDefinitionBuilder
    .From(new PipelineKey("orders-ingest"), batchSource)
    .ToPostgreSqlBinaryCopy(
        dataSource,
        "COPY orders_archive (id, number, total) FROM STDIN (FORMAT BINARY)",
        static async (importer, row, cancellationToken) =>
        {
            await importer.WriteAsync(row.Id, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.Number, cancellationToken).ConfigureAwait(false);
            await importer.WriteAsync(row.Total, NpgsqlDbType.Numeric, cancellationToken).ConfigureAwait(false);
        },
        new PostgreSqlBinaryCopySinkOptions { MaxRowsPerBatch = 10_000 });
```

The sink accepts only `IReadOnlyList<T>`, and each `WriteAsync` call is one complete COPY operation:

```text
WriteAsync(batch)
    → validate batch
    → BeginBinaryImportAsync
    → StartRowAsync + rowWriter × rows
    → CompleteAsync
    → dispose importer
    → return success
```

Why the envelope is a batch and not one row at a time: the Core sink contract treats a successful `WriteAsync` as the
completion of that sink operation. Keeping a binary importer open across many writes would report success for rows that
PostgreSQL had not accepted yet, and would only discover failure during disposal. Therefore:

- a successful `WriteAsync` means `CompleteAsync` already succeeded;
- an empty batch is a successful no-op;
- a batch larger than `MaxRowsPerBatch` is rejected before any server operation starts;
- a cancelled or failed import is aborted by importer disposal, and PostgreSQL reverts that COPY;
- an earlier completed batch stays committed if a later batch fails;
- `DisposeAsync` performs cleanup only — it never performs a business write;
- there is **no automatic retry**.

### Ambiguous completion is not guessed

A connection or network failure around completion can leave the caller unable to prove whether the server committed
immediately before transport loss. This package never claims "definitely committed", "definitely rolled back" or
"safe to replay". Retrying a COPY write is an application decision that requires an explicit idempotency strategy.

### Generic batching is not part of this package

Applications supply already-formed batches. A generic batching or windowing runtime has to answer tail flush,
completion output, cancellation, bounded memory, multi-output semantics and failure handling first; that problem is not
hidden inside a PostgreSQL integration.

## LISTEN / NOTIFY

```csharp
var definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("orders-signalled"),
        PostgreSqlPipelineComponents.NotificationSource(
            dataSource,
            ["orders_changed"],
            new PostgreSqlNotificationSourceOptions { BufferCapacity = 256 }))
    .Build();

await foreach (var envelope in run.Outputs)
{
    var notification = envelope.Payload;
    Console.WriteLine($"{notification.Channel} from backend {notification.BackendProcessId}");
}
```

`InitializeAsync` returns only after the registration transaction has committed, because `LISTEN` takes effect at
commit. Channel identifiers are quoted through Npgsql's public identifier-quoting API before they reach `LISTEN`.

### NOTIFY is not a durable queue

`NOTIFY` provides none of the following, and this package does not pretend otherwise:

- durable storage, replay or a log;
- exactly-once delivery;
- at-least-once delivery across a disconnect;
- a guaranteed one-to-one mapping from `NOTIFY` statements to deliveries;
- a substitute for Kafka or a transactional outbox.

Relevant PostgreSQL behaviour that shapes the contract:

- `NOTIFY` inside a transaction is delivered only after that transaction commits;
- identical channel and payload pairs in one transaction may be folded into a single delivery;
- notifications from different committed transactions are delivered in commit order;
- `LISTEN` is effective at commit;
- when a session terminates, its `LISTEN` registrations disappear.

There is **no automatic reconnect**. If the connection is lost the source faults, and notifications issued during the
gap cannot be recovered. Restarting the pipeline is an application decision with the same missed-notification
semantics.

### Durable outbox plus NOTIFY wake-up

```text
durable table / outbox = truth
NOTIFY                 = wake-up signal
```

Store the real payload in a table and put a small key, version or wake-up token in the notification:

```csharp
// Producer, inside the same transaction as the business change.
await using var command = dataSource.CreateCommand(
    "INSERT INTO outbox (id, payload) VALUES ($1, $2); NOTIFY orders_changed, 'outbox';");
```

The package never parses the payload. It performs no JSON deserialization, no event-type inference, no schema
validation and no logging of the raw payload.

### Startup race

PostgreSQL documents a race between establishing `LISTEN` and notifications that commit concurrently. The recommended
application pattern is:

```text
1. the notification source reports Ready (LISTEN committed)
2. query the durable application state with Dapper or EF Core
3. process that snapshot
4. rely on subsequent NOTIFY calls to wake the application for later checks
```

A notification may overlap state already observed by the snapshot. Where that matters, consumers must be idempotent at
the domain level. SmartPipe cannot perform the domain snapshot for you, so there is no generic snapshot callback.

### Bounded bridge and overflow

Npgsql delivers notifications through a synchronous event callback, so callback backpressure cannot be awaited. The
package therefore bridges the callback into one internal bounded channel and applies explicit backpressure rules:

```text
Npgsql Notification event
    → copy Channel / Payload / PID into an immutable record
    → TryWrite
    → bounded channel (Capacity = BufferCapacity)
    → source async enumerator
    → pipeline
```

The callback never blocks, never calls `.Result` or `.Wait()`, never runs user asynchronous code and never waits for
pipeline capacity. A full bridge is an explicit fault: the rejected notification was not accepted and may be lost, and
the source faults rather than pretending the delivery was lossless. Overflow, caller cancellation and provider faults
compete through one immutable first-cause slot, and the first recorded cause wins.

## Ownership and lifetime

| Object | Owner | Lifetime |
|---|---|---|
| `NpgsqlDataSource` | application | long-lived, shared, never disposed by SmartPipe |
| `NpgsqlConnection` | SmartPipe runtime | one per component per run, disposed exactly once |
| `NpgsqlBinaryExporter` / `NpgsqlBinaryImporter` | SmartPipe runtime | one per enumeration or per batch |
| `ILoggerFactory` passed to a factory | application | borrowed, never disposed by SmartPipe |

Components accept no caller-owned connection or transaction. Creating a descriptor or definition and calling `Build()`
performs no database connection, no COPY, no `LISTEN`, no SQL, no task and no timer — only argument validation and
immutable snapshot construction.

### Ambient transactions are rejected

Npgsql enlists opened connections automatically when `System.Transactions.Transaction.Current` is set. That would make
"one batch envelope equals one completed COPY" untrue and would silently couple pipeline work to a caller's
transaction. Every component therefore throws `InvalidOperationException` when an ambient transaction is present:
once before opening its connection, and again immediately before starting COPY or `LISTEN` protocol work. Components
accept no caller-owned transaction, do not participate in ambient transactions, and do not require `Enlist=false`.

## Connection cost

```text
active COPY source ≈ one leased Npgsql connection
active COPY sink   ≈ one leased Npgsql connection
active LISTEN      ≈ one dedicated leased connection for the run lifetime
```

Size the pool accordingly. Prefer one listener for several channels over one listener per channel.

## Security responsibility

This package does not create or configure connection strings, SSL mode, CA or client certificates, GSS, passwords,
authentication tokens, cloud identity or secret rotation. The application configures the `NpgsqlDataSource`, and
credential refresh strategies such as Npgsql's periodic password or token providers stay outside the pipeline
lifecycle.

Transport encryption without server identity verification is **not** authenticated TLS. Production deployments must
select the Npgsql SSL verification settings appropriate to their environment. Test fixtures in this repository use
disposable local servers with trust authentication and disabled durability; that configuration is never a production
recommendation.

## Logging

The package depends on `Microsoft.Extensions.Logging.Abstractions`. Factories accept an optional borrowed
`ILoggerFactory`; SmartPipe never disposes it and never resolves services from a locator.

The package never logs connection strings, credentials, COPY SQL text, notification payloads, row payloads or
parameter values, and it performs no per-row logging. Permitted structured fields are the operation name, the operation
kind, row counts, notification buffer capacity or count category, the failure category, and pipeline/run identity where
Core already exposes it.

## OpenTelemetry

Telemetry layers stay separate:

```text
SmartPipe.Extensions.OpenTelemetry  → pipeline runtime instrumentation
Npgsql / Npgsql.OpenTelemetry       → database client instrumentation
```

This package has no production dependency on `SmartPipe.Extensions.OpenTelemetry` or `Npgsql.OpenTelemetry`, and it
creates no database client spans of its own, so COPY and connection activity is traced exactly once by Npgsql.

## NativeAOT and trimming

The package declares the positive `IsAotCompatible`, trim-analyzer and AOT-analyzer contract for the documented
slim/static primitive path:

```csharp
var builder = new NpgsqlSlimDataSourceBuilder(connectionString);
var dataSource = builder.Build();
```

- built-in primitive mappings (`int`, `text`, `numeric`, `date`, `time`, `timestamp`, `timestamptz`, `uuid`, `jsonb` as
  `string`) work without opt-in;
- arrays, JSON document types and POCO mappings need explicit Npgsql opt-ins;
- the canonical AOT consumer uses static COPY callbacks only — no dynamic JSON POCO mapping, no unmapped dynamic types
  and no dynamic composite mapping;
- transport security is **off by default** in the slim builder; call `EnableTransportSecurity()` when the deployment
  requires TLS.

If an application opts into APIs annotated with `RequiresUnreferencedCode` or `RequiresDynamicCode` — for example
`EnableDynamicJson`, `EnableRecordsAsTuples`, `EnableUnmappedTypes`, `MapComposite` or `MapEnum(Type, …)` — those
warnings remain the caller's. SmartPipe never suppresses them globally.

## Composition

### Dependency injection

The production package has no DI dependency. The application builds the data source and the definition, then registers
the definition:

```csharp
var dataSource = BuildApplicationDataSource();

var definition = PipelineDefinitionBuilder
    .From(
        new PipelineKey("export"),
        PostgreSqlPipelineComponents.BinaryCopySource<OrderRow>(
            dataSource,
            copyToCommand,
            rowReader,
            new PostgreSqlBinaryCopySourceOptions()));

services.AddSmartPipe().AddPipeline(definition);
```

The definition captures the data source explicitly. The package never resolves `NpgsqlDataSource` from
`PipelineActivationContext.Services`.

### Dapper

`NpgsqlDataSource` derives from the generic `DbDataSource` boundary, so one application pool serves both packages:

```text
application-owned NpgsqlDataSource
    ├── SmartPipe.Extensions.Dapper         generic SQL
    └── SmartPipe.Extensions.PostgreSql     COPY / LISTEN
```

Neither package disposes the data source, each run obtains its own connection, and there is no production package
dependency between them.

### EF Core

`SmartPipe.Extensions.EntityFrameworkCore` stays provider-neutral. An application supplies
`Npgsql.EntityFrameworkCore.PostgreSQL` itself and can run EF queries alongside COPY and LISTEN components; the
runtime-owned `DbContext` and the runtime-owned COPY/LISTEN connections have independent lifetimes, and there is no
production dependency in either direction.

### Resilience

There is no built-in PostgreSQL retry. Application resilience policies may retry acquiring a connection; retrying a
COPY after an uncertain completion is not automatically safe; restarting a listener does not recover missed
notifications.

### Health checks

Database health is application and provider infrastructure. `SmartPipe.Extensions.HealthChecks` covers pipeline run
health, not database reachability, and this package adds no health-check APIs.

## Type handling

Npgsql remains the owner of PostgreSQL type mapping: arrays, ranges, multiranges, `json`/`jsonb`, network types, enums,
composites, full-text types and PostGIS/NodaTime integrations. Copy callbacks call Npgsql's typed APIs directly. There
is no parallel SmartPipe type system, and the package does not normalize Npgsql 10's modern date/time behaviour (for
example `DateOnly` and `TimeOnly`) back to historical provider behaviour.

One provider detail is worth knowing before writing a row reader: reading a `timestamp with time zone` column as
`DateTime` through binary COPY reports `DateTimeKind.Unspecified` rather than `Utc`. That is the provider's mapping, and
SmartPipe passes it through unchanged instead of rewriting the value's kind. Read `DateTimeOffset`, or assert the
instant and "not local" rather than the `Kind`, when the distinction matters.

## Non-goals

Explicitly out of scope for this package:

- ordinary SQL wrappers or a second Dapper integration;
- an EF Core provider wrapper or an EF/Npgsql-EF production dependency;
- connection strings, data-source construction, pool management, provider failover or load-balancer abstraction;
- TLS, GSS, authentication-token or secret-rotation wrappers;
- built-in retries or a Polly dependency;
- database health-check APIs;
- text COPY, CSV COPY and raw backup/restore COPY;
- a `NOTIFY` sink — emitting a notification atomically with a business change requires the application's own
  transaction, so it stays application SQL, Dapper or EF responsibility;
- automatic `LISTEN` reconnect;
- logical or physical replication, replication-slot management and LSN acknowledgement;
- advisory or distributed locks;
- schema migrations, pgvector, PostGIS, NodaTime, JSONB-serializer and enum/composite wrappers;
- large objects, exactly-once semantics and a cross-component transaction coordinator;
- a generic batch or windowing runtime.

Logical replication and CDC are excluded on semantic grounds, not on effort: acknowledging a WAL position correctly
requires a downstream settlement contract — a source can only be acknowledged after downstream success, with explicit
negative settlement, checkpoint persistence and recovery semantics. Core does not yet have that contract, and
acknowledging early risks data loss while never acknowledging grows the replication slot.

## Requirements

- .NET 10 (`net10.0`)
- `SmartPipe.Core` 2.2.0
- Npgsql 10.0.3
- PostgreSQL 18.6 (primary CI baseline) and 17.11 (compatibility baseline)

## Documentation

- [SmartPipe 2.2 integration packages](../../docs/migration/2.2.0-integration-packages.md)
- [PostgreSQL subsystem reference](../../docs/postgresql.md)
- [AOT compatibility](../../docs/aot-compatibility.md)
- [Extension architecture plan](../../docs/plans/2.2.0-extension-architecture.md)
- [Package authoring](../../docs/contributing/package-authoring.md)
