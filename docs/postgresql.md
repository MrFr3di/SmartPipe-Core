# PostgreSQL

`SmartPipe.Extensions.PostgreSql` adds the three PostgreSQL-native capabilities that a
generic provider integration cannot express without leaking provider specifics: binary
`COPY` in both directions, and `LISTEN`/`NOTIFY` as an asynchronous notification source.
Ordinary SQL stays with `SmartPipe.Extensions.Dapper`; ORM and `IQueryable` behaviour stays
with `SmartPipe.Extensions.EntityFrameworkCore`.

```bash
dotnet add package SmartPipe.Extensions.PostgreSql
```

```text
SmartPipe.Extensions.PostgreSql
    ├── binary COPY TO STDOUT   → IPipelineSource<T>
    ├── binary COPY FROM STDIN  → IPipelineSink<IReadOnlyList<T>>
    ├── LISTEN / NOTIFY         → IPipelineSource<PostgreSqlNotification>
    └── NpgsqlDataSource        → application-owned provider boundary
```

| Capability | Pipeline shape | Entry points |
|---|---|---|
| Binary `COPY … TO STDOUT (FORMAT BINARY)` | streaming source, one row per envelope | `PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<T>`, `PostgreSqlPipelineComponents.BinaryCopySource<T>` |
| Binary `COPY … FROM STDIN (FORMAT BINARY)` | batch sink, one batch envelope per complete COPY | `PostgreSqlPipelineDefinitionBuilderExtensions.ToPostgreSqlBinaryCopy`, `PostgreSqlPipelineComponents.BinaryCopyBatchSink<T>` |
| `LISTEN` / `NOTIFY` | notification source | `PostgreSqlPipelineDefinitionBuilder.FromNotifications`, `PostgreSqlPipelineComponents.NotificationSource` |

`PostgreSqlNotification` is `sealed record PostgreSqlNotification(string Channel, string
Payload, int BackendProcessId)`. PostgreSQL and Npgsql supply no database-side event
timestamp, so none is invented: local receipt time belongs to pipeline telemetry. The
payload is opaque and is never parsed, validated or logged by the package.

The full package walk-through, including the `NpgsqlSlimDataSourceBuilder` setup for
NativeAOT, lives in the [package README](../src/SmartPipe.Extensions.PostgreSql/README.md).

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

## Provider boundary

Every factory takes an already-configured, application-owned `NpgsqlDataSource`. SmartPipe
never accepts a connection string and never constructs, configures, mutates or disposes the
data source. Host and database selection, credentials, TLS and certificates, GSS, cloud
identity and token refresh, pool sizing, multi-host/failover, load balancing, type mappings
and Npgsql plugins remain the application's responsibility.

```csharp
await using var dataSource = NpgsqlDataSource.Create(connectionString);
```

The data source is long-lived and thread-safe; SmartPipe treats it as borrowed
infrastructure. Components accept no caller-owned connection or transaction. Creating a
descriptor or definition and calling `Build()` performs only argument validation and
immutable option snapshotting — no connection, no COPY, no `LISTEN`, no SQL, no task, no
timer. Each run obtains its own connection.

`CopyCommand` is the complete configuration statement sent to PostgreSQL; Npgsql appends
nothing to it. Pass the `SELECT` or `INSERT` you mean, for example
`"COPY (SELECT id, number, total FROM orders_archive ORDER BY id) TO STDOUT (FORMAT BINARY)"`
or `"COPY orders_archive (id, number, total) FROM STDIN (FORMAT BINARY)"`.

Options are validated and snapshotted when the factory runs:

| Option | Rule |
|---|---|
| `OperationName` | Non-blank, at most 64 characters; defaults to `postgresql-copy-out`, `postgresql-copy-in` or `postgresql-listen` |
| `ExpectedColumnCount` (COPY out) | Optional; when set, greater than zero, and a mismatch is reported before the row callback runs |
| `MaxRowsPerBatch` (COPY in) | Greater than zero; bounds one COPY operation, not the memory the caller already used to build the batch |
| `BufferCapacity` (LISTEN) | Greater than zero; capacity of the bounded bridge between the Npgsql callback and the pipeline |

Channel identifiers are copied defensively, validated for ordinal duplicates and never
trimmed or case-folded, because PostgreSQL owns identifier interpretation.

## Binary COPY out

The row reader receives the exporter and the row's column count, and produces one `T` per
PostgreSQL row:

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

- the source is pull-based: no whole-result materialisation and no detached prefetch queue;
- a row callback is invoked once per PostgreSQL row and must consume or skip every column
  before returning;
- the callback borrows the exporter only until its returned value task completes; it must
  not dispose, retain or concurrently use the cursor;
- `NpgsqlBinaryExporter.StartRowAsync` returns the row's column count and `-1` at the end of
  data, which is what ends the enumeration;
- one enumeration per activated source instance; repeat or concurrent enumeration is
  rejected;
- early exit, a callback failure or cancellation cancels the exporter and then disposes it,
  and the primary failure stays primary over any cleanup failure.

## Binary COPY in

The sink accepts a preformed `IReadOnlyList<T>` and each `WriteAsync` call is one complete
COPY operation:

```text
WriteAsync(batch)
    → validate batch
    → BeginBinaryImportAsync
    → StartRowAsync + row writer × rows
    → CompleteAsync
    → dispose importer
    → return success
```

```csharp
// batchSource creates a PipelineComponent<IPipelineSource<IReadOnlyList<OrderBatch>>>.
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

One batch envelope is one COPY because the Core sink contract treats a successful
`WriteAsync` as the completion of that sink operation. Holding a binary importer open across
many writes would report success for rows PostgreSQL had not accepted yet and would only
surface failure during disposal. Therefore:

- a successful `WriteAsync` means `NpgsqlBinaryImporter.CompleteAsync` already succeeded;
  it returns the number of rows copied and issues no `BEGIN`/`COMMIT` of its own;
- an empty batch is a successful no-op;
- a batch above `MaxRowsPerBatch` is rejected before any server work starts;
- disposing an incomplete importer cancels it and PostgreSQL reverts those rows;
- an earlier completed batch stays committed if a later batch fails, because each envelope
  is its own COPY operation;
- `DisposeAsync` performs cleanup only and never performs a business write;
- there is no automatic retry, and no hidden accumulation between envelopes, no tail buffer
  and no deferred SQL at disposal.

### Ambiguous completion is not guessed

A connection or network failure around completion can leave the caller unable to prove
whether the server committed immediately before transport loss. The package never claims
"definitely committed", "definitely rolled back" or "safe to replay". Retrying a COPY write
is an application decision that requires an explicit idempotency strategy, and no
exactly-once claim is made anywhere in this package.

### Generic batching is not part of the package

Applications supply already-formed batches. A generic batching or windowing runtime has to
answer tail flush, completion output, cancellation, bounded memory, multi-output semantics
and failure handling first; that problem is not hidden inside a PostgreSQL integration. The
package accepts no caller-supplied `IAsyncEnumerable` window and adds no timer or size
window.

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
```

PostgreSQL does not accept bind parameters for identifiers, so channel identifiers reach
the server quoted through Npgsql's public `NpgsqlCommandBuilder.QuoteIdentifier` API, which
escapes embedded quotes. `LISTEN` takes effect at commit, so the source reports ready only
after the registration transaction has committed.

`WaitAsync` returning `true` means only that an asynchronous message was received — a notice
or a parameter change also returns `true` — so the notification event is the signal, not the
wait result.

### NOTIFY is not a durable queue

`NOTIFY` provides none of the following, and the package does not pretend otherwise:

- durable storage, replay or a log;
- exactly-once delivery;
- at-least-once delivery across a disconnect;
- a guaranteed one-to-one mapping from `NOTIFY` statements to deliveries;
- a substitute for Kafka or a transactional outbox.

PostgreSQL behaviour that shapes the contract:

- a `NOTIFY` sent inside a transaction is delivered to peers only after that transaction
  commits;
- identical channel and payload pairs issued in one transaction may be folded into a single
  delivery;
- notifications from different committed transactions arrive in commit order;
- `LISTEN` takes effect at commit;
- when a session terminates its `LISTEN` registrations disappear.

There is no automatic reconnect. If the connection is lost the source faults, and
notifications issued during the gap cannot be recovered. Restarting the pipeline is an
application decision with the same missed-notification semantics.

### Durable outbox plus NOTIFY wake-up

```text
durable table / outbox = truth
NOTIFY                 = wake-up signal
```

Keep the real payload in a table and put a small key, version or wake-up token in the
notification, in the same transaction as the business change:

```csharp
// Values are bound as parameters. A channel identifier cannot be parameterized in
// PostgreSQL, so it must be an application-owned literal, never interpolated user input.
await using var command = dataSource.CreateCommand(
    "INSERT INTO outbox (id, payload) VALUES ($1, $2); NOTIFY orders_changed, 'outbox';");
command.Parameters.AddWithValue(orderId);
command.Parameters.AddWithValue(payload);
await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
```

PostgreSQL documents a race between establishing `LISTEN` and notifications that commit
concurrently. The recommended pattern is: wait until the notification source is ready, query
the durable application state with Dapper or EF Core, process that snapshot, and then rely on
later `NOTIFY` calls to wake the application for further checks. A notification may overlap
state the snapshot already observed, so consumers must be idempotent at the domain level
where that matters. SmartPipe cannot perform the domain snapshot for you, so there is no
generic snapshot callback.

### Bounded bridge and overflow

Npgsql delivers notifications through a synchronous event callback, so callback backpressure
cannot be awaited. The package bridges that callback into one internal bounded channel:

```text
Npgsql Notification event
    → copy Channel / Payload / PID into an immutable record
    → TryWrite
    → bounded channel (BufferCapacity)
    → source async enumerator
    → pipeline
```

The callback never blocks, never waits on the pipeline and never runs user asynchronous
code. A full bridge is an explicit fault: the rejected notification is not accepted and may
be lost, and the source faults rather than pretending the delivery was lossless. Overflow,
caller cancellation and provider faults compete through one first-cause slot.

## Ownership and lifetime

| Object | Owner | Lifetime |
|---|---|---|
| `NpgsqlDataSource` | application | long-lived, shared, never disposed by SmartPipe |
| `NpgsqlConnection` | SmartPipe runtime | one per component per run, disposed exactly once |
| `NpgsqlBinaryExporter` / `NpgsqlBinaryImporter` | SmartPipe runtime | one per enumeration or per batch |
| `ILoggerFactory` passed to a factory | application | borrowed, never disposed by SmartPipe |

`DisposeAsync` is idempotent. For a LISTEN source it stops notification acceptance and interrupts a pending server
wait, then unregisters the channels and releases the connection after that wait exits. An active enumeration unwinds
after the stop request; later enumeration is rejected.

All three factories return `PipelineComponent.RuntimeOwned` descriptors. Cleanup releases
each owned resource once, attempts every release in reverse acquisition order, and keeps a
primary operation failure before cleanup failures; a combined primary and cleanup failure is
an `AggregateException` with the primary failure first.

Connection cost per active component:

```text
active COPY source ≈ one leased Npgsql connection
active COPY sink   ≈ one leased Npgsql connection
active LISTEN      ≈ one dedicated leased connection for the run lifetime
```

Size the pool accordingly, and prefer one listener serving several channels over one
listener per channel.

## Ambient transactions are rejected

Npgsql enlists opened connections automatically when `System.Transactions.Transaction.Current`
is set, which would make "one batch envelope equals one completed COPY" untrue and would
silently couple pipeline work to a caller's transaction. Every component therefore throws
`InvalidOperationException` when an ambient transaction is present: once before opening its
connection, and again immediately before starting COPY or `LISTEN` protocol work. Components
accept no caller-owned transaction, do not participate in ambient transactions, and do not
require `Enlist=false`.

## Security responsibility

The package creates no connection strings, data sources, credentials, certificates or
tokens, and it does not rotate them. The application configures the `NpgsqlDataSource`, and
credential refresh strategies such as Npgsql's periodic password or token providers stay
outside the pipeline lifecycle.

Transport encryption without server identity verification is **not** authenticated TLS.
Production deployments must select the Npgsql SSL verification settings appropriate to their
environment. The disposable local servers used by this repository's test fixtures run with
trust authentication and disabled durability; that configuration is never a production
recommendation.

## Logging

The package depends on `Microsoft.Extensions.Logging.Abstractions`. Factories accept an
optional borrowed `ILoggerFactory` that SmartPipe never disposes and never resolves from a
service locator. The package never logs connection strings, credentials, COPY SQL text,
notification payloads, row payloads or parameter values, and it performs no per-row logging.
Permitted structured fields are the operation name, the operation kind, row counts,
notification buffer capacity or count category, the failure category, and pipeline/run
identity where Core already exposes it.

## NativeAOT and trimming

The package declares the positive `IsAotCompatible`, trim-analyzer and AOT-analyzer contract
for the slim/static primitive path only: `NpgsqlSlimDataSourceBuilder`, built-in primitive
mappings, static COPY callbacks, no dynamic JSON, no unmapped types and no composite
mapping.

- primitive mappings (`int`, `text`, `numeric`, `date`, `time`, `timestamp`, `timestamptz`,
  `uuid`, `jsonb` as `string`) work without opt-in;
- arrays, JSON document types and POCO mappings need explicit Npgsql opt-ins;
- binary COPY and `LISTEN` carry no `RequiresUnreferencedCode` or `RequiresDynamicCode`
  annotations, and Npgsql ships `IsAotCompatible` for `net8.0+`;
- transport security is off by default in the slim builder; call `EnableTransportSecurity()`
  when the deployment requires TLS.

Caller opt-ins such as `EnableDynamicJson`, `EnableRecordsAsTuples`, `EnableUnmappedTypes`,
`MapComposite` and `MapEnum(Type, …)` keep their own warnings. SmartPipe never suppresses
them globally, and arbitrary application callbacks remain outside the claim.
See [AOT and trimming compatibility](aot-compatibility.md).

## Composition

- **Dependency injection.** The package has no DI dependency. Build the data source and the
  definition in the application, then register the definition with
  `services.AddSmartPipe().AddPipeline(definition)`. The definition captures the data source
  explicitly; the package never resolves `NpgsqlDataSource` from
  `PipelineActivationContext.Services`.
- **Dapper.** `NpgsqlDataSource` derives from the generic `DbDataSource` boundary, so one
  application pool serves both packages. Neither package disposes the data source, each run
  obtains its own connection, and there is no production package dependency between them.
- **EF Core.** `SmartPipe.Extensions.EntityFrameworkCore` stays provider-neutral; the
  application supplies `Npgsql.EntityFrameworkCore.PostgreSQL` itself. The runtime-owned
  `DbContext` and the runtime-owned COPY/LISTEN connections have independent lifetimes.
- **OpenTelemetry.** `SmartPipe.Extensions.OpenTelemetry` instruments the pipeline runtime
  and `Npgsql`/`Npgsql.OpenTelemetry` instruments the database client. This package has no
  production dependency on either and creates no database client spans of its own.
- **Resilience.** There is no built-in PostgreSQL retry and no Polly dependency. Retrying a
  COPY after an uncertain completion is not automatically safe, and restarting a listener
  does not recover missed notifications.
- **Health checks.** `SmartPipe.Extensions.HealthChecks` covers pipeline run health, not
  database reachability, and this package adds no health-check API.

## Non-goals

Out of scope for this package: text, CSV and raw backup/restore COPY; a `NOTIFY` sink;
logical or physical replication and CDC; replication-slot management and LSN acknowledgement;
advisory or distributed locks; connection strings, data-source construction, pool
management, provider failover or load-balancer abstraction; TLS, GSS, authentication-token
or secret-rotation wrappers; built-in retries; database health-check APIs; automatic
`LISTEN` reconnect; schema migrations, pgvector, PostGIS, NodaTime, JSONB serializer and
enum/composite wrappers; large objects; exactly-once semantics; a cross-component
transaction coordinator; and a generic batch or windowing runtime.

Logical replication and CDC are excluded on semantic grounds, not on effort: acknowledging a
WAL position correctly requires a downstream settlement contract, where a source is
acknowledged only after downstream success, with explicit negative settlement, checkpoint
persistence and recovery semantics. Core has no such contract yet; acknowledging early risks
data loss, and never acknowledging grows the replication slot.

## Requirements and baselines

- .NET 10 (`net10.0`).
- `SmartPipe.Core` 2.2.0.
- `Npgsql` 10.0.3, the latest stable release (no Npgsql 11 exists, not even a prerelease);
  the provider behaviour above was reconned against the released v10.0.3 sources.
- `Microsoft.Extensions.Logging.Abstractions`.
- PostgreSQL 18.6 is the primary baseline and PostgreSQL 17.11 the compatibility baseline.
  Both were verified on 2026-09-26.

## Related

- [SmartPipe 2.2 integration package migration](migration/2.2.0-integration-packages.md)
- [Package ownership](package-ownership.md)
- [AOT and trimming compatibility](aot-compatibility.md)
- [Extension architecture plan](plans/2.2.0-extension-architecture.md)
