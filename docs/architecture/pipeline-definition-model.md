# Pipeline Definition Model

SmartPipe.Core 2.2 introduces the canonical immutable definition model. The
current layer covers identity, activation inputs, component ownership, typed
fluent construction, structural validation, resource-free compilation, per-run
component activation, readiness-aware startup, and the sole legacy-builder
adapter into that same compiler, activator, and executor path.

## Identity

`PipelineKey` identifies one definition and `PipelineStageKey` identifies one
stage. Both values preserve the supplied string exactly and use ordinal,
case-sensitive equality. Null, empty, and whitespace-only strings are rejected.
The default value is an invalid sentinel and is rejected at API boundaries.

## Activation Context

`PipelineActivationContext` carries one exact pipeline key, one non-empty run
identifier, an optional `IServiceProvider`, and a non-null `TimeProvider`.
Omitting the provider selects `TimeProvider.System`, while Core retains an
internal distinction between an omitted provider and an explicitly supplied
`TimeProvider.System`.

The service provider and time provider are borrowed dependencies. Core neither
creates a DI scope here nor owns or disposes either instance.

## Component Ownership

Components enter the canonical model through exactly three factory methods:

| Route | Created per run | Initialized by Core | Disposed by Core |
|---|---:|---:|---:|
| `PipelineComponent.RuntimeOwned(factory)` | Yes | Yes | Yes |
| `PipelineComponent.ScopeOwned(factory)` | Yes | Yes | No |
| `PipelineComponent.Borrowed(instance, initialize)` | No | Opt-in | No |

Factories are retained without invocation until activation. A borrowed instance
is externally owned and will make its eventual definition single-use; reusable
definitions use per-run factories. The descriptor is sealed and has no public
constructor, so callers cannot bypass these lifecycle routes.

The shipped `PipelineComponentLifetime` and related non-generic metadata remain
the compatibility API. They are not aliases for the canonical ownership enum.

## Defensive Options

Public option objects remain source-compatible init-only types. Definition
finalization uses internal immutable snapshots instead of retaining caller-owned
option objects. Snapshot creation validates first, then explicitly copies every
scalar, enum, duration, nested policy, and compatibility flag. Retry and error
classifier delegates are intentionally preserved as external behavior
references; service or runtime resource instances are not captured.

Clock selection for a run follows one order:

1. An explicitly supplied activation-context `TimeProvider` is adapted to
   `IPipelineClock`.
2. Otherwise, an explicitly configured `PipelineRuntimeOptions.Clock` instance
   is preserved exactly.
3. Otherwise, `SystemPipelineClock.Instance` is used.

The resolved clock is produced once by activation and is the clock the later
runtime graph must share. The snapshot path contains no reflection; reflection
is used only by a guard test that detects newly added, unmapped option properties.

## Typed Definitions

`PipelineDefinitionBuilder.From` requires an explicit `PipelineKey` and a typed
source descriptor. Every fluent call returns a new builder state. Transforms
preserve compile-time input/output adjacency, append an exact copied descriptor
array, and never invoke component factories. `Build()` finalizes a sinkless
definition; terminal `To()` finalizes one with a typed sink.

Finalization validates the pipeline key, stage keys and type adjacency, duplicate
stage keys, option snapshots, lineage mode, and observer policies. Stage metadata
contains only the exact key/name, input/output types, and a defensive failure-policy
copy. A null stage name materializes as the exact stage-key value; an explicit
whitespace-only name is rejected and other text is preserved without trimming.
The public stage list is a read-only collection over a private copied array.

`IsReusable` is true only when the source, every transformer, and an optional sink
are per-run descriptors and no borrowed observer or dead-letter configuration is
present. A `StageDeadLetterOptions<T>` retains a caller-owned stream, serializer,
and redactor, so its presence forces single-use. Core never disposes those
dead-letter resources.

## Resource-Free Compilation

Compilation repeats the structural validator as an internal trust boundary and
produces only descriptors, copied observer registrations, option snapshots, and
derived lifecycle flags. It creates no component instance, channel, timer, task,
scope, or other runtime resource. Each definition caches either one execution
plan or one compilation failure with `Lazy<T>` using
`LazyThreadSafetyMode.ExecutionAndPublication`, so concurrent access observes the
same published result.

## Per-Run Activation

Activation validates the context and cached plan before claiming a single-use
definition. A pre-cancelled request, a mismatched key, an empty run identifier,
or missing required services therefore creates no resources and does not consume
the claim. Once a non-reusable definition is claimed, cancellation or failure
does not make it reusable again.

For each source, stage, and optional sink, activation invokes the factory,
records the resulting ownership lease, and only then initializes the instance
when requested by its descriptor. The activated graph retains the live instances;
the ledger retains only role, ownership, stage identity, and the runtime-owned
cleanup callback. Reusable definitions create an isolated graph and ledger for
every activation.

Partial activation rolls back leases in reverse creation order. Runtime-owned
components are disposed; scope-owned and externally owned components remain in
the ordered ledger but are never disposed by Core. Cleanup is best-effort and
idempotent: concurrent rollback and disposal callers share one cleanup task, and
all cleanup callbacks are attempted even after an earlier callback fails.

When rollback succeeds, the original factory, initialization, or cancellation
exception is rethrown with its identity and stack preserved. When rollback also
fails, `PipelineActivationException` exposes the pipeline key, run identifier,
the original failure as `InnerException`, and a copied read-only cleanup-error
list in reverse cleanup-attempt order.

## Owned Startup And Readiness

`StartAsync` creates one owned operation after pure validation and any required
single-use claim. The operation owns one output channel, activation cancellation
source, lifecycle task, readiness signal, and `PipelineRun<TOutput>` shell. It
invokes activation directly as a hot async operation; it does not add a second
runtime path or wrap activation in `Task.Run`.

After activation, the existing typed executor attaches to that same output
channel and activated graph. Readiness completes only after the executor state
is `Running` and `PipelineStartedEvent` has either completed inline dispatch or
been accepted by buffered dispatch. A fast source may already have reached a
terminal state when `StartAsync` resumes, but the returned run is never
`NotStarted` or activation-in-progress.

Before readiness, the deferred run shell already exposes its output reader and
owns cancel, abort, drain, and disposal requests. Cancel and abort stop activation;
dispose waits for rollback; drain waits for executor attachment or receives the
startup failure. Startup failures are observed through the same lifecycle task
after owned cleanup, so canonical callers do not leave a secondary unobserved
fault.

Runtime-created runs expose the exact definition `PipelineKey` and activation
`Guid RunId`. Manually constructed compatibility handles retain default identity.
Each run handle caches one disposal task; concurrent callers invoke its supplied
cleanup delegate once and observe the same success or failure. `WithLifetime`
preserves identity and keeps its shipped replacement-disposal semantics.

## Legacy Builder Adapter

`PipelineBuilder` retains its shipped signatures but creates generic definitions
through one internal `LegacyPipelineDefinitionAdapter`. Instance source, stage,
sink, and observer registrations make the builder single-use regardless of a
legacy `Reusable` or `SingletonExternal` lifetime label. The shared definition
claim rejects a second or concurrent start before activation. Factory routes are
lazy runtime-owned descriptors and remain sequentially and concurrently reusable.

Legacy stage keys remain `stage-1` through `stage-N`. An explicit pipeline ID is
used verbatim and forces envelope IDs; without one, each factory run receives a
generated definition key while an existing non-empty source envelope ID is
preserved. The adapter calls `StartDeferred` and returns its run immediately, so
factory and initialization failures are observed through `Completion` after
ordered activation rollback.

The shipped non-generic `PipelineDefinition` and `PipelineExecutionPlan` remain
metadata compatibility objects rather than a second runtime. Their component and
stage collections are defensive read-only copies, and compilation uses the same
ordinal duplicate-ID and adjacent-type topology validator as the generic model.

## Benchmark Contract

`PipelineDefinitionBenchmarks` measures zero-, one-, and ten-stage build,
first/cached compilation, canonical start-to-disposal, and the one-stage legacy
adapter path. First-compilation cases use a fresh definition in `IterationSetup`;
cached cases reuse an already published plan. Factories perform no I/O, every run
drains outputs and awaits completion and disposal, and `GlobalSetup` rejects an
incorrect output before measurement. The benchmark project has internal access
only so it can measure the internal compiler without widening the shipped API.

Benchmark results are advisory. A comparison must record the commit SHA, OS and
CPU, .NET SDK/runtime, BenchmarkDotNet job/configuration, raw artifact, and
allocation shape from the same environment. No absolute timing or percentage
threshold is a correctness gate; deterministic tests prove compile-once,
resource-free compilation, activation order, and bounded cleanup counts.
