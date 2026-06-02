# AOT And Trimming Compatibility

SmartPipe.Core 1.1.0 uses an evidence-first trim and NativeAOT posture.

The current documentation does not make a broad package-wide `AOT-ready` claim.
Compatibility statements are limited to the focused scenarios that are covered
by project build, CI, and consumer validation.

## Core

Core may enable analyzer properties such as:

```xml
<EnableTrimAnalyzer>true</EnableTrimAnalyzer>
<EnableAotAnalyzer>true</EnableAotAnalyzer>
<VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>
```

Analyzer-clean builds are necessary evidence, but not sufficient for a broad
public AOT compatibility claim. Publish-and-run consumer harnesses are still
required.

Current posture:

- Core builds with trim/AOT analyzer checks enabled in the package project.
- Focused package-installed trim and NativeAOT consumer smoke scenarios have
  been validated.
- Broad API-surface and every-integration AOT compatibility is not claimed.

`ProcessingEnvelope<T>.Create`, `PipelineRuntimeOptions`, `IPipelineClock`,
observer dispatch options, and circuit breaker evaluation options are
AOT-neutral configuration APIs. They do not introduce reflection or dynamic
code paths.

## Extensions

`SmartPipe.Extensions` contains integrations whose AOT behavior depends on the
underlying package and usage pattern. Reflection-heavy or runtime-codegen paths
must remain scoped unless a focused harness covers them.

Current posture:

- Analyzer-clean for the current Extensions project build.
- Smoke-verified for package install, legacy runtime, typed runtime,
  `RunInBackground`, and a low-risk transform scenario.
- Focused source-generated JSON and dead-letter scenarios have trim and
  NativeAOT smoke evidence.
- CSV, Dapper, EF Core, Mapster, HTTP, hosting, and other integration-specific
  paths are not covered by a broad AOT claim.

## Source-Generated JSON Paths

Prefer source-generated metadata overloads for trim and NativeAOT scenarios:

- `JsonTransform<TInput,TOutput>(JsonTypeInfo<TInput>, JsonTypeInfo<TOutput>)`;
- `JsonFileSource<T>(string, JsonTypeInfo<List<T>>, JsonTypeInfo<T>)`;
- `JsonFileSink<T>(string, JsonTypeInfo<List<T>>, int)`;
- `DeadLetterSource<T>(string, JsonTypeInfo<T>)`;
- `DeadLetterSink<T>(string, JsonTypeInfo<ProcessingResult<T>>, ...)`;
- `JsonLinesDeadLetterSerializer<T>(JsonTypeInfo<DeadLetterEnvelope<T>>)`.

Reflection/options-based JSON constructors remain for compatibility and should
not be documented as trim- or NativeAOT-safe.
