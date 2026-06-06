# API Reference

This reference is synchronized to the public API baselines for SmartPipe.Core
and SmartPipe.Extensions. Configuration details live in
[configuration.md](configuration.md); failure behavior lives in
[resilience.md](resilience.md).

## Legacy Runtime Contracts

### ISource<T>

Legacy source contract:

```csharp
Task InitializeAsync(CancellationToken ct = default);
IAsyncEnumerable<ProcessingContext<T>> ReadAsync(CancellationToken ct = default);
Task DisposeAsync();
```

### ITransformer<TInput,TOutput>

Legacy transformer contract:

```csharp
Task InitializeAsync(CancellationToken ct = default);
ValueTask<ProcessingResult<TOutput>> TransformAsync(
    ProcessingContext<TInput> ctx,
    CancellationToken ct = default);
Task DisposeAsync();
```

### ISink<T>

Legacy sink contract:

```csharp
Task InitializeAsync(CancellationToken ct = default);
Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default);
Task DisposeAsync();
```

### SmartPipeChannel<TInput,TOutput>

Compatibility runtime for legacy 1.x components. It supports adding sources,
transformers, and sinks before execution, `RunAsync`, `RunInBackground`,
`ProcessSingleAsync`, `DrainAsync`, `Cancel`, dashboard creation, and
`IAsyncDisposable`.

## Typed Runtime Contracts

### IPipelineSource<T>

Envelope-aware source contract:

```csharp
ValueTask InitializeAsync(CancellationToken ct = default);
IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
    CancellationToken ct = default);
```

### IPipelineTransformer<TInput,TOutput>

Envelope-aware transformer contract:

```csharp
ValueTask InitializeAsync(CancellationToken ct = default);
ValueTask<StageResult<TOutput>> TransformAsync(
    ProcessingEnvelope<TInput> envelope,
    CancellationToken ct = default);
```

### IPipelineSink<T>

Envelope-aware sink contract:

```csharp
ValueTask InitializeAsync(CancellationToken ct = default);
ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default);
```

## Envelope And Result Types

### ProcessingEnvelope<T>

Carries typed payload plus run metadata:

- `Payload`;
- `TraceId`;
- `PipelineId`;
- `RunId`;
- `CreatedAtUtc`;
- `Attempt`;
- `Metadata`;
- `Lineage`.

Use `FromContext` and `ToContext` to bridge legacy contexts.

Factory helpers:

```csharp
var envelope = ProcessingEnvelope<Order>.Create(order);

var explicitEnvelope = ProcessingEnvelope<Order>.Create(
    order,
    pipelineId: "orders-sync",
    runId: "run-001",
    traceId: 123);

var legacyEnvelope = ProcessingEnvelope<Order>.FromContext(
    legacyContext,
    SystemPipelineClock.Instance,
    pipelineId: "legacy",
    runId: "legacy-run");
```

### StageResult<T>

Typed stage result with:

- `Kind`;
- `Value`;
- `Error`;
- `IsSuccess`;
- `IsValid`;
- `ToProcessingResult(traceId)`.

Factory methods: `Success`, `Failure`, `Filtered`, `Skipped`, `Cancelled`,
`TimedOut`, and `FromProcessingResult`.

### StageResultKind

Values: `Success`, `Failure`, `Filtered`, `Skipped`, `Cancelled`, `TimedOut`.

### PipelineOutput<T>

Typed run output containing:

- `Envelope`;
- `Result`.

### PipelineRun<T>

Run handle returned by typed `PipelineBuilder` APIs:

- `Outputs`;
- `Completion`;
- `State`;
- `ReadResultsAsync`;
- `CancelAsync`;
- `DrainAsync`;
- `AbortAsync`;
- `DisposeAsync`.

## Builder And Runtime Types

### PipelineBuilder

Entry points:

```csharp
PipelineBuilder.From(ISource<T> source);
PipelineBuilder.From(IPipelineSource<T> source);
PipelineBuilder.FromFactory(
    Func<IServiceProvider?, IPipelineSource<T>> sourceFactory,
    IServiceProvider? serviceProvider = null);
```

Legacy chains use `ITransformer` and finish with `To(ISink<T>)`, which returns a
`Task`.

Typed chains use `IPipelineTransformer` and finish with `Run()`,
`To(IPipelineSink<T>)`, or `ToFactory(...)`, each returning `PipelineRun<T>`.

Typed identity and runtime options are additive:

```csharp
var run = PipelineBuilder
    .From(source)
    .WithPipelineId("orders-sync")
    .WithRuntimeOptions(new PipelineRuntimeOptions
    {
        OutputCapacity = 1024,
        Clock = SystemPipelineClock.Instance,
        ObserverDispatch = ObserverDispatchOptions.Inline,
    })
    .Transform(transformer)
    .Run();
```

If `WithPipelineId` is not called, current generated pipeline id behavior is
preserved. If `WithRuntimeOptions` is not called, output buffering, observer
dispatch, clock, retry, sink, and circuit breaker defaults are preserved.

### PipelineRuntimeOptions

Runtime options:

- `OutputCapacity`;
- `OutputFullMode`;
- `ObserverDispatch`;
- `Clock`.

`IPipelineClock` exposes `GetUtcNow`, `GetTimestamp`, and `GetElapsedTime`.
`SystemPipelineClock` is the default. `TimeProviderPipelineClock` adapts a .NET
`TimeProvider`.

`ObserverDispatchOptions` supports `Inline`, `BufferedBestEffort`, and
`BufferedReliable`. Inline is the default. `BufferedReliable` requires
`FlushOnCompletion = true`.

### PipelineDefinition

Declarative topology containing pipeline id, component registrations, stage
definitions, ownership options, lineage mode, and reusability state.

### PipelineExecutionPlan

Compiled and validated execution plan:

```csharp
PipelineExecutionPlan.Compile(PipelineDefinition definition);
```

### PipelineRuntime

Single-use runtime owner with `ExecutionPlan` and `RunId`.

Typed `PipelineRun<T>.DrainAsync` requests source-boundary drain, completes
already accepted work, and waits for the run task until completion, timeout, or
external cancellation.

## Failure And Resilience Types

### StageFailureOptions

Typed stage policy:

- `Retry`;
- `Timeout`;
- `CircuitBreaker`;
- `OnPermanentFailure`;
- `OnRetryExhausted`;
- `Default`.

### FailureAction

Values: `EmitFailureResult`, `DeadLetter`, `Skip`, `StopPipeline`,
`FaultPipeline`.

### TimeoutPolicy

Properties:

- `AttemptTimeout`;
- `StageTimeout`.

### CircuitBreakerPolicy

Properties:

- `EvaluationMode`;
- `FailureRatio`;
- `SamplingDuration`;
- `MinimumThroughput`;
- `MaxHalfOpenRequests`;

- `FailureThreshold`;
- `BreakDuration`.

`CircuitBreakerEvaluationMode` values are `CompatibilityThreshold` and
`FailureRatio`. `CompatibilityThreshold` is the default; `FailureRatio` is an
opt-in sampling mode.

### RetryQueueOverflowPolicy

Values: `Wait`, `FailFast`, `DeadLetter`, `DropNewest`, `DropOldest`.

## Dead Letter Types

### DeadLetterEnvelope<T>

Replay-safe envelope containing:

- `SchemaVersion`;
- `PipelineId`;
- `RunId`;
- `TraceId`;
- `StageId`;
- `StageName`;
- `OriginalPayload`;
- `Metadata`;
- `Error`;
- `Attempt`;
- `FailedAtUtc`.

### IDeadLetterSerializer<T>

Writes and reads `DeadLetterEnvelope<T>` values:

```csharp
ValueTask WriteAsync(DeadLetterEnvelope<T> envelope, Stream stream, CancellationToken ct = default);
IAsyncEnumerable<DeadLetterEnvelope<T>> ReadAsync(Stream stream, CancellationToken ct = default);
```

### IDeadLetterRedactor<T>

Redacts a `DeadLetterEnvelope<T>` before persistence.

### JsonLinesDeadLetterSerializer<T>

JSON Lines serializer for modern dead-letter envelopes. Constructors accept
either `JsonSerializerOptions` or
`JsonTypeInfo<DeadLetterEnvelope<T>>`. Use the `JsonTypeInfo` overload for trim
and NativeAOT scenarios.

## Metadata And Lineage

### MetadataBag

Immutable-style string metadata bag with `Empty`, `From`, `Set`, `Contains`,
`GetString`, `Items`, `ToDictionary`, and `AsReadOnlyDictionary`.

### LineageEntry

Lineage record with stage id/name, input/output type names, start/completion
timestamps, and `StageOutcome`.

### PipelineComponentLifetime

Values: `SingleUse`, `Reusable`, `SingletonExternal`.

### IPipelineComponentDescriptor

Components can declare `Lifetime` and `OwnsResources`.

## Observer Events

### IPipelineObserver

Observer contract:

```csharp
ValueTask OnEventAsync(PipelineEvent pipelineEvent, CancellationToken ct = default);
```

### PipelineEvent

Base event fields: `PipelineId`, `RunId`, `TraceId`, `StageId`, `Attempt`,
`TimestampUtc`.

Current event family includes run lifecycle, stage start/success/failure, retry
scheduled/attempted/exhausted, sink write start/failure, dead-letter written,
circuit breaker opened/rejected, and observer failure events.

Observer configuration uses `PipelineObserverRegistration`,
`ObserverReliability`, and `ObserverFailurePolicy`.

## Legacy Adapters

- `LegacySourceAdapter<T>` adapts `ISource<T>` to `IPipelineSource<T>`.
- `LegacyTransformerAdapter<TInput,TOutput>` adapts `ITransformer` to
  `IPipelineTransformer`.
- `LegacySinkAdapter<T>` adapts `ISink<T>` to `IPipelineSink<T>`.

## Extensions Highlights

### HttpSelector<T>

Constructor:

```csharp
new HttpSelector<T>(
    HttpClient httpClient,
    string requestUri,
    ResiliencePipeline? pipeline = null,
    ILogger<HttpSelector<T>>? logger = null);
```

### Common Sources, Transforms, And Sinks

Sources: `HttpSelector<T>`, `JsonFileSource<T>`, `CsvFileSource<T>`,
`EfCoreSelector<T>`, `DapperSelector<T>`, `DeadLetterSource<T>`.

Transforms: `JsonTransform<TInput,TOutput>`, `CsvTransform<TInput,TOutput>`,
`MapsterTransform<TInput,TOutput>`, `CompressionTransform`,
`FilterTransform<T>`, `ConditionalTransform<T>`, `CompositeTransform<T>`,
`ValidationTransform<T>`, `PollyResilienceTransform<T>`.

Sinks: `LoggerSink<T>`, `HttpSink<T>`, `JsonFileSink<T>`, `CsvFileSink<T>`,
`DbSink<T>`, `DeadLetterSink<T>`.

### JsonTypeInfo Overloads

The Extensions API includes 1.1.0 source-generated metadata overloads:

```csharp
new JsonTransform<TInput,TOutput>(
    JsonTypeInfo<TInput> inputTypeInfo,
    JsonTypeInfo<TOutput> outputTypeInfo);

new JsonFileSource<T>(
    string path,
    JsonTypeInfo<List<T>> listTypeInfo,
    JsonTypeInfo<T> itemTypeInfo);

new JsonFileSink<T>(
    string path,
    JsonTypeInfo<List<T>> batchTypeInfo,
    int flushInterval);

new DeadLetterSource<T>(string path, JsonTypeInfo<T> valueTypeInfo);

new DeadLetterSink<T>(
    string path,
    JsonTypeInfo<ProcessingResult<T>> resultTypeInfo,
    ILogger<DeadLetterSink<T>>? logger = null,
    Stream? stream = null);
```

These overloads are accepted into the `1.1.0` public API baseline and are
recorded in `src/SmartPipe.Extensions/PublicAPI.Shipped.txt`. Accepted Core
runtime-option APIs are recorded in `src/SmartPipe.Core/PublicAPI.Shipped.txt`.
Core and Extensions `PublicAPI.Unshipped.txt` files contain no public entries
beyond `#nullable enable` for stable 1.1.0.
