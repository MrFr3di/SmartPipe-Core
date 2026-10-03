# Architecture

SmartPipe is organized around one typed, in-process pipeline runtime and a set
of narrow integration packages. The architecture separates runtime semantics,
integration ownership, compatibility, and release evidence so that each can be
validated independently.

## Runtime model

```text
source
  -> bounded input
  -> worker(s)
  -> sequential typed stage chain
  -> optional sink
  -> policy-gated outputs
```

Sources produce `ProcessingEnvelope<T>`. Transformers return
`StageResult<T>`. Sinks consume typed envelopes.

For one envelope, the stage chain is sequential. Multiple envelopes may execute
concurrently when `MaxConcurrency > 1`; cross-envelope output ordering is not
guaranteed.

The normative lifecycle, output, observer, cancellation, failure-precedence,
and cleanup semantics are documented in
[Runtime contracts](runtime-contracts.md).

## Runtime ownership

A `PipelineRun<TOutput>` owns one execution.

SmartPipe distinguishes:

- runtime-owned components — created and disposed by the runtime;
- scope-owned components — created from a run scope and disposed with that
  scope;
- borrowed/external components — caller-owned and never silently disposed by
  SmartPipe.

Reusable pipeline definitions use per-run component descriptors. Definitions
that retain borrowed state are single-use.

## Package architecture

```text
                       +----------------------+
                       |    SmartPipe.Core    |
                       +----------+-----------+
                                  |
          +-----------------------+-----------------------+
          |                       |                       |
          v                       v                       v
  narrow integration       infrastructure leaves    optional leaves
       packages             (DI/Host/Health/OTel)   (for example PostgreSql)
          |                       |                       |
          +-----------------------+-----------------------+
                                  ^
                                  |
                    +-------------+-------------+
                    | SmartPipe.Extensions      |
                    | compatibility facade      |
                    +---------------------------+

                    SmartPipe.Testing -> Core
```

The exact package IDs, lifecycle, publish order, AOT contract, and dependency
policy are normative in `eng/package-graph.json`. See the generated-style
[package reference](reference/packages.md) for a human-readable projection.

Architecture invariants:

- `SmartPipe.Core` does not depend on `SmartPipe.Extensions*`.
- Narrow leaves do not depend back on the broad
  `SmartPipe.Extensions` compatibility facade.
- `SmartPipe.Extensions.Http` does not acquire an implicit Polly dependency.
- Health checks do not depend on Hosting.
- `SmartPipe.Extensions.PostgreSql` is optional and remains outside the broad
  facade.
- `SmartPipe.Testing` is test-only and is not a production dependency.
- Package ownership is single-source: a public type has one implementation
  owner even when the compatibility facade forwards that identity.

## Compatibility model

SmartPipe treats source, binary, API, behavioral, package, and AOT compatibility
as separate dimensions.

For the 2.1.2 → 2.2.0 transition, the compatibility facade preserves retained
identities through a combination of physical facade types and metadata type
forwarders. Approved removals are documented rather than hidden behind broad
API suppressions.

The machine-readable ownership contract lives in
`eng/package-ownership.json`. The immutable 2.1.2 package baseline lives under
`eng/baselines/2.1.2/`.

See:

- [Versioning and compatibility](../VERSIONING.md)
- [2.1.2 → 2.2.0 compatibility matrix](reference/compatibility/2.1.2-to-2.2.0.md)
- [2.1.2 → 2.2.0 migration guide](migration/2.2.0-integration-packages.md)

## Backpressure and channels

Input, output, and buffered-observer queues are bounded. Runtime channel
configuration makes reader/writer cardinality explicit. Dropping behavior is a
configured policy and is observable; it is never an accidental substitute for
backpressure.

## Failure and lifecycle

Stage execution owns retry, timeout, circuit breaker, dead-letter routing, and
terminal stage actions. The run lifecycle coordinates drain, cooperative
cancellation, abort intent, completion, fault publication, and cleanup.

Primary processing or mandatory-cleanup failures retain precedence over abort,
cancellation, and normal completion.

## Observability

`SmartPipe.Core` owns the stable diagnostic source names and emits .NET
metrics/activities. Integration packages such as
`SmartPipe.Extensions.OpenTelemetry` register those sources with external
telemetry systems; they do not create a second SmartPipe runtime or select the
application's exporter policy.

## AOT and trimming

AOT/trimming support is package-specific. Core and some leaves have positive
contracts, while reflection/dynamic-code-heavy integrations use narrower claims
or explicit annotations.

The canonical package-level claim is `aotContract` in
`eng/package-graph.json`; documentation must not widen that claim.

See [AOT and trimming compatibility](aot-compatibility.md).

## Documentation and evidence boundaries

Product documentation describes stable/current contracts. Architecture
decisions explain accepted trade-offs. Version-specific plans and implementation
records are maintainer/release evidence and must not become a second source of
truth for current runtime behavior.

Start from the [documentation index](index.md) for the audience-oriented map of
the repository documentation.
