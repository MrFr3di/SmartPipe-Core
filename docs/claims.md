# Claims Policy

SmartPipe.Core release claims must be backed by reproducible evidence.

## Verdicts

Each claim should be classified as:

- `Proven`
- `Partially Proven`
- `Not Proven`
- `False`
- `Not Applicable`

## Claims Requiring Evidence

The following claims must not appear in release-facing docs or package metadata
without evidence:

- production-ready;
- 0 allocations;
- 0 dependencies;
- lock-free;
- exact coverage;
- exact test count;
- AOT-ready;
- dead-letter replay;
- adaptive parallelism;
- O(1) behavior for complex operations.

## Evidence Types

Acceptable evidence includes:

- CI logs;
- package validation results;
- API compatibility reports;
- benchmark artifacts;
- consumer smoke-test logs;
- trim/AOT publish logs;
- targeted tests that assert the behavior.

## Current 1.1.0 Claim Posture

`SmartPipe.Core` is described as a streaming pipeline library built on
`System.Threading.Channels`. It currently depends on
`Microsoft.Extensions.Logging.Abstractions`, so it must not be documented as
having zero dependencies.

## Current Release-Facing Claims

| Claim | Verdict | Evidence |
|---|---|---|
| SmartPipe.Core is a streaming pipeline library built on `System.Threading.Channels`. | Proven | Source implementation and package consumer smoke in `.work/package/consumer-smoke-test.md`. |
| Legacy `ISource`/`ITransformer`/`ISink` APIs remain supported for 1.x compatibility. | Proven | Consumer smoke uses package-installed legacy `SmartPipeChannel`; regression tests cover legacy runtime behavior. |
| New `PipelineBuilder` and `IPipeline*` APIs support envelope-aware typed execution. | Proven | Consumer smoke uses package-installed typed chain; runtime tests cover modern pipeline behavior. |
| `SmartPipe.Core` has zero dependencies. | False | Package metadata shows dependency on `Microsoft.Extensions.Logging.Abstractions`. |
| `SmartPipe.Extensions` uses stable dependencies for 1.1.0. | Proven | Package metadata audit in `.work/package/package-metadata.md`; no preview dependency versions found. |
| `0 allocations`, `0B hot path`, or equivalent allocation claim. | Not Proven | No benchmark gate exists yet. |
| AOT-ready. | Partially Proven | Trim and NativeAOT consumer smoke passed locally in `.work/runtime/*`; source-generated JSON/dead-letter path has focused update12 evidence, but no global AOT-ready claim is made because package-specific coverage is still limited. |
| Source-generated JSON/dead-letter path supports trim and NativeAOT smoke scenarios. | Proven for focused smoke | update12 package-installed harness in `.work/agent/update12-json-deadletter-aot.md`, `.work/runtime/trimming.md`, and `.work/runtime/nativeaot.md`. |
| Reflection/options-based JSON constructors are trim/AOT-safe. | False | These constructors are annotated with `RequiresUnreferencedCode` and `RequiresDynamicCode`; use `JsonTypeInfo` overloads for trim/NativeAOT. |
| Legacy Extensions dead-letter format is replay-safe. | False | Legacy Extensions dead-letter persists `ProcessingResult<T>` and failed results do not preserve the original payload. |
| Exact current coverage percentage or exact test count. | Not Proven | Current CI collects coverage, but no committed coverage artifact or release threshold is available in this repository. |
