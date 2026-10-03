# API overview

This page is an orientation map, not a hand-maintained signature reference.
Exact public signatures come from the source/XML documentation and the
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` baselines validated by
the repository.

## Core pipeline model

The primary abstractions are:

- `IPipelineSource<T>` — produces typed `ProcessingEnvelope<T>` values;
- `IPipelineTransformer<TInput,TOutput>` — converts one envelope into a
  `StageResult<TOutput>`;
- `IPipelineSink<T>` — consumes typed envelopes;
- `PipelineBuilder` — single-use instance composition;
- `PipelineDefinitionBuilder` / `PipelineDefinition<,>` — definition-first
  composition with explicit component descriptors;
- `PipelineRun<T>` — running execution, state, completion, controls, metrics,
  and output access.

For lifecycle, ownership, output, cancellation, and failure precedence, use the
[Runtime contracts](../runtime-contracts.md).

## Failure and resilience

Core owns stage-level policy primitives such as retry, timeout, circuit breaker,
dead-letter routing, and terminal failure actions. Integration packages may
adapt external resilience libraries, but they do not replace Core lifecycle
semantics.

See [Resilience](../resilience.md).

## Adapters and helpers

Core includes typed adapters for async-enumerable sources, delegate transforms,
and delegate sinks. Integration-specific factories/builders live in their
owning packages.

## Integration surface

Use the narrow owning package for new code:

- JSON, CSV, Dapper, EF Core, Mapster;
- HTTP and HTTP JSON;
- Polly;
- Channels, Transforms, DataAnnotations, Logging;
- DependencyInjection, Hosting, HealthChecks, OpenTelemetry;
- PostgreSQL;
- Testing helpers.

See the [package reference](packages.md) and package-specific READMEs for exact
entry points, ownership, and AOT/trimming claims.

## Compatibility API

`SmartPipe.Extensions` is a compatibility facade/bundle. Some 2.1.2 public
identities remain facade-owned, some are metadata-forwarded to leaf packages,
and six are intentionally removed.

See the [compatibility reference](compatibility/README.md) and
[2.2 migration guide](../migration/2.2.0-integration-packages.md).

## Generated API documentation

The documentation workflow already projects package XML documentation into
generated DocFX API pages under the site's `api/` section. Those generated pages
are a browsing surface, not a separate compatibility authority: source,
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`, package validation, and
executable consumers remain the contracts used for release acceptance.
