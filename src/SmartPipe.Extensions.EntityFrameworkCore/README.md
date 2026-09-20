# SmartPipe.Extensions.EntityFrameworkCore

Provider-neutral Entity Framework Core query sources for `SmartPipe.Core`.

## What this package owns

- `EfCorePipelineComponents.QuerySource<TContext,TResult>` — a queryable source over either a borrowed
  `IDbContextFactory<TContext>` or a caller-supplied async context factory
  `Func<PipelineActivationContext,CancellationToken,ValueTask<TContext>>`.
- `EfCorePipelineComponents.CompiledQuerySource<TContext,TResult>` — a source over a caller-provided
  compiled or asynchronous sequence with the same two context-acquisition forms.
- `EfCorePipelineDefinitionBuilder.FromQuery<TContext,TResult>` and
  `FromCompiledQuery<TContext,TResult>` — typed entry points that return the ordinary Core
  `PipelineDefinitionBuilder<TResult>`; stage attachment stays on the normal `Transform` chain.

The package depends on `Microsoft.EntityFrameworkCore` and `Microsoft.Extensions.Logging.Abstractions` only. It never references a provider
(`SqlServer`, `Npgsql`, `Sqlite`, `InMemory`), `SmartPipe.Extensions`, Dapper, HTTP, JSON, Mapster,
Polly, Hosting, or dependency injection. It exposes no write sink, `SaveChanges`, repository,
unit-of-work, ambient transaction, or provider-specific abstraction.

## Query behaviour

| Concern | Contract |
|---|---|
| Tracking | `EfCoreQueryOptions.TrackingMode` applies `AsNoTracking` (default), `AsNoTrackingWithIdentityResolution`, `AsTracking`, or no operator at all (`PreserveQuery`) to the caller-created `IQueryable<TResult>`. The queryable path keeps `where TResult : class`, which the tracking operators require. |
| Compiled path | `CompiledQuerySource` has no tracking option because the delegate already fixes the query shape, and its result type is intentionally unconstrained so scalar and struct results work. |
| Composition | Creating a component or definition invokes neither the context factory nor the query factory and performs no I/O. Options are validated and snapshotted at composition. |
| Context ownership | The factory and logger factory are borrowed. `CreateDbContextAsync` is called once per run; that context belongs to the run and is released exactly once. Concurrent runs never share a context. A factory that returns an already-active shared context is a caller-contract violation that this package does not detect. |
| Enumeration | One logical enumeration per run. The caller's query factory runs once at activation and the compiled delegate runs once per run. Early break and cancellation release the enumerator first and then the context. A second enumeration or any use after disposal fails deterministically. |
| Failures | Context creation, query factory, enumeration, and mapping failures stay primary; cleanup runs exactly once with `CancellationToken.None` and can never replace a primary failure. Provider exceptions are preserved and no timeout or retry loop is hidden here. |
| Logging | Operation name, result type, pipeline key, run id, item count, duration, and outcome only. Query text, parameter values, connection strings, and payloads are never logged. |

Provider buffering (for example a provider that materializes a result set) remains provider behaviour;
this package never forces buffering with `ToListAsync`.

## Trimming and NativeAOT

The package makes no blanket trimming or NativeAOT claim. Query shape, provider choice, compiled
models, and generated query delegates are evaluated by the consumer. Any reflection or dynamic-code
path that appears later must be annotated and documented rather than suppressed globally.

The forwarded legacy `EfCoreSelector<T>` resolves its entity set through `DbContext.Set<T>()`, which is
trimming-unsafe. Its narrow internal suppression documents exactly that boundary, and the factory-based
sources above are the supported alternative for trimmed or NativeAOT consumers.

## Usage

```csharp
var definition = EfCorePipelineDefinitionBuilder
    .FromQuery<AppDbContext, Order>(
        new PipelineKey("orders"),
        contextFactory,                       // IDbContextFactory<AppDbContext> or an async factory
        static (context, activation) => context.Orders.Where(order => order.Total > 0),
        new EfCoreQueryOptions { OperationName = "orders" })
    .Build();

await using var run = await definition.StartAsync();
await run.Completion;
```

## Migrating from 2.1.2

`SmartPipe.Extensions.Selectors.EfCoreSelector<T>` moved into this package and stays reachable from
`SmartPipe.Extensions` through type forwarding, with its namespace, constructors, and behaviour
unchanged. It keeps its shipped borrowed-context semantics: it never disposes a caller-owned
`DbContext`. New code uses the factory-based sources above, which own exactly one context per run.
