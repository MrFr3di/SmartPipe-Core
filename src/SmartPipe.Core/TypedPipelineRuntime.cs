#nullable enable

using System.Diagnostics;
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
        Exception? exception
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
        catch (OperationCanceledException)
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
            return TypedStageExecutionResult.Terminal(
                new SmartPipeError(
                    ex.Message,
                    ErrorType.Permanent,
                    "StageException",
                    ex),
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

    public TypedStageExecutionResult CreateTimedOutResult(
        object envelope,
        LineageMode lineageMode,
        IPipelineClock clock,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout,
        Exception? exception
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
            lineage
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
    IReadOnlyList<LineageEntry>? Lineage
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
            null
        );
    }

    public static TypedStageExecutionResult Terminal(
        SmartPipeError? error,
        StageResultKind kind,
        ulong traceId,
        int attempt,
        IReadOnlyList<LineageEntry> lineage
    )
    {
        return new TypedStageExecutionResult(false, null, error, kind, traceId, attempt, lineage);
    }
}

internal readonly record struct TypedStageCorrelation(ulong TraceId, int Attempt);

internal readonly record struct DeadLetterWriteResult(
    ulong TraceId,
    int Attempt,
    string StageId,
    string StageName
);

internal sealed class TypedPipelineExecutor<TInput, TOutput> : IAsyncDisposable
{
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
    private readonly Channel<PipelineOutput<TOutput>> _outputs;
    private readonly PipelineOutputEmitter<TOutput> _outputEmitter;
    private readonly PipelineProducer<TInput> _producer;
    private readonly PipelineWorker<TInput> _worker;
    private readonly AdaptiveParallelismRuntimeState? _adaptiveParallelism;
    private readonly StageExecutor _stageExecutor;
    private readonly SinkExecutor<TOutput> _sinkExecutor;
    private readonly SmartPipeMetricsRecorder _metrics;
    private readonly PipelineLifecycleController _lifecycle = new();
    private readonly IPipelineObserverDispatcher _observerDispatcher;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _sourceCts;
    private readonly CancellationTokenSource _processingCts;
    private readonly CancellationTokenRegistration _sourceCancellationRegistration;
    private readonly Dictionary<string, CircuitBreaker> _breakers = [];
    private readonly object _breakersGate = new();
    private int _disposed;
    private int _componentsDisposed;
    private Task? _runTask;
    private int _started;
    private int _drainRequested;
    private int _stopAcceptingRequested;
    private int _sourceStopReason;

    /// <summary>
    /// Initializes a new typed pipeline executor for a single pipeline run.
    /// </summary>
    /// <param name="runtime">The pipeline runtime.</param>
    /// <param name="spec">The pipeline specification to execute.</param>
    /// <param name="sink">The optional sink that receives pipeline results.</param>
    /// <param name="ct">A token that cancels the run.</param>
    public TypedPipelineExecutor(
        PipelineRuntime runtime,
        TypedPipelineSpec<TInput, TOutput> spec,
        IPipelineSink<TOutput>? sink,
        CancellationToken ct
    )
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _sink = sink;
        _options = _runtime.Options;
        _clock = _options.Clock;
        _metrics = new SmartPipeMetricsRecorder(_clock);
        _outputs = CreateOutputChannel(_options, OnOutputDropped);
        _outputEmitter = new PipelineOutputEmitter<TOutput>(
            _outputs.Writer,
            _options,
            _sink is not null);
        _producer = new PipelineProducer<TInput>(_spec.Source, ShouldStopAccepting);
        _worker = new PipelineWorker<TInput>(
            ProcessEnvelopeWithAdaptiveAdmissionAsync,
            RequestStopAccepting);
        _stageExecutor = new StageExecutor(
            _spec.PipelineId,
            _runtime.RunId,
            _spec.LineageMode,
            _clock,
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

    /// <summary>
    /// Creates the output channel for pipeline results.
    /// </summary>
    /// <param name="options">The pipeline runtime options that control output buffering and overflow behavior.</param>
    /// <param name="itemDropped">The callback invoked when an output item cannot be accepted.</param>
    /// <returns>The configured output channel.</returns>
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

    /// <summary>
    /// Starts the pipeline runtime and returns a handle for controlling the run.
    /// </summary>
    /// <returns>A <see cref="PipelineRun{TOutput}"/> for reading outputs and controlling the run lifecycle.</returns>
    public PipelineRun<TOutput> Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException(
                "This pipeline runtime instance has already been started. Create a new runtime instance per run.");
        }

        _runTask = Task.Run(RunAsync, CancellationToken.None);
        return new PipelineRun<TOutput>(
            _outputs.Reader,
            _runTask,
            () => _lifecycle.State,
            CancelAsync,
            DrainAsync,
            TryDrainAsync,
            AbortAsync,
            DisposeAsync,
            _metrics.CaptureSnapshot
        );
    }

    /// <summary>
    /// Cancels the pipeline run.
    /// </summary>
    /// <param name="ct">A token that cancels waiting for the cancellation request to complete.</param>
    public async ValueTask CancelAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        _lifecycle.MarkCancelledUnlessAborted();
        var cancellation = new OperationCanceledException("Pipeline run cancelled.");
        var cancelTask = _cts.CancelAsync();
        _adaptiveParallelism?.Complete();
        _outputs.Writer.TryComplete(cancellation);

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
            await runTask.WaitAsync(timeout, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Aborts the current pipeline run.
    /// </summary>
    /// <param name="ct">A cancellation token for the abort request.</param>
    public ValueTask AbortAsync(CancellationToken ct = default)
    {
        _lifecycle.MarkAborted();
        _cts.Cancel();
        _adaptiveParallelism?.Complete();
        _outputs.Writer.TryComplete(new OperationCanceledException("Pipeline run aborted."));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Disposes the executor and releases pipeline resources.
    /// </summary>
    /// <remarks>
    /// Cancels any active run, waits for an in-flight run to finish, and then disposes the pipeline components,
    /// observer dispatcher, and internal cancellation resources.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _cts.Cancel();
        _sourceCts.Cancel();
        _processingCts.Cancel();
        _adaptiveParallelism?.Complete();

        // Wait for the in-flight run task to drain before disposing the linked
        // CTSs, otherwise RunAsync may observe ObjectDisposedException when
        // it next accesses a CTS token after a dispose-triggered resumption.
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
        }

        await DisposeComponentsAsync(CancellationToken.None).ConfigureAwait(false);
        await _observerDispatcher.DisposeAsync().ConfigureAwait(false);
        _sourceCancellationRegistration.Dispose();
        _sourceCts.Dispose();
        _processingCts.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Runs the pipeline lifecycle and processing loop.
    /// </summary>
    /// <remarks>
    /// Emits lifecycle events, initializes components, processes input sequentially or in parallel, and completes or tears down runtime resources when the run ends, is cancelled, or faults.
    /// </remarks>
    private async Task RunAsync()
    {
        using var activity = SmartPipeActivitySource.Source.StartActivity("Pipeline.Run", ActivityKind.Internal);
        activity?.SetTag("smartpipe.pipeline_id", _spec.PipelineId);
        activity?.SetTag("smartpipe.run_id", _runtime.RunId);
        activity?.SetTag("smartpipe.parallelism", _options.EffectiveMaxConcurrency);
        activity?.SetTag("smartpipe.input_capacity", _options.InputCapacity);
        activity?.SetTag("smartpipe.output_capacity", _options.OutputCapacity);

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
            await InitializeComponentsAsync(_processingCts.Token).ConfigureAwait(false);

            if (_options.EffectiveMaxConcurrency == 1)
                await RunSequentialProcessingAsync(_sourceCts.Token, _processingCts.Token).ConfigureAwait(false);
            else
                await RunParallelProcessingAsync(_sourceCts.Token, _processingCts.Token).ConfigureAwait(false);

            _lifecycle.MarkCompleted();
            activity?.SetStatus(ActivityStatusCode.Ok);
            await EmitAsync(
                    new PipelineCompletedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow()
                    ),
                    _processingCts.Token
                )
                .ConfigureAwait(false);
            await _observerDispatcher.CompleteAsync(_processingCts.Token).ConfigureAwait(false);
            _outputs.Writer.TryComplete();
        }
        catch (OperationCanceledException ex)
        {
            _lifecycle.MarkCancelledUnlessAborted();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

            await TryEmitAsync(
                    new PipelineCancelledEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow()
                    )
                )
                .ConfigureAwait(false);
            await TryCompleteObserversAsync().ConfigureAwait(false);
            _outputs.Writer.TryComplete(ex);
            throw;
        }
        catch (ChannelClosedException ex) when (_cts.IsCancellationRequested)
        {
            _lifecycle.MarkCancelledUnlessAborted();
            var cancellation = new OperationCanceledException("Pipeline run cancelled.", ex);
            activity?.SetStatus(ActivityStatusCode.Error, cancellation.Message);

            await TryEmitAsync(
                    new PipelineCancelledEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow()
                    )
                )
                .ConfigureAwait(false);
            await TryCompleteObserversAsync().ConfigureAwait(false);
            _outputs.Writer.TryComplete(cancellation);
            throw cancellation;
        }
        catch (Exception ex)
        {
            _lifecycle.MarkFaulted();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await TryEmitAsync(
                    new PipelineFaultedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow(),
                        ex
                    )
                )
                .ConfigureAwait(false);
            await TryCompleteObserversAsync().ConfigureAwait(false);
            _outputs.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            _adaptiveParallelism?.Complete();
            await DisposeComponentsAsync(CancellationToken.None).ConfigureAwait(false);
            await _observerDispatcher.DisposeAsync().ConfigureAwait(false);
            _sinkExecutor.Dispose();
        }
    }

    private async ValueTask TryCompleteObserversAsync()
    {
        try
        {
            await _observerDispatcher.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Run failure/cancellation should remain the primary completion cause.
        }
    }

    /// <summary>
    /// Requests a graceful drain of the current pipeline run.
    /// </summary>
    internal void RequestDrain()
    {
        Volatile.Write(ref _drainRequested, 1);
        RecordSourceStopReason(SourceStopReason.Drain);
        _sourceCts.Cancel();
    }

    /// <summary>
/// Requests that the pipeline stop accepting new input.
/// </summary>
private void RequestStopAccepting() => Volatile.Write(ref _stopAcceptingRequested, 1);

    /// <summary>
    /// Stops the source due to runtime cancellation.
    /// </summary>
    private void RequestRuntimeSourceCancellation()
    {
        RecordSourceStopReason(SourceStopReason.RuntimeCancellation);
        _sourceCts.Cancel();
    }

    /// <summary>
    /// Records the source stop reason once.
    /// </summary>
    /// <param name="reason">The reason to store.</param>
    private void RecordSourceStopReason(SourceStopReason reason)
    {
        Interlocked.CompareExchange(
            ref _sourceStopReason,
            (int)reason,
            (int)SourceStopReason.None);
    }

    /// <summary>
    /// Determines whether the pipeline should stop accepting new inputs.
    /// </summary>
    /// <returns>
    /// <c>true</c> if draining has been requested or stop-accepting has been requested; otherwise, <c>false</c>.
    /// </returns>
    private bool ShouldStopAccepting()
    {
        return Volatile.Read(ref _drainRequested) != 0
            || Volatile.Read(ref _stopAcceptingRequested) != 0;
    }

    /// <summary>
        /// Determines whether adaptive admission should be enabled for the runtime.
        /// </summary>
        /// <param name="options">The pipeline runtime options.</param>
        /// <returns><c>true</c> if adaptive parallelism is enabled and the effective maximum concurrency is greater than 1, <c>false</c> otherwise.</returns>
        private static bool ShouldUseAdaptiveAdmission(PipelineRuntimeOptions options) =>
        options.AdaptiveParallelism.Enabled && options.EffectiveMaxConcurrency > 1;

    /// <summary>
    /// Processes source envelopes sequentially until the source is exhausted or the pipeline stops accepting input.
    /// </summary>
    /// <param name="sourceToken">The token used to read envelopes from the source.</param>
    /// <param name="processingToken">The token used for envelope processing.</param>
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

    /// <summary>
    /// Processes queued input in parallel workers.
    /// </summary>
    /// <param name="sourceToken">The token used to stop reading from the source.</param>
    /// <param name="processingToken">The token used to cancel worker processing.</param>
    /// <remarks>
    /// Graceful source cancellation is treated as drain-related cancellation and is allowed to complete without failing the run.
    /// </remarks>
    private async ValueTask RunParallelProcessingAsync(
        CancellationToken sourceToken,
        CancellationToken processingToken)
    {
        var input = PipelineChannelFactory.CreateInput<TInput>(
            _options.InputCapacity,
            _options.InputFullMode,
            OnInputDropped);

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

    /// <summary>
    /// Captures the current source cancellation state.
    /// </summary>
    /// <returns>A snapshot indicating whether source cancellation was requested and the recorded stop reason.</returns>
    private SourceStopClassificationSnapshot CaptureSourceStopClassificationSnapshot()
    {
        return new SourceStopClassificationSnapshot(
            SourceCancellationRequested: _sourceCts.IsCancellationRequested,
            Reason: (SourceStopReason)Volatile.Read(ref _sourceStopReason));
    }

    /// <summary>
    /// Records a dropped input item and emits an input-dropped event.
    /// </summary>
    /// <param name="envelope">The dropped input envelope.</param>
    private void OnInputDropped(ProcessingEnvelope<TInput> envelope)
    {
        _metrics.RecordItemDropped();
        EmitEventFireAndForget(new InputDroppedEvent(
            _spec.PipelineId,
            _runtime.RunId,
            envelope.TraceId,
            _clock.GetUtcNow()));
    }

    /// <summary>
    /// Records a dropped output and emits an output-dropped event.
    /// </summary>
    /// <param name="output">The dropped pipeline output.</param>
    private void OnOutputDropped(PipelineOutput<TOutput> output)
    {
        _metrics.RecordOutputDropped();
        EmitEventFireAndForget(new OutputDroppedEvent(
            _spec.PipelineId,
            _runtime.RunId,
            output.Result.TraceId,
            _clock.GetUtcNow()));
    }

    /// <summary>
    /// Records that an observer event was dropped.
    /// </summary>
    /// <param name="pipelineEvent">The dropped pipeline event.</param>
    private void OnObserverEventDropped(PipelineEvent pipelineEvent)
    {
        _metrics.RecordObserverEventDropped();
    }

    private async ValueTask InitializeComponentsAsync(CancellationToken ct)
    {
        await _spec.Source.InitializeAsync(ct).ConfigureAwait(false);
        foreach (var stage in _spec.Stages)
            await stage.InitializeAsync(ct).ConfigureAwait(false);

        if (_sink is not null)
            await _sink.InitializeAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes a source envelope through all stages and writes the resulting output.
    /// </summary>
    /// <param name="sourceEnvelope">The input envelope to process.</param>
    /// <param name="ct">The cancellation token to monitor.</param>
    /// <returns>The failure action that stopped processing, or <c>null</c> when the envelope completes successfully.</returns>
    private async ValueTask<FailureAction?> ProcessEnvelopeAsync(
        ProcessingEnvelope<TInput> sourceEnvelope,
        CancellationToken ct
    )
    {
        var startedAtUtc = _clock.GetUtcNow();
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

        var elapsed = _clock.GetUtcNow() - startedAtUtc;
        _metrics.RecordProcessed(Math.Max(0, elapsed.TotalMilliseconds));

        return null;
    }

    /// <summary>
    /// Processes an envelope under adaptive concurrency admission.
    /// </summary>
    /// <param name="sourceEnvelope">The input envelope to process.</param>
    /// <param name="ct">The cancellation token for the operation.</param>
    /// <returns>The failure action produced by processing, or <c>null</c> when processing completes successfully.</returns>
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

    /// <summary>
        /// Determines whether adaptive admission should stop accepting work.
        /// </summary>
        /// <returns><c>true</c> if the run is cancelled, aborted, or disposed, <c>false</c> otherwise.</returns>
        private bool IsAdaptiveAdmissionShutdownInProgress() =>
        _cts.IsCancellationRequested
        || Volatile.Read(ref _disposed) != 0
        || _lifecycle.State is PipelineRunState.Cancelled or PipelineRunState.Aborted;

    /// <summary>
    /// Records a retry and emits a retry scheduled event.
    /// </summary>
    /// <param name="stage">The stage being retried.</param>
    /// <param name="outcome">The execution result that triggered the retry.</param>
    /// <param name="retryAttempt">The next retry attempt number.</param>
    /// <param name="delay">The delay before the retry is attempted.</param>
    /// <param name="error">The error associated with the retry.</param>
    /// <param name="ct">The cancellation token.</param>
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
        DateTimeOffset stageStartedAtUtc,
        CancellationToken ct
    )
    {
        var attemptTimeout = GetEffectiveAttemptTimeout(stage, stageStartedAtUtc);
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
                null
            );

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(attemptTimeout.Value);
        var execution = stage.ExecuteAsync(current, _spec.LineageMode, _clock, timeoutCts.Token).AsTask();

        try
        {
            return await execution.WaitAsync(attemptTimeout.Value, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            return stage.CreateTimedOutResult(
                current,
                _spec.LineageMode,
                _clock,
                startedAtUtc,
                attemptTimeout.Value,
                ex
            );
        }
        catch (TimeoutException ex)
        {
            timeoutCts.Cancel();
            _ = ObserveLateStageExecutionAsync(execution);
            return stage.CreateTimedOutResult(
                current,
                _spec.LineageMode,
                _clock,
                startedAtUtc,
                attemptTimeout.Value,
                ex
            );
        }
    }

    private TimeSpan? GetEffectiveAttemptTimeout(
        ITypedPipelineStage stage,
        DateTimeOffset stageStartedAtUtc
    )
    {
        var attemptTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.AttemptTimeout);
        var stageRemaining = GetStageTimeoutRemaining(stage, stageStartedAtUtc);
        if (stageRemaining is null)
            return attemptTimeout;

        if (attemptTimeout is null)
            return stageRemaining;

        return stageRemaining.Value < attemptTimeout.Value ? stageRemaining : attemptTimeout;
    }

    private TimeSpan? GetStageTimeoutRemaining(
        ITypedPipelineStage stage,
        DateTimeOffset stageStartedAtUtc
    )
    {
        var stageTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.StageTimeout);
        if (stageTimeout is null)
            return null;

        var elapsed = _clock.GetUtcNow() - stageStartedAtUtc;
        return stageTimeout.Value - elapsed;
    }

    private static TimeSpan? NormalizeTimeout(TimeSpan? timeout)
    {
        return timeout == Timeout.InfiniteTimeSpan ? null : timeout;
    }

    private static async Task ObserveLateStageExecutionAsync(
        Task<TypedStageExecutionResult> execution
    )
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch
        {
            // Late faults after a hard timeout are already represented by the timeout result.
        }
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
        DateTimeOffset stageStartedAtUtc
    )
    {
        var retry = stage.FailureOptions.Retry;
        if (retry is null || !retry.ShouldRetry(error))
            return new RetryDecision(RetryDecisionKind.NotRetryable, 0, TimeSpan.Zero);

        if (attempt >= retry.MaxRetries)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        int nextAttempt = attempt + 1;
        var delay = retry.GetDelay(nextAttempt);
        var remaining = GetStageTimeoutRemaining(stage, stageStartedAtUtc);
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

    private async ValueTask DisposeComponentsAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _componentsDisposed, 1, 0) != 0)
            return;

        if (_sink is not null && ShouldDispose(_sink))
            await _sink.DisposeAsync().ConfigureAwait(false);

        for (int i = _spec.Stages.Count - 1; i >= 0; i--)
            await _spec.Stages[i].DisposeAsync(_spec.OwnershipOptions).ConfigureAwait(false);

        if (ShouldDispose(_spec.Source))
            await _spec.Source.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Emits a pipeline event to registered observers.
    /// </summary>
    /// <param name="pipelineEvent">The event to emit.</param>
    /// <param name="ct">A token that cancels the emission.</param>
    private async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        await _observerDispatcher.EmitAsync(pipelineEvent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a pipeline event for best-effort emission.
    /// </summary>
    /// <param name="pipelineEvent">The event to emit.</param>
    private void EmitEventFireAndForget(PipelineEvent pipelineEvent)
    {
        _ = TryEmitAsync(pipelineEvent).AsTask();
    }

    /// <summary>
    /// Emits a pipeline event without affecting the main run outcome if notification fails.
    /// </summary>
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

    private bool ShouldDispose(object component)
    {
        if (component is not IPipelineComponentDescriptor descriptor)
            return true;

        return descriptor.Lifetime != PipelineComponentLifetime.SingletonExternal
            || _spec.OwnershipOptions.DisposeExternalComponents;
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
                clock: new PipelineClockAdapter(_clock)
            );
        }

        return new CircuitBreaker(
            failureRatio: 0.5,
            samplingDuration: policy.BreakDuration,
            minimumThroughput: policy.FailureThreshold,
            breakDuration: policy.BreakDuration,
            maxHalfOpenRequests: 3,
            clock: new PipelineClockAdapter(_clock)
        );
    }
}

internal sealed class PipelineClockAdapter : IClock
{
    private readonly IPipelineClock _clock;

    public PipelineClockAdapter(IPipelineClock clock)
    {
        _clock = clock;
    }

    public DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
}
