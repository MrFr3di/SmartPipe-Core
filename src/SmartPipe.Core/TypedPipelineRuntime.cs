#nullable enable

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
        IEnumerable<PipelineObserverRegistration>? observers = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        PipelineId = pipelineId;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        OwnershipOptions = ownershipOptions ?? ComponentOwnershipOptions.Default;
        LineageMode = lineageMode;
        IsFactoryBased = isFactoryBased;
        Observers = (observers ?? []).ToArray();
    }

    public string PipelineId { get; }

    public IPipelineSource<TInput> Source { get; }

    public ComponentOwnershipOptions OwnershipOptions { get; }

    public LineageMode LineageMode { get; }

    public IReadOnlyList<ITypedPipelineStage> Stages => _stages;

    public bool IsFactoryBased { get; }

    public IReadOnlyList<PipelineObserverRegistration> Observers { get; }

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
            Observers
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
            Observers.Concat([observer])
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
        CancellationToken ct
    );

    TypedStageExecutionResult CreateTimedOutResult(
        object envelope,
        LineageMode lineageMode,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout,
        Exception? exception
    );

    ValueTask<DeadLetterWriteResult> WriteDeadLetterAsync(
        object envelope,
        SmartPipeError error,
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
        CancellationToken ct
    )
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        var started = DateTimeOffset.UtcNow;
        var result = await _transformer.TransformAsync(input, ct).ConfigureAwait(false);
        if (!result.IsValid)
            throw new InvalidOperationException(
                "default(StageResult<T>) is invalid. Use StageResult factory methods."
            );

        var completed = DateTimeOffset.UtcNow;
        if (!result.IsSuccess)
        {
            var failedLineage = AppendLineage(
                input.Lineage,
                lineageMode,
                started,
                completed,
                ToOutcome(result.Kind),
                includeForError: true
            );
            return TypedStageExecutionResult.Terminal(
                result.Error
                    ?? new SmartPipeError(
                        result.Kind.ToString(),
                        ErrorType.Permanent,
                        result.Kind.ToString()
                    ),
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
            Attempt = input.Attempt,
            CreatedAtUtc = input.CreatedAtUtc,
        };

        return TypedStageExecutionResult.Success(next);
    }

    public TypedStageExecutionResult CreateTimedOutResult(
        object envelope,
        LineageMode lineageMode,
        DateTimeOffset startedAtUtc,
        TimeSpan timeout,
        Exception? exception
    )
    {
        var input = (ProcessingEnvelope<TInput>)envelope;
        var completed = DateTimeOffset.UtcNow;
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

    public async ValueTask<DeadLetterWriteResult> WriteDeadLetterAsync(
        object envelope,
        SmartPipeError error,
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
            FailedAtUtc = DateTimeOffset.UtcNow,
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
        SmartPipeError error,
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
    private readonly PipelineRuntime _runtime;
    private readonly TypedPipelineSpec<TInput, TOutput> _spec;
    private readonly IPipelineSink<TOutput>? _sink;
    private readonly Channel<PipelineOutput<TOutput>> _outputs = Channel.CreateUnbounded<
        PipelineOutput<TOutput>
    >();
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, CircuitBreaker> _breakers = [];
    private PipelineRunState _state = PipelineRunState.NotStarted;
    private int _disposed;
    private int _componentsDisposed;

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
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    public PipelineRun<TOutput> Start()
    {
        var completion = Task.Run(RunAsync, CancellationToken.None);
        return new PipelineRun<TOutput>(
            _outputs.Reader,
            completion,
            () => _state,
            CancelAsync,
            DrainAsync,
            AbortAsync,
            DisposeAsync
        );
    }

    public ValueTask CancelAsync(CancellationToken ct = default)
    {
        _state = PipelineRunState.Cancelled;
        _cts.Cancel();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _state = PipelineRunState.Draining;
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            while (!_outputs.Reader.Completion.IsCompleted)
                await Task.Delay(10, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _state = PipelineRunState.Cancelled;
        }
    }

    public ValueTask AbortAsync(CancellationToken ct = default)
    {
        _state = PipelineRunState.Aborted;
        _cts.Cancel();
        _outputs.Writer.TryComplete(new OperationCanceledException("Pipeline run aborted."));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        _cts.Cancel();
        await DisposeComponentsAsync(CancellationToken.None).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            _state = PipelineRunState.Running;
            await EmitAsync(
                    new PipelineStartedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        DateTimeOffset.UtcNow
                    ),
                    _cts.Token
                )
                .ConfigureAwait(false);
            await InitializeComponentsAsync(_cts.Token).ConfigureAwait(false);

            await foreach (
                var envelope in _spec.Source.ReadEnvelopesAsync(_cts.Token).ConfigureAwait(false)
            )
            {
                var action = await ProcessEnvelopeAsync(envelope, _cts.Token).ConfigureAwait(false);
                if (action == FailureAction.StopPipeline)
                    break;
            }

            _state = PipelineRunState.Completed;
            await EmitAsync(
                    new PipelineCompletedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        DateTimeOffset.UtcNow
                    ),
                    _cts.Token
                )
                .ConfigureAwait(false);
            _outputs.Writer.TryComplete();
        }
        catch (OperationCanceledException ex) when (_cts.IsCancellationRequested)
        {
            if (_state != PipelineRunState.Aborted)
                _state = PipelineRunState.Cancelled;

            await TryEmitAsync(
                    new PipelineCancelledEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        DateTimeOffset.UtcNow
                    )
                )
                .ConfigureAwait(false);
            _outputs.Writer.TryComplete(ex);
            throw;
        }
        catch (Exception ex)
        {
            _state = PipelineRunState.Faulted;
            await TryEmitAsync(
                    new PipelineFaultedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        DateTimeOffset.UtcNow,
                        ex
                    )
                )
                .ConfigureAwait(false);
            _outputs.Writer.TryComplete(ex);
            throw;
        }
        finally
        {
            await DisposeComponentsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask InitializeComponentsAsync(CancellationToken ct)
    {
        await _spec.Source.InitializeAsync(ct).ConfigureAwait(false);
        foreach (var stage in _spec.Stages)
            await stage.InitializeAsync(ct).ConfigureAwait(false);

        if (_sink is not null)
            await _sink.InitializeAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<FailureAction?> ProcessEnvelopeAsync(
        ProcessingEnvelope<TInput> sourceEnvelope,
        CancellationToken ct
    )
    {
        object current = NormalizeEnvelope(sourceEnvelope);
        foreach (var stage in _spec.Stages)
        {
            var breaker = GetOrCreateBreaker(stage);
            var stageStartedAtUtc = DateTimeOffset.UtcNow;
            while (true)
            {
                var correlation = stage.GetCorrelation(current);

                // Circuit breaker check — before each attempt (including retries)
                if (breaker is not null && !breaker.AllowRequest())
                {
                    var cbError = new SmartPipeError(
                        $"Circuit breaker is open for stage '{stage.StageId}'.",
                        ErrorType.Permanent,
                        "CircuitBreaker"
                    );
                    await EmitAsync(
                            new CircuitBreakerRejectedEvent(
                                _spec.PipelineId,
                                _runtime.RunId,
                                correlation.TraceId,
                                stage.StageId,
                                correlation.Attempt,
                                DateTimeOffset.UtcNow,
                                cbError
                            ),
                            ct
                        )
                        .ConfigureAwait(false);

                    var action = stage.FailureOptions.OnPermanentFailure;
                    if (action == FailureAction.DeadLetter)
                        await WriteDeadLetterAsync(stage, current, cbError, ct).ConfigureAwait(false);

                    if (action == FailureAction.Skip)
                        return action;

                    if (action == FailureAction.FaultPipeline)
                        throw new PipelineFailureActionException(
                            stage.StageId,
                            stage.StageName,
                            cbError
                        );

                    // For EmitFailureResult and StopPipeline, write terminal output
                    if (action == FailureAction.EmitFailureResult || action == FailureAction.StopPipeline)
                    {
                        var inputEnvelope = (ProcessingEnvelope<TInput>)current;
                        var terminalOutcome = TypedStageExecutionResult.Terminal(
                            cbError,
                            StageResultKind.Failure,
                            correlation.TraceId,
                            correlation.Attempt,
                            inputEnvelope.Lineage
                        );
                        await WriteTerminalAsync(terminalOutcome, ct).ConfigureAwait(false);
                    }
                    return action == FailureAction.StopPipeline ? action : null;
                }

                await EmitAsync(
                        new StageStartedEvent(
                            _spec.PipelineId,
                            _runtime.RunId,
                            correlation.TraceId,
                            stage.StageId,
                            stage.StageName,
                            correlation.Attempt,
                            DateTimeOffset.UtcNow
                        ),
                        ct
                    )
                    .ConfigureAwait(false);

                TypedStageExecutionResult outcome;
                try
                {
                    outcome = await ExecuteStageAttemptAsync(stage, current, stageStartedAtUtc, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await EmitAsync(
                            new StageFailedEvent(
                                _spec.PipelineId,
                                _runtime.RunId,
                                correlation.TraceId,
                                stage.StageId,
                                correlation.Attempt,
                                DateTimeOffset.UtcNow,
                                new SmartPipeError(
                                    ex.Message,
                                    ErrorType.Permanent,
                                    "StageException",
                                    ex
                                )
                            ),
                            ct
                        )
                        .ConfigureAwait(false);
                    throw;
                }

                if (!outcome.IsSuccess)
                {
                    // Record circuit breaker failure (per-attempt)
                    var wasOpen = breaker?.State == CircuitState.Open;
                    breaker?.RecordFailure();
                    var justOpened = breaker is not null && breaker.State == CircuitState.Open && !wasOpen;
                    if (justOpened)
                    {
                        await EmitAsync(
                                new CircuitBreakerOpenedEvent(
                                    _spec.PipelineId,
                                    _runtime.RunId,
                                    stage.StageId,
                                    DateTimeOffset.UtcNow
                                ),
                                ct
                            )
                            .ConfigureAwait(false);
                    }

                    var error =
                        outcome.Error
                        ?? new SmartPipeError(
                            outcome.Kind.ToString(),
                            ErrorType.Permanent,
                            outcome.Kind.ToString()
                        );
                    await EmitAsync(
                            new StageFailedEvent(
                                _spec.PipelineId,
                                _runtime.RunId,
                                outcome.TraceId,
                                stage.StageId,
                                outcome.Attempt,
                                DateTimeOffset.UtcNow,
                                error
                            ),
                            ct
                        )
                        .ConfigureAwait(false);

                    var decision = GetRetryDecision(stage, error, outcome.Attempt, stageStartedAtUtc);

                    if (decision.Kind == RetryDecisionKind.Retry)
                    {
                        await EmitRetryScheduledAsync(
                                stage,
                                outcome,
                                decision.NextAttempt,
                                decision.Delay,
                                error,
                                ct
                            )
                            .ConfigureAwait(false);
                        if (decision.Delay > TimeSpan.Zero)
                            await Task.Delay(decision.Delay, ct).ConfigureAwait(false);

                        current = stage.WithAttempt(current, decision.NextAttempt);
                        await EmitAsync(
                                new RetryAttemptedEvent(
                                    _spec.PipelineId,
                                    _runtime.RunId,
                                    outcome.TraceId,
                                    stage.StageId,
                                    decision.NextAttempt,
                                    DateTimeOffset.UtcNow
                                ),
                                ct
                            )
                            .ConfigureAwait(false);
                        continue;
                    }

                    var retryExhausted = decision.Kind == RetryDecisionKind.Exhausted;
                    if (retryExhausted)
                        await EmitRetryExhaustedAsync(stage, outcome, error, ct)
                            .ConfigureAwait(false);

                    var action = retryExhausted
                        ? stage.FailureOptions.OnRetryExhausted
                        : stage.FailureOptions.OnPermanentFailure;
                    if (action == FailureAction.DeadLetter)
                        await WriteDeadLetterAsync(stage, current, error, ct).ConfigureAwait(false);

                    if (action == FailureAction.Skip)
                        return action;

                    if (action == FailureAction.FaultPipeline)
                        throw new PipelineFailureActionException(
                            stage.StageId,
                            stage.StageName,
                            error
                        );

                    await WriteTerminalAsync(outcome, ct).ConfigureAwait(false);
                    return action;
                }

                // Record circuit breaker success
                breaker?.RecordSuccess();

                await EmitAsync(
                        new StageSucceededEvent(
                            _spec.PipelineId,
                            _runtime.RunId,
                            correlation.TraceId,
                            stage.StageId,
                            stage.StageName,
                            correlation.Attempt,
                            DateTimeOffset.UtcNow
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                current = outcome.Envelope!;
                break;
            }
        }

        var outputEnvelope = (ProcessingEnvelope<TOutput>)current;
        var result = ProcessingResult<TOutput>.Success(
            outputEnvelope.Payload,
            outputEnvelope.TraceId
        );
        await _outputs
            .Writer.WriteAsync(new PipelineOutput<TOutput>(outputEnvelope, result), ct)
            .ConfigureAwait(false);

        if (_sink is not null)
        {
            await EmitAsync(
                    new SinkWriteStartedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        outputEnvelope.TraceId,
                        outputEnvelope.Attempt,
                        DateTimeOffset.UtcNow
                    ),
                    ct
                )
                .ConfigureAwait(false);

            try
            {
                await _sink.WriteAsync(outputEnvelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await EmitAsync(
                        new SinkWriteFailedEvent(
                            _spec.PipelineId,
                            _runtime.RunId,
                            outputEnvelope.TraceId,
                            outputEnvelope.Attempt,
                            DateTimeOffset.UtcNow,
                            ex
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                throw;
            }
        }

        return null;
    }

    private async ValueTask EmitRetryScheduledAsync(
        ITypedPipelineStage stage,
        TypedStageExecutionResult outcome,
        int retryAttempt,
        TimeSpan delay,
        SmartPipeError error,
        CancellationToken ct
    )
    {
        await EmitAsync(
                new RetryScheduledEvent(
                    _spec.PipelineId,
                    _runtime.RunId,
                    outcome.TraceId,
                    stage.StageId,
                    retryAttempt,
                    DateTimeOffset.UtcNow,
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
                    DateTimeOffset.UtcNow,
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
            return await stage.ExecuteAsync(current, _spec.LineageMode, ct).ConfigureAwait(false);

        var startedAtUtc = DateTimeOffset.UtcNow;
        if (attemptTimeout <= TimeSpan.Zero)
            return stage.CreateTimedOutResult(
                current,
                _spec.LineageMode,
                startedAtUtc,
                TimeSpan.Zero,
                null
            );

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(attemptTimeout.Value);
        var execution = stage.ExecuteAsync(current, _spec.LineageMode, timeoutCts.Token).AsTask();

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
                startedAtUtc,
                attemptTimeout.Value,
                ex
            );
        }
    }

    private static TimeSpan? GetEffectiveAttemptTimeout(
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

    private static TimeSpan? GetStageTimeoutRemaining(
        ITypedPipelineStage stage,
        DateTimeOffset stageStartedAtUtc
    )
    {
        var stageTimeout = NormalizeTimeout(stage.FailureOptions.Timeout?.StageTimeout);
        if (stageTimeout is null)
            return null;

        var elapsed = DateTimeOffset.UtcNow - stageStartedAtUtc;
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
        var pipelineId = string.IsNullOrWhiteSpace(envelope.PipelineId)
            ? _spec.PipelineId
            : envelope.PipelineId;
        var runId = string.IsNullOrWhiteSpace(envelope.RunId) ? _runtime.RunId : envelope.RunId;

        if (pipelineId == envelope.PipelineId && runId == envelope.RunId)
            return envelope;

        return envelope with
        {
            PipelineId = pipelineId,
            RunId = runId,
        };
    }

    private enum RetryDecisionKind
    {
        NotRetryable,
        Retry,
        Exhausted,
    }

    private readonly record struct RetryDecision(
        RetryDecisionKind Kind,
        int NextAttempt,
        TimeSpan Delay
    );

    private static RetryDecision GetRetryDecision(
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

        if (remaining <= TimeSpan.Zero)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        if (delay >= remaining)
            return new RetryDecision(RetryDecisionKind.Exhausted, 0, TimeSpan.Zero);

        return new RetryDecision(RetryDecisionKind.Retry, nextAttempt, delay);
    }

    private async ValueTask WriteTerminalAsync(
        TypedStageExecutionResult outcome,
        CancellationToken ct
    )
    {
        var error =
            outcome.Error
            ?? new SmartPipeError(
                outcome.Kind.ToString(),
                ErrorType.Permanent,
                outcome.Kind.ToString()
            );
        var result = ProcessingResult<TOutput>.Failure(error, outcome.TraceId);
        await _outputs
            .Writer.WriteAsync(new PipelineOutput<TOutput>(null, result), ct)
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
            .WriteDeadLetterAsync(envelope, error, ct)
            .ConfigureAwait(false);
        await EmitAsync(
                new DeadLetterWrittenEvent(
                    _spec.PipelineId,
                    _runtime.RunId,
                    deadLetter.TraceId,
                    deadLetter.StageId,
                    deadLetter.StageName,
                    deadLetter.Attempt,
                    DateTimeOffset.UtcNow
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

    private async ValueTask EmitAsync(PipelineEvent pipelineEvent, CancellationToken ct)
    {
        foreach (var registration in _spec.Observers)
        {
            try
            {
                await registration.Observer.OnEventAsync(pipelineEvent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
                when (registration.FailurePolicy != ObserverFailurePolicy.FaultPipeline
                    && registration.Reliability != ObserverReliability.Critical
                )
            {
                await EmitObserverFailureAsync(registration, ex, ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask TryEmitAsync(PipelineEvent pipelineEvent)
    {
        try
        {
            await EmitAsync(pipelineEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Terminal notifications are best-effort and must not hide the primary run outcome.
        }
    }

    private async ValueTask EmitObserverFailureAsync(
        PipelineObserverRegistration failedRegistration,
        Exception exception,
        CancellationToken ct
    )
    {
        var failureEvent = new ObserverFailedEvent(
            _spec.PipelineId,
            _runtime.RunId,
            failedRegistration.Observer.GetType().Name,
            DateTimeOffset.UtcNow,
            exception
        );

        foreach (var registration in _spec.Observers)
        {
            if (ReferenceEquals(registration.Observer, failedRegistration.Observer))
                continue;

            try
            {
                await registration.Observer.OnEventAsync(failureEvent, ct).ConfigureAwait(false);
            }
            catch (Exception)
                when (registration.FailurePolicy != ObserverFailurePolicy.FaultPipeline
                    && registration.Reliability != ObserverReliability.Critical
                )
            {
                // Best-effort observer failure notifications must not recurse indefinitely.
            }
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
        if (!_breakers.TryGetValue(stage.StageId, out var breaker))
        {
            breaker = CreateBreaker(stage);
            if (breaker is not null)
                _breakers[stage.StageId] = breaker;
        }
        return breaker;
    }

    private static CircuitBreaker? CreateBreaker(ITypedPipelineStage stage)
    {
        var policy = stage.FailureOptions.CircuitBreaker;
        if (policy is null)
            return null;
        return new CircuitBreaker(
            failureRatio: 0.5,
            samplingDuration: policy.BreakDuration,
            minimumThroughput: policy.FailureThreshold,
            breakDuration: policy.BreakDuration,
            maxHalfOpenRequests: 3
        );
    }
}
