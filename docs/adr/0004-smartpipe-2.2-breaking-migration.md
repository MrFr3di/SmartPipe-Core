# ADR-0004: SmartPipe 2.2 HTTP breaking migration and checkpoint route

- Status: Accepted for implementation
- Date: 2026-09-24
- Decision owners: SmartPipe maintainers
- Target release: 2.2.0
- Accepted predecessor checkpoint: E, true merge `520dc7088f57f3149224631b5a383f06e7e9f79e` (PR #91)

## Context

ADR-0001 establishes facade forwarding and obsolete wrappers for moved public
types. The four composite HTTP identities combine transport with JSON and
resilience dependencies, so preserving them would retain the package coupling
that the HTTP leaves are meant to remove. The accepted user scope explicitly
allows this source and binary break.

## Decision

Remove these identities from `SmartPipe.Extensions` in 2.2.0:

- `HttpSelector`
- `HttpClientFactorySelector`
- `HttpSink`
- `HttpClientFactorySink`
- `HttpSelectorStreamingMode`, whose only meaning was selecting the response
  format of the removed `HttpSelector`

Record each as `Removed` with an explicit replacement in the existing ownership
matrix. They must be absent from implementation and type forwarders. Add no
wrapper, alias, or old-binary shim; consumers using them update to the HTTP leaf
APIs and recompile. Use only targeted native ApiCompat suppression for these
approved removals. Other moved identities remain under ADR-0001, and the
DI/Hosting/Health compatibility quarantine remains governed by ADR-0002.

The 2.2.0 work is integrated by checkpoints: E is SP220-09–12, F is SP220-13–16,
and G is SP220-17–18. F starts from accepted E; E is not promoted a second time.
At F entry no remote F ref was advertised. Create the local F integration ref
from the accepted E true merge above before opening the first F task branch;
publish it when ready for review. Task changes integrate into F. Promote a
checkpoint to `release/2.2.0` only after its full scope is reviewed and its
required evidence passes. The F HTTP contribution does not target the release
branch directly.

HTTP transport and JSON codecs remain separate leaf packages. The
transport contract uses borrowed direct `HttpClient` instances or
operation-owned clients from `IHttpClientFactory`, owns each returned request
and response, streams responses through caller readers, bounds error previews,
and does not retry implicitly. `Http.Json` supplies bounded array and NDJSON
codecs using `JsonTypeInfo<T>`; it has no reflection fallback as a positive
trim/AOT path. Exact contracts and limits live in the HTTP sections of the
[architecture plan](../plans/2.2.0-extension-architecture.md) and the two
package READMEs.

## Consequences

HTTP callers must migrate and recompile; 2.1.2 binary compatibility is not
preserved for the removed identities. The facade no longer carries these
composite APIs. ADR-0001 is partially superseded for HTTP compatibility only;
ADR-0002 and ADR-0003 are unchanged.

## Amendment: SP220-14 Polly no-op removal

- Date: 2026-09-26
- Accepted checkpoint base: F, true merge `7901a79ce153c3fec22121b9d782d5c8a1576451` (PR #92)

`SmartPipe.Extensions.Transforms.PollyResilienceTransform<T>` is removed from
`SmartPipe.Extensions` under the same rules. Its Polly callback returned
`StageResult<T>.Success(envelope.Payload)` without running an inner transform,
so forwarding it would preserve a type that never protected real work. It is
recorded as `Removed` in the ownership matrix, is absent from implementation and
type forwarders, and has one targeted native `CP0001` ApiCompat suppression.
Consumers migrate to `SmartPipe.Extensions.Polly`
(`PollyTransformDecorator<TInput,TOutput>` and `PollyPipelineComponents.Decorate`)
and recompile. The facade no longer depends on `Microsoft.Extensions.Resilience`.

## Supersession

This ADR partially supersedes ADR-0001's compatibility requirements for the
four named HTTP identities and, by the SP220-14 amendment, for
`PollyResilienceTransform<T>`. It also replaces the direct-to-release task route in
the 2.2.0 branch policy with the E/F/G checkpoint sequence. All unrelated
package, runtime, and compatibility decisions remain in force.
