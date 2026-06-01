# Package Readiness

SmartPipe.Core 1.1.0 package readiness requires evidence across build, API,
consumer, and security dimensions.

## Core Package

Required release checks:

- restore;
- Release build;
- Core tests;
- package validation;
- `dotnet pack -c Release`;
- public API baseline review;
- XML docs for changed public/protected APIs;
- Source Link and symbol package metadata where configured;
- consumer smoke test;
- trim/AOT smoke before any AOT-ready claim.

Core currently treats compiler/analyzer warnings as errors for the production
package project.

## Extensions Package

`SmartPipe.Extensions` stable `1.1.0` is blocked if preview dependencies remain.
The current 1.1.0 project file uses stable Microsoft 10.x package references;
if preview dependencies return, the package version must be `1.1.0-preview.*`.

Package splitting should wait until Core execution, envelope, observer, and
failure APIs are stable enough to avoid multiplying unstable public contracts.

## Consumer Smoke Test

The consumer matrix should cover:

- local package install;
- minimal pipeline compile/run;
- typed chain compile/run;
- background output;
- dead-letter serializer/redactor usage;
- trim publish;
- NativeAOT smoke where supported.

Update6 evidence:

- NuGet package audit: `.work/package/nupkg-audit.md`.
- Package metadata audit: `.work/package/package-metadata.md`.
- Consumer smoke: `.work/package/consumer-smoke-test.md`.
- Trim smoke: `.work/runtime/trimming.md`.
- NativeAOT smoke: `.work/runtime/nativeaot.md`.

The update6 consumer smoke validates package installation, a legacy pipeline,
the modern typed pipeline path, `RunInBackground`, and one low-risk Extensions
transform. Dead-letter replay remains a later release-readiness scenario.

Update7 evidence:

- NuGet lock-file policy: `.work/dependencies/update7-lockfiles.md`.
- Vulnerability scan: `.work/dependencies/update7-sca.md`.
- Deprecated package scan: `.work/dependencies/update7-deprecated.md`.
- Outdated package scan and upgrade decisions:
  `.work/dependencies/update7-outdated.md`.
- Platform/runtime smoke matrix: `.work/compat/update7-platform-matrix.md`.

`RestorePackagesWithLockFile=true` is enabled for deterministic dependency
resolution. CI verifies `dotnet restore SmartPipe.Core.slnx --locked-mode`
before build/test/pack.

Update8 evidence:

- Test/benchmark warning inventory:
  `.work/tests/update8-warning-inventory.md`.
- Warning cleanup report: `.work/tests/update8-warning-cleanup.md`.
- xUnit v3 feasibility decision:
  `.work/tests/update8-xunit-v3-feasibility.md`.
- Deprecated test dependency ownership:
  `.work/dependencies/update8-deprecated-followup.md`.

Test and benchmark assemblies do not generate XML documentation files. This is
intentional: production packages keep XML documentation enabled, while tests and
benchmarks are not NuGet deliverables and should not bury analyzer signal under
documentation noise.

Update9 evidence:

- CI warning gate report:
  `.work/agent/update9-warning-gate.md`.

CI now builds both test projects and the benchmark project with `-warnaserror`
after the normal test run and before packaging. This keeps the update8
warning-clean state enforceable without changing production runtime behavior.

Update10 evidence:

- CI trim/AOT smoke report:
  `.work/agent/update10-trim-aot-ci.md`.

CI now publishes and runs the package-installed consumer smoke with
`PublishTrimmed=true` and `PublishAot=true` on `linux-x64`. This is a regression
gate for the existing smoke scenario, not a broad package-wide AOT compatibility
claim.

Update11 evidence:

- Extensions AOT/trim matrix:
  `.work/runtime/extensions-aot-matrix.md`.

`SmartPipe.Extensions` remains analyzer-clean for trim/AOT checks, but JSON,
dead-letter, CSV, Dapper, EF Core, and Mapster paths are conditional/risk areas
until they have focused publish-and-run harness evidence.

Update12 evidence:

- JSON/dead-letter source-generated AOT report:
  `.work/agent/update12-json-deadletter-aot.md`.
- Trim smoke evidence:
  `.work/runtime/trimming.md`.
- NativeAOT smoke evidence:
  `.work/runtime/nativeaot.md`.
- Extensions AOT matrix:
  `.work/runtime/extensions-aot-matrix.md`.

The package-installed update12 harness covers `JsonTransform<TInput,TOutput>`,
`JsonFileSource<T>`, `JsonFileSink<T>`, legacy Extensions dead-letter
source-generated overloads, and modern `JsonLinesDeadLetterSerializer<T>` with
`DeadLetterEnvelope<T>` source-generated metadata. These scenarios are now
source-gen smoke-verified under normal execution, trimming, and NativeAOT. The
evidence does not extend to CSV, Dapper, EF Core, Mapster, HTTP, hosting, or a
broad package-wide `SmartPipe.Extensions` AOT compatibility claim.
