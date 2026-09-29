# Project Core Technologies

## Languages and Runtimes

- C# on .NET `net10.0`; the repository pins SDK `10.0.303` with
  `rollForward: disable` in `global.json`.
- Nullable reference types, implicit usings, deterministic builds, latest
  analyzers, and lock-file restore are enabled in `Directory.Build.props`.
- Package version is `2.2.0` in `Directory.Build.props`; central package
  management is enabled in `Directory.Packages.props`.

## Frameworks and Libraries

- `System.Threading.Channels`-based bounded runtime channels and async
  execution.
- `Microsoft.Extensions.*` abstractions for logging, hosting, HTTP,
  resilience, and health checks.
- `System.Text.Json` source-generated metadata paths in the JSON package.
- Extension integrations include CsvHelper, Dapper, EF Core, Mapster, and
  Microsoft resilience APIs; exact versions are centrally pinned.

## Build, Test, and Development Tools

- `SmartPipe.Core.slnx` groups source, tests, repository checks, and the main
  benchmark project.
- Tests use xUnit v3 on Microsoft Testing Platform (`global.json`), with
  FluentAssertions, Moq, and Microsoft Testing Platform extensions.
- `eng/SmartPipe.RepositoryChecks` validates package graph/ownership,
  metadata, lock files, baselines, release versions, and consumer scenarios.
- RepositoryChecks provides compact `agent-context`, `verify-task`, and
  `evidence` commands. Verification profiles compose existing atomic checks;
  successful gates return status/counts while raw logs stay on disk. The
  active ExecPlan is the only task-context source, and agent commands are
  local-only.
- BenchmarkDotNet is used by `benchmarks/SmartPipe.Benchmarks`.

## External Services and Infrastructure

- NuGet package metadata and repository configuration are defined by
  `NuGet.Config`, `eng/SmartPipe.Package.props`, and
  `eng/SmartPipe.Package.targets`.
- CI is represented by the GitHub workflow badge/link in `README.md`; no
  deployment service is part of the runtime contract.
- Same-repository PR CI, CodeQL, and Dependency Review use standard
  GitHub-hosted runners; non-PR hosted routes remain intact. The retired
  SmartPipe self-hosted runner and its local root were removed. The workflow's
  YAML 1.2 oracle guards trigger shape, and lock-file-keyed NuGet caches keep
  Linux and Windows restore state separate.
- `ci.yml` supports an optional exact-SHA diagnostic path for one named
  consumer repeated 1-5 times. Full normal jobs are skipped in that mode and
  no diagnostic artifacts are uploaded. `run-consumers --scenario` is an
  internal selector; NativeAOT path exhaustion currently uses `SPCONS025`
  because `SPCONS024` is already an existing consumer-diagnostic contract.
- The servicing boundary is SDK `10.0.303` plus the approved Microsoft
  `10.0.11` cohort. RepositoryChecks keeps immutable baseline integrity mode
  separate from current-SDK/package validation.

## Important Technical Constraints

- Runtime processing is in-process and in-memory; there is no built-in durable
  queue, distributed coordination, crash replay, or exactly-once guarantee.
- Bounded channels and backpressure are deliberate behavior. Lossy modes can
  drop work and emit drop metrics/events.
- Core and JSON projects enable trim/AOT analyzers. Reflection-sensitive JSON
  APIs should use `JsonTypeInfo`; some broad extension integrations are not
  AOT-friendly.
- Definitions and component ownership are explicit. Factory-created graphs can
  be reused; instance components, observers, and dead-letter option objects are
  borrowed/single-use according to the runtime contracts.
- NuGet audit policy is already explicit and tested. A separate package-pruning
  checker is not planned; preserve SDK pruning diagnostics such as NU1510 and
  let existing package validation surface them.

## SP220-05 HealthChecks boundary

- `SmartPipe.Extensions.DependencyInjection` exposes immutable observation
  contracts and the singleton observation source; active state is read from
  the existing registry and terminal state retains only the latest value per
  case-sensitive `PipelineKey`.
- `SmartPipe.Extensions.HealthChecks` depends on Core, DependencyInjection,
  `Microsoft.Extensions.Diagnostics.HealthChecks`, Options, and DI
  abstractions, with no Hosting or facade dependency. Health evaluations take
  one captured observation and sanitize non-cancellation failures into bounded
  hard-failure results without exception payloads.
- The completed HealthChecks slice has warn-as-error builds, focused/full
  tests, package/consumer validation, trim/AOT coverage, and exact-head remote
  acceptance. Its Sonar gate was remediated with a regex timeout, structured
  registration locking/rollback, and named consumer namespaces. The DI
  disposal regression awaits the cancellation contract and asserts the single
  cancelled terminal observation/sequence.
- Aggregate evaluation snapshots `includeAll`, the included list, maximum,
  policy, and nested options before selection/capture/evaluation. Bounded
  aggregate descriptions report data/problem cardinality; per-pipeline
  sanitized descriptions may retain exact `PipelineKey` identity.

## SP220-07 leaf-package contracts

- `SmartPipe.Extensions.Channels` depends only on Core. `ChannelMerge` supports
  the existing pair overload and `MergeMany` for N readers, defensively
  snapshots bounded options, preserves per-reader order, cooperatively handles
  cancellation, and retains deterministic primary failures.
- `SmartPipe.Extensions.Transforms` depends only on Core. Composite transforms
  initialize once with reverse rollback/disposal, Conditional and Filter
  preserve short-circuit behavior, compression observes cancellation, and
  frozen rule validation is reflection-free.
- `SmartPipe.Extensions.Logging` depends on Core plus logging abstractions.
  The raw `LoggerSink<T>` constructor remains non-obsolete and compatible; the
  additive options path bounds formatted payloads, supports payload suppression
  or explicit unsafe raw logging, and does not invoke disabled formatters.
- `SmartPipe.Extensions.DataAnnotations` depends on Core and Transforms.
  `ValidationTransform<T>` retains non-recursive BCL validation and its
  reflection path is explicitly `RequiresUnreferencedCode`; value types use a
  single boxed instance, while `RuleValidationTransform<T>` is the
  reflection-free trim/NativeAOT path.
- The four leaves are represented in central package/ownership/consumer
  manifests and validated by direct, facade, binary, trim, and NativeAOT
  scenarios. The final exact contribution head passed CI `32583155237`,
  Dependency Review `32583155152`, CodeQL `32583155196`, SonarCloud, CodeRabbit,
  and all bounded cleanup jobs.

## SP220-08 JSON integration boundary

- The JSON leaf remains physically owned by `SmartPipe.Extensions.Json` and
  depends only on Core plus logging abstractions. New reusable definitions must
  compose existing JSON source, transform, sink, and dead-letter components
  through `PipelineComponent.RuntimeOwned`; no second JSON runtime or facade/
  DI dependency is allowed.
- The positive trim/NativeAOT path accepts resolver-backed `JsonTypeInfo`,
  privately clones and freezes serializer options, and re-resolves metadata
  without mutating caller-owned options or metadata. Reflection fallback is not
  part of the canonical path.
- Only the transport-neutral bounded UTF-8 line-framing primitive may be
  shared. File-specific probing, path diagnostics, JSON validation, and logging
  remain in the JSON package until a later HTTP.Json contract proves otherwise.
- `JsonPipelineComponents`, typed `JsonPipelineDefinitionBuilder`, and its
  extensions now compose existing JSON components through Core
  `RuntimeOwned` factories. Definition construction is resource-free; each
  accepted run receives fresh components and reverse cleanup.
- Resolver-backed `JsonTypeInfo` paths use a privately cloned/read-only
  `JsonSerializerOptions` snapshot and re-resolve metadata. File/dead-letter
  sources apply their JSON-owned depth/record policies; transform input/output
  contexts are snapshotted independently, and caller-owned metadata/options
  are not mutated.
- JSON framing is linked from internal `src/Shared/JsonFraming` and remains
  bounded, cancellable, BCL-only, and non-public. File probing, unframed input
  limits, validation, path diagnostics, and logging remain JSON-owned.
- The final JSON contract is covered by 11/11 focused definition tests,
  251/251 JSON tests, 12/12 framing tests, 21/21 BDN Dry cases, direct/DI/
  reflection-disabled trim/NativeAOT consumers, and package graph/ownership
  gates.


## SP220-09 CSV Integration (Paused)

- `SmartPipe.Extensions.Csv` directly references Core, CsvHelper 33.1.0, and
  logging abstractions; it does not reference the broad facade or other leaves.
  The facade intentionally retains its direct CsvHelper compatibility
  dependency through 2.2.
- The strict public surface is file-only: immutable source/sink options,
  `CsvMapRegistration<T>`, component factories, and typed definition
  builders. It uses decoded-character RFC4180 framing, bounded record/field/
  column limits, strict encoding/BOM handling, and boundary-only recovery.
- The three legacy CSV implementations retain their namespaces, constructors,
  defaults, and warning behavior in the leaf; facade type forwarders preserve
  source, binary, and reflection identities.
- Fresh affected-project evidence is 42/42; legacy source/sink and transform
  evidence is 14/14; JSON lifecycle evidence is 2/2 plus existing JSON
  definition contracts 11/11. Package graph, metadata, SourceLink/snupkg, and
  ownership checks report 19 graph packages (12 active, 7 planned), 12 packed
  packages, and 157 owned types.
- Direct, DI-composition, facade-source, and trimmed diagnostic consumers print
  `CONSUMER_OK` from the fresh package feed. The two named repository-runner
  facade scenarios stop before execution at `SPCONS019` because of three
  unrelated competitor-benchmark CPM violations; no blanket AOT claim is made.
