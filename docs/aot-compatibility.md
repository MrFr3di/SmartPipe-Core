# AOT And Trimming Compatibility

SmartPipe.Core 1.1.0 uses an evidence-first AOT posture.

## Core

Do not set `IsAotCompatible=true` until a consumer publish harness completes
without actionable IL warnings.

Before making a public compatibility claim, Core may enable analyzer properties:

```xml
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
```

These analyzer properties are not the same as a public AOT-ready claim.

Current local analyzer status:

- Core builds with trim/AOT analyzers enabled and no analyzer warnings after
  moving to stable `Microsoft.Extensions.Logging.Abstractions` 10.x.
- Update6 local consumer evidence:
  - `PublishTrimmed=true` for the package-installed consumer smoke passed and
    the trimmed executable ran successfully. Evidence:
    `.work/runtime/trimming.md`.
  - `PublishAot=true` for the package-installed consumer smoke passed and the
    native executable ran successfully. Evidence:
    `.work/runtime/nativeaot.md`.
- Core still must not make a broad AOT-ready release claim until the coverage is
  expanded beyond the smoke harness and reviewed for package-specific warnings.

## Extensions

`SmartPipe.Extensions` must maintain an explicit AOT status matrix by integration
area. Reflection-heavy or runtime-codegen integrations such as object mappers,
ORMs, and serializers may be partial or unsupported unless configured with
source-generated metadata or package-specific AOT support.

If preview dependencies return, `SmartPipe.Extensions` must use a preview package
version instead of stable `1.1.0`. The current 1.1.0 project file uses stable
Microsoft 10.x package references.

Update6 evidence is smoke-level only for Extensions because the consumer harness
references `SmartPipe.Extensions` and exercises `FilterTransform<T>`. It does
not prove AOT compatibility for EF Core, Dapper, Mapster, CSV, HTTP, or hosting
integrations.

Update7 repeated trim and NativeAOT smoke after dependency-governance changes.
Evidence: `.work/runtime/update7-trim-aot.md`. The verdict remains unchanged:
smoke-level evidence is useful, but it is not a package-wide AOT-ready claim.

Update10 adds CI trim and NativeAOT smoke for the package-installed consumer
scenario on `linux-x64`. Evidence: `.work/agent/update10-trim-aot-ci.md`.
This makes regressions in the current smoke path visible in pull requests, but
does not expand the claim beyond the exercised Core path and one low-risk
Extensions transform.

Update11 classifies `SmartPipe.Extensions` by integration area. Evidence:
`.work/runtime/extensions-aot-matrix.md`.

Current Extensions posture:

- Analyzer-clean: the Extensions project builds with trim/AOT analyzers enabled
  and no warnings.
- Smoke-verified: package install, legacy runtime, typed runtime,
  `RunInBackground`, and `FilterTransform<T>`.
- Conditional/risk areas: JSON, dead-letter, CSV, Dapper, EF Core, and Mapster
  paths require focused harnesses before any broader AOT compatibility claim.

Update12 adds focused source-generated JSON evidence:

- `JsonTransform<TInput,TOutput>` has a `JsonTypeInfo<TInput>` /
  `JsonTypeInfo<TOutput>` constructor for trim/NativeAOT scenarios.
- `JsonFileSource<T>` and `JsonFileSink<T>` have source-generated metadata
  constructors for DTO arrays, NDJSON items, and buffered batches.
- Legacy `DeadLetterSink<T>` and `DeadLetterSource<T>` have source-generated
  metadata constructors for the legacy `ProcessingResult<T>` file shape.
- Modern `JsonLinesDeadLetterSerializer<T>` has a source-generated
  `JsonTypeInfo<DeadLetterEnvelope<T>>` path that was publish-and-run tested
  under trimming and NativeAOT.

The older `JsonSerializerOptions`/reflection-based constructors remain for
1.x compatibility and are explicitly marked as not trim/NativeAOT-safe. Legacy
Extensions dead-letter remains a diagnostic record format, not replay-safe
dead-letter storage. Replay-safe dead-letter uses `DeadLetterEnvelope<T>` plus
`JsonLinesDeadLetterSerializer<T>` with source-generated metadata.

Evidence:

- `.work/runtime/extensions-aot-matrix.md`
- `.work/runtime/trimming.md`
- `.work/runtime/nativeaot.md`
- `.work/agent/update12-json-deadletter-aot.md`

This is still not a broad `SmartPipe.Extensions` AOT-ready claim. CSV, Dapper,
EF Core, Mapster, HTTP, hosting, and other integration paths remain
package-specific compatibility areas until covered by their own harnesses.
