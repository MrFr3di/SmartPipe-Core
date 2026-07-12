#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class TypedPipelineSpec<TInput, TOutput>
{
    private readonly IReadOnlyList<ITypedPipelineStage> _stages;
    private int _runtimeCreated;

    public TypedPipelineSpec(
        string pipelineId,
        IPipelineSource<TInput> source,
        IReadOnlyList<ITypedPipelineStage> stages,
        ComponentOwnershipOptions? ownershipOptions = null,
        LineageMode lineageMode = LineageMode.Minimal,
        bool isFactoryBased = false,
        IEnumerable<PipelineObserverRegistration>? observers = null,
        PipelineRuntimeOptions? runtimeOptions = null,
        bool forcePipelineId = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        runtimeOptions ??= new PipelineRuntimeOptions();
        runtimeOptions.Validate();
        PipelineId = pipelineId;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        OwnershipOptions = ownershipOptions ?? ComponentOwnershipOptions.Default;
        LineageMode = lineageMode;
        IsFactoryBased = isFactoryBased;
        Observers = (observers ?? []).ToArray();
        RuntimeOptions = runtimeOptions;
        ForcePipelineId = forcePipelineId;
    }

    public string PipelineId { get; }

    public IPipelineSource<TInput> Source { get; }

    public ComponentOwnershipOptions OwnershipOptions { get; }

    public LineageMode LineageMode { get; }

    public IReadOnlyList<ITypedPipelineStage> Stages => _stages;

    public bool IsFactoryBased { get; }

    public IReadOnlyList<PipelineObserverRegistration> Observers { get; }

    public PipelineRuntimeOptions RuntimeOptions { get; }

    public bool ForcePipelineId { get; }

    public bool IsReusable =>
        IsFactoryBased
        || IsComponentReusable(Source)
            && _stages.All(stage => IsComponentReusable(stage.Component));

    public TypedPipelineSpec<TInput, TNext> AddStage<TNext>(
        IPipelineTransformer<TOutput, TNext> transformer,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TOutput>? deadLetterOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(transformer);
        var stages = _stages
            .Concat([
                new TypedPipelineStage<TOutput, TNext>(
                    transformer,
                    _stages.Count + 1,
                    failureOptions,
                    deadLetterOptions
                ),
            ])
            .ToArray();

        return new TypedPipelineSpec<TInput, TNext>(
            PipelineId,
            Source,
            stages,
            OwnershipOptions,
            LineageMode,
            IsFactoryBased,
            Observers,
            RuntimeOptions,
            ForcePipelineId
        );
    }

    public TypedPipelineSpec<TInput, TOutput> WithObserver(PipelineObserverRegistration observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new TypedPipelineSpec<TInput, TOutput>(
            PipelineId,
            Source,
            _stages,
            OwnershipOptions,
            LineageMode,
            IsFactoryBased,
            Observers.Concat([observer]),
            RuntimeOptions,
            ForcePipelineId
        );
    }

    public TypedPipelineSpec<TInput, TOutput> WithPipelineId(string pipelineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        return new TypedPipelineSpec<TInput, TOutput>(
            pipelineId,
            Source,
            _stages,
            OwnershipOptions,
            LineageMode,
            IsFactoryBased,
            Observers,
            RuntimeOptions,
            forcePipelineId: true
        );
    }

    public TypedPipelineSpec<TInput, TOutput> WithRuntimeOptions(PipelineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new TypedPipelineSpec<TInput, TOutput>(
            PipelineId,
            Source,
            _stages,
            OwnershipOptions,
            LineageMode,
            IsFactoryBased,
            Observers,
            options,
            ForcePipelineId
        );
    }

    public PipelineDefinition CreateDefinition(
        IPipelineSink<TOutput>? sink,
        bool sinkIsFactoryBased = false
    )
    {
        MarkRuntimeCreated(sink);
        var components = new List<PipelineComponentRegistration>
        {
            DescribeComponent(Source, IsFactoryBased),
        };

        foreach (var stage in _stages)
            components.Add(DescribeComponent(stage.Component, IsFactoryBased));

        if (sink is not null)
            components.Add(DescribeComponent(sink, sinkIsFactoryBased));

        return new PipelineDefinition(
            PipelineId,
            RuntimeOptions,
            components,
            _stages.Select(stage => new PipelineStageDefinition(
                stage.StageId,
                stage.StageName,
                stage.InputType,
                stage.OutputType,
                stage.FailureOptions
            )),
            OwnershipOptions,
            LineageMode
        );
    }

    private static PipelineComponentRegistration DescribeComponent(
        object component,
        bool isFactoryBased
    )
    {
        var descriptor = component as IPipelineComponentDescriptor;
        return new PipelineComponentRegistration(
            component.GetType(),
            descriptor?.Lifetime ?? PipelineComponentLifetime.SingleUse,
            descriptor?.OwnsResources ?? true,
            isFactoryBased
        );
    }

    private void MarkRuntimeCreated(IPipelineSink<TOutput>? sink)
    {
        var sinkReusable = sink is null || IsComponentReusable(sink);
        if (IsReusable && sinkReusable)
            return;

        if (Interlocked.Exchange(ref _runtimeCreated, 1) == 1)
        {
            throw new InvalidOperationException(
                "This pipeline definition contains single-use component instances and cannot create multiple runtimes. "
                    + "Use factory-based registration or components that declare reusable lifetime."
            );
        }
    }

    private static bool IsComponentReusable(object component)
    {
        return component
            is IPipelineComponentDescriptor
        {
            Lifetime: PipelineComponentLifetime.Reusable
                    or PipelineComponentLifetime.SingletonExternal
        };
    }
}

internal interface ITypedPipelineStage
{
    string StageId { get; }

    string StageName { get; }

    Type InputType { get; }

    Type OutputType { get; }

    object Component { get; }

    StageFailureOptions FailureOptions { get; }

    TypedStageCorrelation GetCorrelation(object envelope);

    ProcessingEnvelope<object> BoxEnvelope(object envelope);

    object WithAttempt(object envelope, int attempt);

    ValueTask InitializeAsync(CancellationToken ct);

    ValueTask<TypedStageExecutionResult> ExecuteAsync(
        object envelope,
        LineageMode lineageMode,
        IPipelineClock clock,
        CancellationToken ct
    );

    TypedStageExecutionResult CreateTimedOutResult(
        object envelope,
        LineageMode lineageMode,
        IPipelineClock clock,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout,
        Exception? exception,
        bool canRetryTimeout
    );

    TypedStageExecutionResult CreateFailureResult(
        object envelope,
        SmartPipeError error,
        StageResultKind kind,
        LineageMode lineageMode,
        IPipelineClock clock,
        DateTimeOffset startedAtUtc
    );

    ValueTask<DeadLetterWriteResult> WriteDeadLetterAsync(
        object envelope,
        SmartPipeError error,
        IPipelineClock clock,
        CancellationToken ct
    );

    ValueTask DisposeAsync(ComponentOwnershipOptions ownershipOptions);
}

internal sealed class TypedPipelineStage<TInput, TOutput> : ITypedPipelineStage
{
    private readonly IPipelineTransformer<TInput, TOutput> _transformer;
    private readonly StageDeadLetterOptions<TInput>? _deadLetterOptions;

    public TypedPipelineStage(
        IPipelineTransformer<TInput, TOutput> transformer,
        int index,
        StageFailureOptions? failureOptions = null,
        StageDeadLetterOptions<TInput>? deadLetterOptions = null
    )
    {
        _transformer = transformer ?? throw new ArgumentNullException(nameof(transformer));
        FailureOptions = failureOptions ?? StageFailureOptions.Default;
        _deadLetterOptions = deadLetterOptions;
        StageId = $"stage-{index}";
        StageName = transformer.GetType().Name;
    }

    public string StageId { get; }

    public string StageName { get; }

    public Type InputType => typeof(TInput);

    public Type OutputType => typeof(TOutput);

    public object Component => _transformer;

    public StageFailureOptions FailureOptions { get; }

    public TypedStageCorrelation GetCorrelation(object envelope)
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        return new TypedStageCorrelation(input.TraceId, input.Attempt);
    }

    public ProcessingEnvelope<object> BoxEnvelope(object envelope)
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        return new ProcessingEnvelope<object>
        {
            PipelineId = input.PipelineId,
            RunId = input.RunId,
            TraceId = input.TraceId,
            Payload = input.Payload!,
            Metadata = input.Metadata,
            Lineage = input.Lineage,
            Attempt = input.Attempt,
            CreatedAtUtc = input.CreatedAtUtc,
        };
    }

    public object WithAttempt(object envelope, int attempt)
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        return input with { Attempt = attempt };
    }

    public ValueTask InitializeAsync(CancellationToken ct) => _transformer.InitializeAsync(ct);

    public async ValueTask<TypedStageExecutionResult> ExecuteAsync(
        object envelope,
        LineageMode lineageMode,
        IPipelineClock clock,
        CancellationToken ct
    )
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        var started = clock.GetUtcNow();
        using var activity = SmartPipeActivitySource.Source.StartActivity("Transform", ActivityKind.Internal);
        activity?.SetTag("smartpipe.pipeline_id", input.PipelineId);
        activity?.SetTag("smartpipe.run_id", input.RunId);
        activity?.SetTag("smartpipe.trace_id", input.TraceId);
        activity?.SetTag("smartpipe.stage_id", StageId);
        activity?.SetTag("smartpipe.stage_name", StageName);

        StageResult<TOutput> result;
        try
        {
            result = await _transformer.TransformAsync(input, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var failedAt = clock.GetUtcNow();
            var failedLineage = AppendLineage(
                input.Lineage,
                lineageMode,
                started,
                failedAt,
                StageOutcome.Failed,
                includeForError: true);
            var error = ClassifyException(ex);
            return TypedStageExecutionResult.Terminal(
                error,
                StageResultKind.Failure,
                input.TraceId,
                input.Attempt,
                failedLineage);
        }

        if (!result.IsValid)
            throw new InvalidOperationException(
                "default(StageResult<T>) is invalid. Use StageResult factory methods."
            );

        var completed = clock.GetUtcNow();
        if (!result.IsSuccess)
        {
            if (result.IsFailure)
                activity?.SetStatus(ActivityStatusCode.Error, result.Error?.Message ?? result.Kind.ToString());

            var failedLineage = AppendLineage(
                input.Lineage,
                lineageMode,
                started,
                completed,
                ToOutcome(result.Kind),
                includeForError: result.IsFailure
            );
            return TypedStageExecutionResult.Terminal(
                result.Error,
                result.Kind,
                input.TraceId,
                input.Attempt,
                failedLineage
            );
        }

        var next = new ProcessingEnvelope<TOutput>
        {
            PipelineId = input.PipelineId,
            RunId = input.RunId,
            TraceId = input.TraceId,
            Payload = result.Value!,
            Metadata = input.Metadata,
            Lineage = AppendLineage(
                input.Lineage,
                lineageMode,
                started,
                completed,
                StageOutcome.Succeeded,
                includeForError: false
            ),
            Attempt = 0,
            CreatedAtUtc = input.CreatedAtUtc,
        };

        activity?.SetStatus(ActivityStatusCode.Ok);
        return TypedStageExecutionResult.Success(next);
    }

    private SmartPipeError ClassifyException(Exception exception)
    {
        if (FailureOptions.ExceptionClassifier is not { } classifier)
        {
            return new SmartPipeError(
                exception.Message,
                ErrorType.Permanent,
                "StageException",
                exception);
        }

        return classifier(exception);
    }

    public TypedStageExecutionResult CreateTimedOutResult(
        object envelope,
        LineageMode lineageMode,
        IPipelineClock clock,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout,
        Exception? exception,
        bool canRetryTimeout
    )
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        var completed = clock.GetUtcNow();
        var lineage = AppendLineage(
            input.Lineage,
            lineageMode,
            startedAtUtc,
            completed,
            StageOutcome.TimedOut,
            includeForError: true
        );
        var error = new SmartPipeError(
            $"Stage attempt timed out after {timeout}.",
            ErrorType.Transient,
            "Timeout",
            exception
        );

        return TypedStageExecutionResult.Terminal(
            error,
            StageResultKind.TimedOut,
            input.TraceId,
            input.Attempt,
            lineage,
            canRetryTimeout
        );
    }

    public TypedStageExecutionResult CreateFailureResult(
        object envelope,
        SmartPipeError error,
        StageResultKind kind,
        LineageMode lineageMode,
        IPipelineClock clock,
        DateTimeOffset startedAtUtc
    )
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        var completed = clock.GetUtcNow();
        var lineage = AppendLineage(
            input.Lineage,
            lineageMode,
            startedAtUtc,
            completed,
            ToOutcome(kind),
            includeForError: true
        );
        return TypedStageExecutionResult.Terminal(
            error,
            kind,
            input.TraceId,
            input.Attempt,
            lineage
        );
    }

    public async ValueTask<DeadLetterWriteResult> WriteDeadLetterAsync(
        object envelope,
        SmartPipeError error,
        IPipelineClock clock,
        CancellationToken ct
    )
    {
        if (_deadLetterOptions is null)
            throw new InvalidOperationException(
                $"Stage '{StageName}' is configured with FailureAction.DeadLetter but has no StageDeadLetterOptions."
            );

        var input = (ProcessingEnvelope<TInput>)envelope;
        var deadLetter = new DeadLetterEnvelope<TInput>
        {
            SchemaVersion = 1,
            PipelineId = input.PipelineId,
            RunId = input.RunId,
            TraceId = input.TraceId,
            StageId = StageId,
            StageName = StageName,
            OriginalPayload = input.Payload,
            Metadata = input.Metadata,
            Error = error,
            Attempt = input.Attempt,
            FailedAtUtc = clock.GetUtcNow(),
        };

        var redacted = _deadLetterOptions.Redactor.Redact(deadLetter);
        await _deadLetterOptions
            .Serializer.WriteAsync(redacted, _deadLetterOptions.Stream, ct)
            .ConfigureAwait(false);
        await _deadLetterOptions.Stream.FlushAsync(ct).ConfigureAwait(false);
        return new DeadLetterWriteResult(input.TraceId, input.Attempt, StageId, StageName);
    }

    public ValueTask DisposeAsync(ComponentOwnershipOptions ownershipOptions)
    {
        if (!ShouldDispose(_transformer, ownershipOptions))
            return ValueTask.CompletedTask;

        return _transformer.DisposeAsync();
    }

    private IReadOnlyList<LineageEntry> AppendLineage(
        IReadOnlyList<LineageEntry> current,
        LineageMode lineageMode,
        DateTimeOffset started,
        DateTimeOffset completed,
        StageOutcome outcome,
        bool includeForError
    )
    {
        if (
            lineageMode == LineageMode.Off
            || (lineageMode == LineageMode.ErrorsOnly && !includeForError)
        )
        {
            return current;
        }

        var next = new LineageEntry(
            StageId,
            StageName,
            typeof(TInput).FullName ?? typeof(TInput).Name,
            typeof(TOutput).FullName ?? typeof(TOutput).Name,
            started,
            lineageMode == LineageMode.Full ? completed : null,
            outcome
        );

        return current.Count == 0 ? [next] : current.Concat([next]).ToArray();
    }

    private static StageOutcome ToOutcome(StageResultKind kind)
    {
        return kind switch
        {
            StageResultKind.Cancelled => StageOutcome.Cancelled,
            StageResultKind.TimedOut => StageOutcome.TimedOut,
            StageResultKind.Skipped => StageOutcome.Skipped,
            StageResultKind.Filtered => StageOutcome.Filtered,
            _ => StageOutcome.Failed,
        };
    }

    private static bool ShouldDispose(object component, ComponentOwnershipOptions ownershipOptions)
    {
        if (component is not IPipelineComponentDescriptor descriptor)
            return true;

        return descriptor.Lifetime != PipelineComponentLifetime.SingletonExternal
            || ownershipOptions.DisposeExternalComponents;
    }
}

internal readonly record struct TypedStageExecutionResult(
    bool IsSuccess,
    object? Envelope,
    SmartPipeError? Error,
    StageResultKind Kind,
    ulong TraceId,
    int Attempt,
    IReadOnlyList<LineageEntry>? Lineage,
    bool CanRetryTimeout
)
{
    public bool IsTerminalNonFailure =>
        Kind is StageResultKind.Filtered or StageResultKind.Skipped;

    public bool IsFailure =>
        Kind is StageResultKind.Failure
            or StageResultKind.Cancelled
            or StageResultKind.TimedOut;

    public static TypedStageExecutionResult Success(object envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new TypedStageExecutionResult(
            true,
            envelope,
            null,
            StageResultKind.Success,
            0,
            0,
            null,
            false
        );
    }

    public static TypedStageExecutionResult Terminal(
        SmartPipeError? error,
        StageResultKind kind,
        ulong traceId,
        int attempt,
        IReadOnlyList<LineageEntry> lineage,
        bool canRetryTimeout = false
    )
    {
        return new TypedStageExecutionResult(
            false,
            null,
            error,
            kind,
            traceId,
            attempt,
            lineage,
            canRetryTimeout);
    }
}

internal readonly record struct TypedStageCorrelation(ulong TraceId, int Attempt);

internal readonly record struct DeadLetterWriteResult(
    ulong TraceId,
    int Attempt,
    string StageId,
    string StageName
);

internal static class RuntimeCleanup
{
    internal static async ValueTask<Exception[]> CollectAsync(IEnumerable<Func<ValueTask>> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        List<Exception>? errors = null;
        foreach (var action in actions)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        return errors?.ToArray() ?? [];
    }

    internal static void ThrowCombined(
        ExceptionDispatchInfo? primary,
        IReadOnlyList<Exception> cleanupErrors)
    {
        ArgumentNullException.ThrowIfNull(cleanupErrors);

        if (primary is null)
        {
            if (cleanupErrors.Count == 0)
                return;

            if (cleanupErrors.Count == 1)
                ExceptionDispatchInfo.Capture(cleanupErrors[0]).Throw();

            throw new AggregateException(cleanupErrors);
        }

        if (cleanupErrors.Count == 0)
            primary.Throw();

        var errors = new Exception[cleanupErrors.Count + 1];
        errors[0] = primary.SourceException;
        for (var i = 0; i < cleanupErrors.Count; i++)
            errors[i + 1] = cleanupErrors[i];

        throw new AggregateException(errors);
    }
}

internal sealed class TypedPipelineExecutor<TInput, TOutput> : IAsyncDisposable
{
    private enum ExecutorLifecycleState
    {
        Created = 0,
        Running = 1,
        Disposing = 2,
        Disposed = 3,
    }

    private enum TimedAttemptCompletionKind
    {
        StageResultReturned = 0,
        ExpectedTimeoutCancellation = 1,
        Faulted = 2,
        StillRunning = 3,
        CallerCancelled = 4,
    }

    private readonly record struct TerminalOutcome(
        PipelineRunState State,
        ExceptionDispatchInfo? CompletionError)
    {
        public Exception? Exception => CompletionError?.SourceException;
    }

    private readonly record struct TimedAttemptCompletion(
        TimedAttemptCompletionKind Kind,
        TypedStageExecutionResult Result,
        Exception? Exception)
    {
        public static TimedAttemptCompletion StageResult(TypedStageExecutionResult result) =>
            new(TimedAttemptCompletionKind.StageResultReturned, result, null);

        public static TimedAttemptCompletion ExpectedTimeoutCancellation(Exception exception) =>
            new(TimedAttemptCompletionKind.ExpectedTimeoutCancellation, default, exception);

        public static TimedAttemptCompletion Faulted(Exception exception) =>
            new(TimedAttemptCompletionKind.Faulted, default, exception);

        public static TimedAttemptCompletion StillRunning() =>
            new(TimedAttemptCompletionKind.StillRunning, default, null);

        public static TimedAttemptCompletion CallerCancelled(Exception exception) =>
            new(TimedAttemptCompletionKind.CallerCancelled, default, exception);
    }

    private enum SourceStopReason
    {
        None = 0,
        Drain = 1,
        RuntimeCancellation = 2,
    }

    private readonly record struct SourceStopClassificationSnapshot(
        bool SourceCancellationRequested,
        SourceStopReason Reason)
    {
        public bool IsGraceful =>
            SourceCancellationRequested
            && Reason == SourceStopReason.Drain;
    }

    // Prevents scheduling retries when the remaining StageTimeout budget is too small
    // to execute a meaningful next attempt after retry delay.
    private static readonly TimeSpan MinimumRetryAttemptBudget = TimeSpan.FromMilliseconds(5);
    private const int DefaultOutputCapacity = 1024;

    private readonly PipelineRuntime _runtime;
    private readonly TypedPipelineSpec<TInput, TOutput> _spec;
    private readonly IPipelineSink<TOutput>? _sink;
    private readonly PipelineRuntimeOptions _options;
    private readonly IPipelineClock _clock;
    private readonly PipelineTime _time;
    private readonly Channel<PipelineOutput<TOutput>> _outputs;
    private readonly PipelineOutputEmitter<TOutput> _outputEmitter;
    private readonly PipelineProducer<TInput> _producer;
    private readonly PipelineWorker<TInput> _worker;
    private readonly AdaptiveParallelismRuntimeState? _adaptiveParallelism;
    private readonly StageExecutor _stageExecutor;
    private readonly SinkExecutor<TOutput> _sinkExecutor;
    private readonly LateStageAttemptRegistry _lateAttemptRegistry;
    private readonly PipelineComponentLifetimeManager<TInput, TOutput> _componentLifetime;
    private readonly SmartPipeMetricsRecorder _metrics;
    private readonly PipelineLifecycleController _lifecycle = new();
    private readonly IPipelineObserverDispatcher _observerDispatcher;
    private readonly TaskCompletionSource<Task> _publicCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _publicCompletion;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _sourceCts;
    private readonly CancellationTokenSource _processingCts;
    private readonly CancellationTokenRegistration _sourceCancellationRegistration;
    private ChannelReader<ProcessingEnvelope<TInput>>? _inputReader;
    private readonly Dictionary<string, CircuitBreaker> _breakers = [];
    private readonly object _breakersGate = new();
    private readonly object _lifecycleGate = new();
    private ExecutorLifecycleState _executorLifecycleState;
    private int _disposed;
    private Task? _runTask;
    private Task? _disposeTask;
    private Exception? _disposeException;
    private int _started;
    private int _drainRequested;
    private int _stopAcceptingRequested;
    private int _sourceStopReason;
    private int _publicCancellationPending;

    public TypedPipelineExecutor(
        PipelineRuntime runtime,
        TypedPipelineSpec<TInput, TOutput> spec,
        IPipelineSink<TOutput>? sink,
        CancellationToken ct
    )
    {
        _publicCompletion = _publicCompletionSource.Task.Unwrap();
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _sink = sink;
        _options = _runtime.Options;
        _clock = _options.Clock;
        _time = new PipelineTime(_clock);
        _metrics = new SmartPipeMetricsRecorder(_clock);
        _lateAttemptRegistry = new LateStageAttemptRegistry(_time);
        _componentLifetime = new PipelineComponentLifetimeManager<TInput, TOutput>(
            _spec.Source,
            _spec.Stages,
            _sink,
            _spec.OwnershipOptions,
            _lateAttemptRegistry);
        _outputs = CreateOutputChannel(_options, OnOutputDropped);
        _outputEmitter = new PipelineOutputEmitter<TOutput>(
            _outputs.Writer,
            _options,
            _sink is not null);
        _producer = new PipelineProducer<TInput>(
            _spec.Source,
            ShouldStopAccepting,
            _metrics.RecordActivity);
        _worker = new PipelineWorker<TInput>(
            ProcessEnvelopeWithAdaptiveAdmissionAsync,
            RequestStopAccepting);
        _stageExecutor = new StageExecutor(
            _spec.PipelineId,
            _runtime.RunId,
            _spec.LineageMode,
            _clock,
            _time,
            GetOrCreateBreaker,
            GetRetryDecision,
            EmitRetryScheduledAsync,
            EmitRetryExhaustedAsync,
            WriteDeadLetterAsync,
            WriteTerminalAsync,
            EmitAsync,
            ExecuteStageAttemptAsync);
        _sinkExecutor = new SinkExecutor<TOutput>(
            _sink,
            _spec.PipelineId,
            _runtime.RunId,
            _clock,
            EmitAsync,
            _metrics.RecordSinkDuration);
        _observerDispatcher = PipelineObserverDispatcher.Create(
            _spec.Observers,
            _options.ObserverDispatch,
            _clock,
            _time,
            OnObserverEventDropped
        );
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sourceCts = new CancellationTokenSource();
        _processingCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _sourceCancellationRegistration = _cts.Token.Register(
            static state =>
                ((TypedPipelineExecutor<TInput, TOutput>)state!).RequestRuntimeSourceCancellation(),
            this);
        _adaptiveParallelism = ShouldUseAdaptiveAdmission(_options)
            ? new AdaptiveParallelismRuntimeState(_options)
            : null;
    }

    private static Channel<PipelineOutput<TOutput>> CreateOutputChannel(
        PipelineRuntimeOptions options,
        Action<PipelineOutput<TOutput>> itemDropped)
    {
        options.Validate();
        var capacity = options.OutputCapacity ?? DefaultOutputCapacity;
        return PipelineChannelFactory.CreateOutput<TOutput>(
            capacity,
            options.OutputFullMode,
            itemDropped);
    }

    public PipelineRun<TOutput> Start()
    {
        Task runTask;
        lock (_lifecycleGate)
        {
            if (_executorLifecycleState is ExecutorLifecycleState.Disposing or ExecutorLifecycleState.Disposed)
            {
                throw new ObjectDisposedException(
                    "pipeline runtime",
                    "This pipeline runtime instance has already been disposed.");
            }

            if (_executorLifecycleState != ExecutorLifecycleState.Created
                || Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException(
                    "This pipeline runtime instance has already been started. Create a new runtime instance per run.");
            }

            _executorLifecycleState = ExecutorLifecycleState.Running;
            runTask = Task.Run(RunAsync, CancellationToken.None);
            _runTask = runTask;
        }

        return new PipelineRun<TOutput>(
            _outputs.Reader,
            _publicCompletion,
            () => _lifecycle.State,
            CancelAsync,
            DrainAsync,
            TryDrainAsync,
            AbortAsync,
            DisposeAsync,
            CaptureMetricsSnapshot
        );
    }

    public async ValueTask CancelAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _lifecycle.RequestCancellation();
        var cancelTask = _cts.CancelAsync();
        _adaptiveParallelism?.Complete();

        await cancelTask.WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var result = await TryDrainAsync(timeout, ct).ConfigureAwait(false);
        switch (result.Status)
        {
            case PipelineDrainStatus.Completed:
            case PipelineDrainStatus.AlreadyCompleted:
                return;
            case PipelineDrainStatus.TimedOutStillRunning:
                throw new TimeoutException(
                    $"Pipeline drain timed out after {timeout} while run state was {result.State}.");
            case PipelineDrainStatus.CancelledByCaller:
                throw result.Exception
                    ?? new OperationCanceledException("Pipeline drain was cancelled by the caller.", ct);
            case PipelineDrainStatus.Faulted:
                throw result.Exception
                    ?? new InvalidOperationException("Pipeline drain failed because the run faulted.");
            default:
                throw new InvalidOperationException(
                    $"Unsupported pipeline drain status '{result.Status}'.");
        }
    }

    public async ValueTask<PipelineDrainResult> TryDrainAsync(
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var started = _clock.GetTimestamp();
        RequestDrain();
        var runTask = _runTask ?? throw new InvalidOperationException("Pipeline run has not started.");
        var alreadyCompleted = runTask.IsCompleted;
        if (!alreadyCompleted)
            _lifecycle.MarkDrainingIfRunning();

        try
        {
            await _time.WaitAsync(runTask, timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CreateDrainResult(PipelineDrainStatus.TimedOutStillRunning, started);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            return CreateDrainResult(PipelineDrainStatus.CancelledByCaller, started, ex);
        }
        catch (Exception ex)
        {
            return CreateDrainResult(PipelineDrainStatus.Faulted, started, ex);
        }

        _lifecycle.MarkCompletedIfDraining();
        return CreateDrainResult(
            alreadyCompleted ? PipelineDrainStatus.AlreadyCompleted : PipelineDrainStatus.Completed,
            started);
    }

    private PipelineDrainResult CreateDrainResult(
        PipelineDrainStatus status,
        long started,
        Exception? exception = null)
    {
        var elapsed = _clock.GetElapsedTime(started, _clock.GetTimestamp());
        return new PipelineDrainResult(status, _lifecycle.State, elapsed, exception);
    }

    public ValueTask AbortAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _lifecycle.RequestAbort();
        _cts.Cancel();
        _adaptiveParallelism?.Complete();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        TaskCompletionSource? starter = null;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
                if (_executorLifecycleState is ExecutorLifecycleState.Created or ExecutorLifecycleState.Running)
                    _executorLifecycleState = ExecutorLifecycleState.Disposing;
            }

            disposeTask = _disposeTask;
        }

        if (starter is not null)
            _ = RunDisposeAsync(starter);

        return new ValueTask(disposeTask);
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            lock (_lifecycleGate)
                _executorLifecycleState = ExecutorLifecycleState.Disposed;
            completion.SetResult();
        }
        catch (Exception ex)
        {
            lock (_lifecycleGate)
                _executorLifecycleState = ExecutorLifecycleState.Disposed;
            completion.SetException(ex);
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);

        try
        {
            _cts.Cancel();
            _sourceCts.Cancel();
            _processingCts.Cancel();
            _adaptiveParallelism?.Complete();

            var runTask = _runTask;
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch
                {
                    // RunAsync faults are observed via the pipeline run's Completion task.
                }

                var deferredCleanupErrors = await _componentLifetime.DisposeDeferredStagesAsync()
                    .ConfigureAwait(false);
                var disposeException = CreateCombinedException(null, deferredCleanupErrors);
                if (disposeException is not null)
                    ExceptionDispatchInfo.Capture(disposeException).Throw();

                if (_disposeException is not null)
                    ExceptionDispatchInfo.Capture(_disposeException).Throw();
            }
            else
            {
                var cleanup = await _componentLifetime.DisposeAsync().ConfigureAwait(false);
                var observerErrors = await RuntimeCleanup.CollectAsync([
                    () => _observerDispatcher.DisposeAsync(),
                ]).ConfigureAwait(false);
                _sinkExecutor.Dispose();
                RuntimeCleanup.ThrowCombined(null, cleanup.DisposeErrors.Concat(observerErrors).ToArray());
            }
        }
        finally
        {
            _sourceCancellationRegistration.Dispose();
            _sourceCts.Dispose();
            _processingCts.Dispose();
            _cts.Dispose();
        }
    }

    private Task RunAsync()
    {
        var runTask = RunCoreAsync();
        _ = runTask.ContinueWith(
            static (task, state) =>
                ((TypedPipelineExecutor<TInput, TOutput>)state!).CompleteUnexpectedPublicRun(task),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return runTask;
    }

    private async Task RunCoreAsync()
    {
        using var activity = SmartPipeActivitySource.Source.StartActivity("Pipeline.Run", ActivityKind.Internal);
        activity?.SetTag("smartpipe.pipeline_id", _spec.PipelineId);
        activity?.SetTag("smartpipe.run_id", _runtime.RunId);
        activity?.SetTag("smartpipe.parallelism", _options.EffectiveMaxConcurrency);
        activity?.SetTag("smartpipe.input_capacity", _options.InputCapacity);
        activity?.SetTag("smartpipe.output_capacity", _options.OutputCapacity);

        ExceptionDispatchInfo? primary = null;
        try
        {
            _lifecycle.MarkRunning();
            await EmitAsync(
                    new PipelineStartedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow()
                    ),
                    _processingCts.Token
                )
                .ConfigureAwait(false);
            await _componentLifetime.InitializeAsync(_processingCts.Token).ConfigureAwait(false);

            if (_options.EffectiveMaxConcurrency == 1)
                await RunSequentialProcessingAsync(_sourceCts.Token, _processingCts.Token).ConfigureAwait(false);
            else
                await RunParallelProcessingAsync(_sourceCts.Token, _processingCts.Token).ConfigureAwait(false);

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            primary = ExceptionDispatchInfo.Capture(ex);
        }
        catch (ChannelClosedException ex) when (_cts.IsCancellationRequested)
        {
            var cancellation = new OperationCanceledException("Pipeline run cancelled.", ex);
            activity?.SetStatus(ActivityStatusCode.Error, cancellation.Message);
            primary = ExceptionDispatchInfo.Capture(cancellation);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            primary = ExceptionDispatchInfo.Capture(ex);
        }

        _adaptiveParallelism?.Complete();
        var observerFlushErrors = await RuntimeCleanup.CollectAsync([
            () => _observerDispatcher.FlushAsync(CancellationToken.None),
        ]).ConfigureAwait(false);
        var componentCleanup = await _componentLifetime.DisposeAsync().ConfigureAwait(false);
        var finalizationErrors = observerFlushErrors.Concat(componentCleanup.CompletionErrors).ToArray();
        _disposeException = CreateCombinedException(
            null,
            observerFlushErrors.Concat(componentCleanup.DisposeErrors).ToArray());
        var outcome = DetermineTerminalOutcome(primary, finalizationErrors);

        _lifecycle.MarkTerminal(outcome.State);
        CompleteOutputs(outcome);

        // Terminal observer delivery and dispatcher teardown happen after the
        // public state/output outcome is published. They are cleanup diagnostics
        // and must not rewrite PipelineRun.Completion.
        await RuntimeCleanup.CollectAsync([
            () => EmitTerminalEventAsync(outcome),
            () => _observerDispatcher.CompleteAsync(CancellationToken.None),
            () => _observerDispatcher.DisposeAsync(),
        ]).ConfigureAwait(false);

        _sinkExecutor.Dispose();
        CompletePublicRun(outcome);
        outcome.CompletionError?.Throw();
    }

    private TerminalOutcome DetermineTerminalOutcome(
        ExceptionDispatchInfo? primary,
        IReadOnlyList<Exception> componentCleanupErrors)
    {
        var exception = CreateCombinedException(primary, componentCleanupErrors);
        var completionError = CaptureTerminalException(primary, exception, componentCleanupErrors);
        var hasCleanupError = componentCleanupErrors.Count != 0;
        if (primary?.SourceException is not null
            && (primary.SourceException is not OperationCanceledException
                || !_cts.IsCancellationRequested))
        {
            return new TerminalOutcome(
                PipelineRunState.Faulted,
                completionError);
        }

        if (hasCleanupError)
        {
            return new TerminalOutcome(
                PipelineRunState.Faulted,
                completionError);
        }

        if (primary?.SourceException is OperationCanceledException)
        {
            var state = _lifecycle.IsAbortRequested
                ? PipelineRunState.Aborted
                : PipelineRunState.Cancelled;
            return new TerminalOutcome(state, completionError);
        }

        if (_lifecycle.IsAbortRequested)
        {
            var aborted = new OperationCanceledException("Pipeline run aborted.");
            return new TerminalOutcome(
                PipelineRunState.Aborted,
                ExceptionDispatchInfo.Capture(aborted));
        }

        if (_lifecycle.IsCancellationRequested)
        {
            var cancelled = new OperationCanceledException("Pipeline run cancelled.");
            return new TerminalOutcome(
                PipelineRunState.Cancelled,
                ExceptionDispatchInfo.Capture(cancelled));
        }

        return new TerminalOutcome(PipelineRunState.Completed, null);
    }

    private void CompletePublicRun(TerminalOutcome outcome)
    {
        switch (outcome.State)
        {
            case PipelineRunState.Completed:
                _publicCompletionSource.TrySetResult(Task.CompletedTask);
                break;
            case PipelineRunState.Cancelled:
            case PipelineRunState.Aborted:
                Volatile.Write(ref _publicCancellationPending, 1);
                break;
            case PipelineRunState.Faulted:
                _publicCompletionSource.TrySetResult(Task.FromException(
                    outcome.Exception
                    ?? new InvalidOperationException("Faulted pipeline run has no completion exception.")));
                break;
            default:
                _publicCompletionSource.TrySetResult(Task.FromException(
                    new InvalidOperationException($"Unsupported terminal state '{outcome.State}'.")));
                break;
        }
    }

    private void CompleteUnexpectedPublicRun(Task runTask)
    {
        if (_publicCompletionSource.Task.IsCompleted)
            return;

        if (Interlocked.Exchange(ref _publicCancellationPending, 0) != 0
            && runTask.IsCanceled)
        {
            _publicCompletionSource.TrySetResult(runTask);
            return;
        }

        if (runTask.Exception is { } exception)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion.SetException(exception.InnerExceptions);
            _publicCompletionSource.TrySetResult(completion.Task);
            return;
        }

        if (runTask.IsCanceled)
        {
            _publicCompletionSource.TrySetResult(
                Task.FromException(new TaskCanceledException(runTask)));
            return;
        }

        _publicCompletionSource.TrySetResult(Task.FromException(
            new InvalidOperationException("Pipeline run completed without publishing a terminal outcome.")));
    }

    private static ExceptionDispatchInfo? CaptureTerminalException(
        ExceptionDispatchInfo? primary,
        Exception? exception,
        IReadOnlyList<Exception> cleanupErrors)
    {
        if (exception is null)
            return null;

        if (primary is not null && cleanupErrors.Count == 0)
            return primary;

        return ExceptionDispatchInfo.Capture(exception);
    }

    private static Exception? CreateCombinedException(
        ExceptionDispatchInfo? primary,
        IReadOnlyList<Exception> cleanupErrors)
    {
        if (primary is null)
            return cleanupErrors.Count switch
            {
                0 => null,
                1 => cleanupErrors[0],
                _ => new AggregateException(cleanupErrors),
            };

        if (cleanupErrors.Count == 0)
            return primary.SourceException;

        var errors = new Exception[cleanupErrors.Count + 1];
        errors[0] = primary.SourceException;
        for (var i = 0; i < cleanupErrors.Count; i++)
            errors[i + 1] = cleanupErrors[i];

        return new AggregateException(errors);
    }

    private void CompleteOutputs(TerminalOutcome outcome)
    {
        var exception = outcome.Exception;
        if (exception is null)
            _outputs.Writer.TryComplete();
        else if (outcome.State == PipelineRunState.Faulted
            && exception is OperationCanceledException)
        {
            _outputs.Writer.TryComplete(
                new AggregateException("Pipeline faulted with an unrequested cancellation exception.", exception));
        }
        else
            _outputs.Writer.TryComplete(exception);
    }

    private ValueTask EmitTerminalEventAsync(TerminalOutcome outcome)
    {
        var now = _clock.GetUtcNow();
        PipelineEvent pipelineEvent = outcome.State switch
        {
            PipelineRunState.Completed => new PipelineCompletedEvent(
                _spec.PipelineId,
                _runtime.RunId,
                now),
            PipelineRunState.Cancelled or PipelineRunState.Aborted => new PipelineCancelledEvent(
                _spec.PipelineId,
                _runtime.RunId,
                now),
            PipelineRunState.Faulted => new PipelineFaultedEvent(
                _spec.PipelineId,
                _runtime.RunId,
                now,
                outcome.Exception
                    ?? new InvalidOperationException("Pipeline run faulted without an exception.")),
            _ => throw new InvalidOperationException(
                $"Unsupported terminal pipeline state '{outcome.State}'."),
        };

        return EmitAsync(pipelineEvent, CancellationToken.None);
    }

    internal void RequestDrain()
    {
        Volatile.Write(ref _drainRequested, 1);
        RecordSourceStopReason(SourceStopReason.Drain);
        _sourceCts.Cancel();
    }

    private void RequestStopAccepting() => Volatile.Write(ref _stopAcceptingRequested, 1);

    private void RequestRuntimeSourceCancellation()
    {
        RecordSourceStopReason(SourceStopReason.RuntimeCancellation);
        _sourceCts.Cancel();
    }

    private void RecordSourceStopReason(SourceStopReason reason)
    {
        Interlocked.CompareExchange(
            ref _sourceStopReason,
            (int)reason,
            (int)SourceStopReason.None);
    }

    private bool ShouldStopAccepting()
    {
        return Volatile.Read(ref _drainRequested) != 0
            || Volatile.Read(ref _stopAcceptingRequested) != 0;
    }

    private static bool ShouldUseAdaptiveAdmission(PipelineRuntimeOptions options) =>
        options.AdaptiveParallelism.Enabled && options.EffectiveMaxConcurrency > 1;

    private async ValueTask RunSequentialProcessingAsync(
        CancellationToken sourceToken,
        CancellationToken processingToken)
    {
        var enumerator = _spec.Source
            .ReadEnvelopesAsync(sourceToken)
            .GetAsyncEnumerator(sourceToken);
        try
        {
            while (!ShouldStopAccepting() && await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var envelope = enumerator.Current;
                _metrics.RecordActivity();
                var action = await ProcessEnvelopeAsync(envelope, processingToken).ConfigureAwait(false);
                if (action == FailureAction.StopPipeline)
                {
                    RequestStopAccepting();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            var classification = CaptureSourceStopClassificationSnapshot();
            if (!classification.IsGraceful)
                throw;

            _cts.Token.ThrowIfCancellationRequested();
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask RunParallelProcessingAsync(
        CancellationToken sourceToken,
        CancellationToken processingToken)
    {
        var input = PipelineChannelFactory.CreateInput<TInput>(
            _options.InputCapacity,
            _options.InputFullMode,
            OnInputDropped);
        Volatile.Write(ref _inputReader, input.Reader);

        Exception? workerFailure = null;
        object workerFailureGate = new();

        void RecordWorkerFailure(Exception ex)
        {
            lock (workerFailureGate)
                workerFailure ??= ex;
        }

        bool HasWorkerFailure()
        {
            lock (workerFailureGate)
                return workerFailure is not null;
        }

        var workers = Enumerable
            .Range(0, _options.EffectiveMaxConcurrency)
            .Select(_ => Task.Run(
                () => _worker.RunAsync(input.Reader, input.Writer, RecordWorkerFailure, processingToken),
                CancellationToken.None
            ))
            .ToArray();

        try
        {
            try
            {
                await _producer.ProduceAsync(input.Writer, sourceToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var classification = CaptureSourceStopClassificationSnapshot();
                if (!classification.IsGraceful)
                    throw;

                _cts.Token.ThrowIfCancellationRequested();
            }
            catch (ChannelClosedException) when (HasWorkerFailure())
            {
            }
            finally
            {
                input.Writer.TryComplete();
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _inputReader, null);
        }
    }

    private SmartPipeMetricsSnapshot CaptureMetricsSnapshot()
    {
        var inputReader = Volatile.Read(ref _inputReader);
        _metrics.UpdateQueueDepths(
            CountOrZero(inputReader),
            CountOrZero(_outputs.Reader));
        return _metrics.CaptureSnapshot();
    }

    private static int CountOrZero<T>(ChannelReader<T>? reader) =>
        reader is not null && reader.CanCount ? reader.Count : 0;

    private SourceStopClassificationSnapshot CaptureSourceStopClassificationSnapshot()
    {
        return new SourceStopClassificationSnapshot(
            SourceCancellationRequested: _sourceCts.IsCancellationRequested,
            Reason: (SourceStopReason)Volatile.Read(ref _sourceStopReason));
    }

    private void OnInputDropped(ProcessingEnvelope<TInput> envelope)
    {
        _metrics.RecordItemDropped();
        EmitEventFireAndForget(new InputDroppedEvent(
            _spec.PipelineId,
            _runtime.RunId,
            envelope.TraceId,
            _clock.GetUtcNow()));
    }

    private void OnOutputDropped(PipelineOutput<TOutput> output)
    {
        _metrics.RecordOutputDropped();
        EmitEventFireAndForget(new OutputDroppedEvent(
            _spec.PipelineId,
            _runtime.RunId,
            output.Result.TraceId,
            _clock.GetUtcNow()));
    }

    private void OnObserverEventDropped(PipelineEvent pipelineEvent)
    {
        _metrics.RecordObserverEventDropped();
    }

    private async ValueTask<FailureAction?> ProcessEnvelopeAsync(
        ProcessingEnvelope<TInput> sourceEnvelope,
        CancellationToken ct
    )
    {
        var started = _clock.GetTimestamp();
        object current = NormalizeEnvelope(sourceEnvelope);
        foreach (var stage in _spec.Stages)
        {
            var stageResult = await _stageExecutor.ExecuteAsync(stage, current, ct)
                .ConfigureAwait(false);
            if (stageResult.StopProcessing)
                return stageResult.FailureAction;

            current = stageResult.Envelope;
        }

        var outputEnvelope = (ProcessingEnvelope<TOutput>)current;
        var result = PipelineResult<TOutput>.Success(
            outputEnvelope.Payload,
            outputEnvelope.TraceId
        );

        await _sinkExecutor.WriteAsync(outputEnvelope, ct).ConfigureAwait(false);

        await _outputEmitter
            .WriteAsync(new PipelineOutput<TOutput>(outputEnvelope, result), ct)
            .ConfigureAwait(false);

        var elapsed = _clock.GetElapsedTime(started, _clock.GetTimestamp());
        _metrics.RecordProcessed(Math.Max(0, elapsed.TotalMilliseconds));

        return null;
    }

    private async ValueTask<FailureAction?> ProcessEnvelopeWithAdaptiveAdmissionAsync(
        ProcessingEnvelope<TInput> sourceEnvelope,
        CancellationToken ct
    )
    {
        var adaptiveParallelism = _adaptiveParallelism;
        if (adaptiveParallelism is null)
            return await ProcessEnvelopeAsync(sourceEnvelope, ct).ConfigureAwait(false);

        AdaptiveConcurrencyLimiter.Lease lease;
        try
        {
            lease = await adaptiveParallelism.AcquireAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex) when (IsAdaptiveAdmissionShutdownInProgress())
        {
            throw new OperationCanceledException("Pipeline run cancelled.", ex, ct);
        }

        var started = _clock.GetTimestamp();
        var failed = false;
        try
        {
            var action = await ProcessEnvelopeAsync(sourceEnvelope, ct).ConfigureAwait(false);
            failed = action is not null;
            return action;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try
            {
                var elapsed = _clock.GetElapsedTime(started, _clock.GetTimestamp());
                adaptiveParallelism.RecordCompletion(elapsed, failed);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private bool IsAdaptiveAdmissionShutdownInProgress() =>
        _cts.IsCancellationRequested
        || Volatile.Read(ref _disposed) != 0
        || _lifecycle.State is PipelineRunState.Cancelled or PipelineRunState.Aborted;

    private async ValueTask EmitRetryScheduledAsync(
        ITypedPipelineStage stage,
        TypedStageExecutionResult outcome,
        int retryAttempt,
        TimeSpan delay,
        SmartPipeError error,
        CancellationToken ct
    )
    {
        _metrics.RecordRetry();
        await EmitAsync(
                new RetryScheduledEvent(
                    _spec.PipelineId,
                    _runtime.RunId,
                    outcome.TraceId,
                    stage.StageId,
                    retryAttempt,
                    _clock.GetUtcNow(),
                    delay,
                    error
                ),
                ct
            )
            .ConfigureAwait(false);
    }

    private async ValueTask EmitRetryExhaustedAsync(
        ITypedPipelineStage stage,
        TypedStageExecutionResult outcome,
        SmartPipeError error,
        CancellationToken ct
    )
    {
        await EmitAsync(
                new RetryExhaustedEvent(
                    _spec.PipelineId,
                    _runtime.RunId,
                    outcome.TraceId,
                    stage.StageId,
                    outcome.Attempt,
                    _clock.GetUtcNow(),
                    error
                ),
                ct
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<TypedStageExecutionResult> ExecuteStageAttemptAsync(
        ITypedPipelineStage stage,
        object current,
        long stageStartedTimestamp,
        CancellationToken ct
    )
    {
        var attemptTimeout = GetEffectiveAttemptTimeout(stage, stageStartedTimestamp);
        if (attemptTimeout is null || attemptTimeout == Timeout.InfiniteTimeSpan)
            return await stage.ExecuteAsync(current, _spec.LineageMode, _clock, ct).ConfigureAwait(false);

        var startedAtUtc = _clock.GetUtcNow();
        if (attemptTimeout <= TimeSpan.Zero)
            return stage.CreateTimedOutResult(
                current,
                _spec.LineageMode,
                _clock,
                startedAtUtc,
                TimeSpan.Zero,
                null,
                false
            );

        CancellationTokenSource? timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var execution = stage.ExecuteAsync(current, _spec.LineageMode, _clock, timeoutCts.Token).AsTask();

        try
        {
            return await _time.WaitAsync(execution, attemptTimeout.Value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var retryMode = stage.FailureOptions.Timeout?.RetryMode ?? TimeoutRetryMode.CooperativeOnly;
            return stage.CreateTimedOutResult(
                current,
                _spec.LineageMode,
                _clock,
                startedAtUtc,
                attemptTimeout.Value,
                ex,
                retryMode != TimeoutRetryMode.DetachWithoutRetry
            );
        }
        catch (TimeoutException ex)
        {
            if (execution.IsCompleted)
                return await execution.ConfigureAwait(false);

            timeoutCts.Cancel();
            var transferredTimeoutCts = timeoutCts;
            timeoutCts = null;
            return await HandleTimedOutStageExecutionAsync(
                    stage,
                    current,
                    startedAtUtc,
                    attemptTimeout.Value,
                    ex,
                    execution,
                    transferredTimeoutCts,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private async ValueTask<TypedStageExecutionResult> HandleTimedOutStageExecutionAsync(
        ITypedPipelineStage stage,
        object current,
        DateTimeOffset startedAtUtc,
        TimeSpan attemptTimeout,
        TimeoutException exception,
        Task<TypedStageExecutionResult> execution,
        CancellationTokenSource timeoutCts,
        CancellationToken ct)
    {
        var retryMode = stage.FailureOptions.Timeout?.RetryMode ?? TimeoutRetryMode.CooperativeOnly;
        bool canRetryTimeout;
        switch (retryMode)
        {
            case TimeoutRetryMode.CooperativeOnly:
                var completion = await TryWaitForCooperativeTimeoutCompletionAsync(
                        _time,
                        execution,
                        GetCancellationGracePeriod(stage),
                        ct)
                    .ConfigureAwait(false);

                switch (completion.Kind)
                {
                    case TimedAttemptCompletionKind.StageResultReturned:
                        timeoutCts.Dispose();
                        return completion.Result;
                    case TimedAttemptCompletionKind.ExpectedTimeoutCancellation:
                        timeoutCts.Dispose();
                        canRetryTimeout = true;
                        break;
                    case TimedAttemptCompletionKind.Faulted:
                    case TimedAttemptCompletionKind.CallerCancelled:
                        if (execution.IsCompleted)
                            timeoutCts.Dispose();
                        else
                            RegisterLateStageAttempt(stage, current, execution, timeoutCts);

                        ExceptionDispatchInfo.Capture(completion.Exception!).Throw();
                        return default;
                    case TimedAttemptCompletionKind.StillRunning:
                        RegisterLateStageAttempt(stage, current, execution, timeoutCts);
                        canRetryTimeout = false;
                        break;
                    default:
                        timeoutCts.Dispose();
                        throw new InvalidOperationException(
                            $"Unsupported timed attempt completion kind '{completion.Kind}'.");
                }

                break;

            case TimeoutRetryMode.DetachWithoutRetry:
                canRetryTimeout = false;
                RegisterLateStageAttempt(stage, current, execution, timeoutCts);
                break;

            case TimeoutRetryMode.DetachAndRetryIdempotent:
                canRetryTimeout = true;
                RegisterLateStageAttempt(stage, current, execution, timeoutCts);
                break;

            default:
                timeoutCts.Dispose();
                throw new ArgumentOutOfRangeException(
                    nameof(TimeoutPolicy.RetryMode),
                    retryMode,
                    "Timeout retry mode is invalid.");
        }

        return stage.CreateTimedOutResult(
            current,
            _spec.LineageMode,
            _clock,
            startedAtUtc,
            attemptTimeout,
            exception,
            canRetryTimeout
        );
    }

    private static async ValueTask<TimedAttemptCompletion> TryWaitForCooperativeTimeoutCompletionAsync(
        PipelineTime time,
        Task<TypedStageExecutionResult> execution,
        TimeSpan gracePeriod,
        CancellationToken ct)
    {
        if (execution.IsCompleted)
            return await ObserveTimedAttemptCompletionAsync(execution, ct).ConfigureAwait(false);

        if (gracePeriod <= TimeSpan.Zero)
            return TimedAttemptCompletion.StillRunning();

        try
        {
            var result = await time.WaitAsync(execution, gracePeriod, ct).ConfigureAwait(false);
            return TimedAttemptCompletion.StageResult(result);
        }
        catch (TimeoutException)
        {
            if (execution.IsCompleted)
                return await ObserveTimedAttemptCompletionAsync(execution, ct).ConfigureAwait(false);

            return TimedAttemptCompletion.StillRunning();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested && !execution.IsCompleted)
        {
            return TimedAttemptCompletion.CallerCancelled(ex);
        }
        catch (Exception ex)
        {
            return ex is OperationCanceledException
                ? TimedAttemptCompletion.ExpectedTimeoutCancellation(ex)
                : TimedAttemptCompletion.Faulted(ex);
        }
    }

    private static async ValueTask<TimedAttemptCompletion> ObserveTimedAttemptCompletionAsync(
        Task<TypedStageExecutionResult> execution,
        CancellationToken callerToken)
    {
        try
        {
            var result = await execution.ConfigureAwait(false);
            return TimedAttemptCompletion.StageResult(result);
        }
        catch (OperationCanceledException ex) when (!callerToken.IsCancellationRequested)
        {
            return TimedAttemptCompletion.ExpectedTimeoutCancellation(ex);
        }
        catch (OperationCanceledException ex)
        {
            return TimedAttemptCompletion.CallerCancelled(ex);
        }
        catch (Exception ex)
        {
            return TimedAttemptCompletion.Faulted(ex);
        }
    }

    private void RegisterLateStageAttempt(
        ITypedPipelineStage stage,
        object current,
        Task<TypedStageExecutionResult> execution,
        CancellationTokenSource timeoutCts)
    {
        var correlation = stage.GetCorrelation(current);
        _lateAttemptRegistry.Register(
            stage.StageId,
            stage.StageName,
            correlation.TraceId,
            correlation.Attempt,
            execution,
            timeoutCts,
            GetLateAttemptFinalizationTimeout(stage));
    }

    private TimeSpan GetCancellationGracePeriod(ITypedPipelineStage stage)
    {
        var grace = stage.FailureOptions.Timeout?.CancellationGracePeriod ?? TimeSpan.FromSeconds(1);
        return grace == Timeout.InfiniteTimeSpan || grace > TimeSpan.Zero
            ? grace
            : TimeSpan.Zero;
    }

    private TimeSpan GetLateAttemptFinalizationTimeout(ITypedPipelineStage stage)
    {
        var timeout = stage.FailureOptions.Timeout?.LateAttemptFinalizationTimeout
            ?? TimeSpan.FromSeconds(30);
        return timeout == Timeout.InfiniteTimeSpan || timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.Zero;
    }

    private TimeSpan? GetEffectiveAttemptTimeout(
        ITypedPipelineStage stage,
        long stageStartedTimestamp
    )
    {
        var attemptTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.AttemptTimeout);
        var stageRemaining = GetStageTimeoutRemaining(stage, stageStartedTimestamp);
        if (stageRemaining is null)
            return attemptTimeout;

        if (attemptTimeout is null)
            return stageRemaining;

        return stageRemaining.Value < attemptTimeout.Value ? stageRemaining : attemptTimeout;
    }

    private TimeSpan? GetStageTimeoutRemaining(
        ITypedPipelineStage stage,
        long stageStartedTimestamp
    )
    {
        var stageTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.StageTimeout);
        if (stageTimeout is null)
            return null;

        var elapsed = _clock.GetElapsedTime(stageStartedTimestamp, _clock.GetTimestamp());
        return stageTimeout.Value - elapsed;
    }

    private static TimeSpan? NormalizeTimeout(TimeSpan? timeout)
    {
        return timeout == Timeout.InfiniteTimeSpan ? null : timeout;
    }

    private ProcessingEnvelope<TInput> NormalizeEnvelope(ProcessingEnvelope<TInput> envelope)
    {
        var pipelineId = _spec.ForcePipelineId || string.IsNullOrWhiteSpace(envelope.PipelineId)
            ? _spec.PipelineId
            : envelope.PipelineId;
        var runId = string.IsNullOrWhiteSpace(envelope.RunId) ? _runtime.RunId : envelope.RunId;
        var createdAtUtc =
            envelope.CreatedAtUtc == default ? _clock.GetUtcNow() : envelope.CreatedAtUtc;

        if (
            pipelineId == envelope.PipelineId
            && runId == envelope.RunId
            && createdAtUtc == envelope.CreatedAtUtc
        )
            return envelope;

        return envelope with
        {
            PipelineId = pipelineId,
            RunId = runId,
            CreatedAtUtc = createdAtUtc,
        };
    }

    private RetryDecision GetRetryDecision(
        ITypedPipelineStage stage,
        SmartPipeError error,
        int attempt,
        long stageStartedTimestamp
    )
    {
        var retry = stage.FailureOptions.Retry;
        if (retry is null || !retry.ShouldRetry(error))
            return new RetryDecision(RetryDecisionKind.NotRetryable, 0, TimeSpan.Zero);

        if (attempt >= retry.MaxRetries)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        int nextAttempt = attempt + 1;
        var delay = retry.GetDelay(nextAttempt);
        var remaining = GetStageTimeoutRemaining(stage, stageStartedTimestamp);
        if (remaining is null)
            return new RetryDecision(RetryDecisionKind.Retry, nextAttempt, delay);

        // Remaining budget must allow a meaningful next attempt after delay.
        // 'Meaningful' means the effective attempt timeout for the retry attempt
        // would be strictly greater than zero, accounting for the delay.
        var budgetAfterDelay = remaining.Value - delay;
        if (budgetAfterDelay <= TimeSpan.Zero)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        // If an attempt timeout is configured, the remaining budget after delay
        // must be at least that value for the retry to complete a full attempt.
        var attemptTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.AttemptTimeout);
        if (attemptTimeout is not null && budgetAfterDelay < attemptTimeout.Value)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        if (budgetAfterDelay <= MinimumRetryAttemptBudget)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        return new RetryDecision(RetryDecisionKind.Retry, nextAttempt, delay);
    }

    private async ValueTask WriteTerminalAsync(
        TypedStageExecutionResult outcome,
        CancellationToken ct
    )
    {
        if (outcome.IsTerminalNonFailure)
        {
            if (outcome.Kind == StageResultKind.Filtered)
                _metrics.RecordFiltered();

            var terminalResult = outcome.Kind switch
            {
                StageResultKind.Filtered => PipelineResult<TOutput>.Filtered(outcome.TraceId),
                StageResultKind.Skipped => PipelineResult<TOutput>.Skipped(outcome.TraceId),
                _ => throw new InvalidOperationException(
                    $"Unsupported non-failure terminal result '{outcome.Kind}'."),
            };

            await _outputEmitter
                .WriteAsync(new PipelineOutput<TOutput>(null, terminalResult), ct)
                .ConfigureAwait(false);
            return;
        }

        _metrics.RecordFailed();
        var error =
            outcome.Error
            ?? new SmartPipeError(
                outcome.Kind.ToString(),
                ErrorType.Permanent,
                outcome.Kind.ToString()
            );
        var result = PipelineResult<TOutput>.Failure(error, outcome.TraceId);
        await _outputEmitter
            .WriteAsync(new PipelineOutput<TOutput>(null, result), ct)
            .ConfigureAwait(false);
    }

    private async ValueTask WriteDeadLetterAsync(
        ITypedPipelineStage stage,
        object envelope,
        SmartPipeError error,
        CancellationToken ct
    )
    {
        _metrics.RecordActivity();
        var deadLetter = await stage
            .WriteDeadLetterAsync(envelope, error, _clock, ct)
            .ConfigureAwait(false);
        _metrics.RecordDeadLetter();
        await EmitAsync(
                new DeadLetterWrittenEvent(
                    _spec.PipelineId,
                    _runtime.RunId,
                    deadLetter.TraceId,
                    deadLetter.StageId,
                    deadLetter.StageName,
                    deadLetter.Attempt,
                    _clock.GetUtcNow()
                ),
                ct
            )
            .ConfigureAwait(false);
    }

    private async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        await _observerDispatcher.EmitAsync(pipelineEvent, ct).ConfigureAwait(false);
    }

    private void EmitEventFireAndForget(PipelineEvent pipelineEvent)
    {
        _ = TryEmitAsync(pipelineEvent).AsTask();
    }

    private async ValueTask TryEmitAsync(PipelineEvent pipelineEvent)
    {
        try
        {
            await EmitAsync(pipelineEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            _metrics.RecordObserverEventDropped();
        }
        catch (ChannelClosedException)
        {
            _metrics.RecordObserverEventDropped();
        }
        catch (Exception)
        {
            // Best-effort notifications must not hide the primary run outcome.
            _metrics.RecordObserverEventDropped();
        }
    }

    private CircuitBreaker? GetOrCreateBreaker(ITypedPipelineStage stage)
    {
        lock (_breakersGate)
        {
            if (!_breakers.TryGetValue(stage.StageId, out var breaker))
            {
                breaker = CreateBreaker(stage);
                if (breaker is not null)
                    _breakers[stage.StageId] = breaker;
            }

            return breaker;
        }
    }

    private CircuitBreaker? CreateBreaker(ITypedPipelineStage stage)
    {
        var policy = stage.FailureOptions.CircuitBreaker;
        if (policy is null)
            return null;
        policy.Validate();

        if (policy.EvaluationMode == CircuitBreakerEvaluationMode.FailureRatio)
        {
            return new CircuitBreaker(
                failureRatio: policy.FailureRatio,
                samplingDuration: policy.SamplingDuration,
                minimumThroughput: policy.MinimumThroughput,
                breakDuration: policy.BreakDuration,
                maxHalfOpenRequests: policy.MaxHalfOpenRequests,
                timeSource: new PipelineClockAdapter(_clock)
            );
        }

        return new CircuitBreaker(
            failureRatio: 0.5,
            samplingDuration: policy.BreakDuration,
            minimumThroughput: policy.FailureThreshold,
            breakDuration: policy.BreakDuration,
            maxHalfOpenRequests: 3,
            timeSource: new PipelineClockAdapter(_clock)
        );
    }
}

internal sealed class PipelineClockAdapter : IClock, ICircuitBreakerTimeSource
{
    private readonly IPipelineClock _clock;

    public PipelineClockAdapter(IPipelineClock clock)
    {
        _clock = clock;
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    public long GetTimestamp() => _clock.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp) =>
        _clock.GetElapsedTime(startingTimestamp, endingTimestamp);
}
