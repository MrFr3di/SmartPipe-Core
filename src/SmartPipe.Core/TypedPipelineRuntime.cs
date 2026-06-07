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
        var result = await _transformer.TransformAsync(input, ct).ConfigureAwait(false);
        if (!result.IsValid)
            throw new InvalidOperationException(
                "default(StageResult<T>) is invalid. Use StageResult factory methods."
            );

        var completed = clock.GetUtcNow();
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
            Attempt = 0,
            CreatedAtUtc = input.CreatedAtUtc,
        };

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
    // Prevents scheduling retries when the remaining StageTimeout budget is too small
    // to execute a meaningful next attempt after retry delay.
    private static readonly TimeSpan MinimumRetryAttemptBudget = TimeSpan.FromMilliseconds(5);

    private readonly PipelineRuntime _runtime;
    private readonly TypedPipelineSpec<TInput, TOutput> _spec;
    private readonly IPipelineSink<TOutput>? _sink;
    private readonly PipelineRuntimeOptions _options;
    private readonly IPipelineClock _clock;
    private readonly Channel<PipelineOutput<TOutput>> _outputs;
    private readonly IPipelineObserverDispatcher _observerDispatcher;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sinkWriteGate = new(1, 1);
    private readonly Dictionary<string, CircuitBreaker> _breakers = [];
    private readonly object _breakersGate = new();
    private PipelineRunState _state = PipelineRunState.NotStarted;
    private int _disposed;
    private int _componentsDisposed;
    private Task? _runTask;
    private int _drainRequested;
    private int _stopAcceptingRequested;

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
        _outputs = CreateOutputChannel(_options);
        _observerDispatcher = PipelineObserverDispatcher.Create(
            _spec.Observers,
            _options.ObserverDispatch,
            _clock
        );
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    private static Channel<PipelineOutput<TOutput>> CreateOutputChannel(PipelineRuntimeOptions options)
    {
        options.Validate();
        var singleWriter = options.MaxDegreeOfParallelism == 1;
        if (options.OutputCapacity is null)
        {
            return Channel.CreateUnbounded<PipelineOutput<TOutput>>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = singleWriter,
                }
            );
        }

        return Channel.CreateBounded<PipelineOutput<TOutput>>(
            new BoundedChannelOptions(options.OutputCapacity.Value)
            {
                FullMode = options.OutputFullMode,
                SingleReader = false,
                SingleWriter = singleWriter,
            }
        );
    }

    public PipelineRun<TOutput> Start()
    {
        _runTask = Task.Run(RunAsync, CancellationToken.None);
        return new PipelineRun<TOutput>(
            _outputs.Reader,
            _runTask,
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
        RequestDrain();
        var runTask = _runTask ?? throw new InvalidOperationException("Pipeline run has not started.");
        if (!runTask.IsCompleted)
            _state = PipelineRunState.Draining;

        await runTask.WaitAsync(timeout, ct).ConfigureAwait(false);

        if (_state == PipelineRunState.Draining)
            _state = PipelineRunState.Completed;
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
        await _observerDispatcher.DisposeAsync().ConfigureAwait(false);
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
                        _clock.GetUtcNow()
                    ),
                    _cts.Token
                )
                .ConfigureAwait(false);
            await InitializeComponentsAsync(_cts.Token).ConfigureAwait(false);

            if (_options.MaxDegreeOfParallelism == 1)
                await RunSequentialProcessingAsync(_cts.Token).ConfigureAwait(false);
            else
                await RunParallelProcessingAsync(_cts.Token).ConfigureAwait(false);

            _state = PipelineRunState.Completed;
            await EmitAsync(
                    new PipelineCompletedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        _clock.GetUtcNow()
                    ),
                    _cts.Token
                )
                .ConfigureAwait(false);
            await _observerDispatcher.CompleteAsync(_cts.Token).ConfigureAwait(false);
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
                        _clock.GetUtcNow()
                    )
                )
                .ConfigureAwait(false);
            await TryCompleteObserversAsync().ConfigureAwait(false);
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
            await DisposeComponentsAsync(CancellationToken.None).ConfigureAwait(false);
            await _observerDispatcher.DisposeAsync().ConfigureAwait(false);
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

    internal void RequestDrain() => Volatile.Write(ref _drainRequested, 1);

    private void RequestStopAccepting() => Volatile.Write(ref _stopAcceptingRequested, 1);

    private bool ShouldStopAccepting()
    {
        return Volatile.Read(ref _drainRequested) != 0
            || Volatile.Read(ref _stopAcceptingRequested) != 0;
    }

    private async ValueTask RunSequentialProcessingAsync(CancellationToken ct)
    {
        var enumerator = _spec.Source
            .ReadEnvelopesAsync(ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (!ShouldStopAccepting() && await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var envelope = enumerator.Current;
                var action = await ProcessEnvelopeAsync(envelope, ct).ConfigureAwait(false);
                if (action == FailureAction.StopPipeline)
                {
                    RequestStopAccepting();
                    break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask RunParallelProcessingAsync(CancellationToken ct)
    {
        var input = Channel.CreateBounded<ProcessingEnvelope<TInput>>(
            new BoundedChannelOptions(_options.MaxDegreeOfParallelism)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            }
        );

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
            .Range(0, _options.MaxDegreeOfParallelism)
            .Select(_ => Task.Run(
                () => RunParallelWorkerAsync(input.Reader, input.Writer, RecordWorkerFailure, ct),
                CancellationToken.None
            ))
            .ToArray();

        try
        {
            await ProduceParallelInputAsync(input.Writer, ct).ConfigureAwait(false);
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

    private async Task ProduceParallelInputAsync(
        ChannelWriter<ProcessingEnvelope<TInput>> writer,
        CancellationToken ct)
    {
        var enumerator = _spec.Source
            .ReadEnvelopesAsync(ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (!ShouldStopAccepting() && await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                await writer.WriteAsync(enumerator.Current, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunParallelWorkerAsync(
        ChannelReader<ProcessingEnvelope<TInput>> reader,
        ChannelWriter<ProcessingEnvelope<TInput>> writer,
        Action<Exception> recordFailure,
        CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var action = await ProcessEnvelopeAsync(envelope, ct).ConfigureAwait(false);
                if (action == FailureAction.StopPipeline)
                    RequestStopAccepting();
            }
        }
        catch (Exception ex)
        {
            recordFailure(ex);
            writer.TryComplete(ex);
            throw;
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
            var stageStartedAtUtc = _clock.GetUtcNow();
            while (true)
            {
                var correlation = stage.GetCorrelation(current);

                // Circuit breaker check — before each attempt (including retries)
                if (breaker is not null && !breaker.AllowRequest())
                {
                    var cbError = new SmartPipeError(
                        $"Circuit breaker is open for stage '{stage.StageId}'.",
                        ErrorType.Transient,
                        "CircuitBreaker"
                    );
                    var rejectedOutcome = stage.CreateFailureResult(
                        current,
                        cbError,
                        StageResultKind.Failure,
                        _spec.LineageMode,
                        _clock,
                        stageStartedAtUtc
                    );
                    await EmitAsync(
                            new CircuitBreakerRejectedEvent(
                                _spec.PipelineId,
                                _runtime.RunId,
                                correlation.TraceId,
                                stage.StageId,
                                correlation.Attempt,
                                _clock.GetUtcNow(),
                                cbError
                            ),
                            ct
                            )
                            .ConfigureAwait(false);

                    var decision = GetRetryDecision(stage, cbError, correlation.Attempt, stageStartedAtUtc);
                    if (decision.Kind == RetryDecisionKind.Retry)
                    {
                        await EmitRetryScheduledAsync(
                                stage,
                                rejectedOutcome,
                                decision.NextAttempt,
                                decision.Delay,
                                cbError,
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
                                    rejectedOutcome.TraceId,
                                    stage.StageId,
                                    decision.NextAttempt,
                                    _clock.GetUtcNow()
                                ),
                                ct
                            )
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (decision.Kind == RetryDecisionKind.Exhausted)
                        await EmitRetryExhaustedAsync(stage, rejectedOutcome, cbError, ct)
                            .ConfigureAwait(false);

                    var action = stage.FailureOptions.OnRetryExhausted;
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

                    if (action == FailureAction.EmitFailureResult || action == FailureAction.StopPipeline || action == FailureAction.DeadLetter)
                    {
                        await WriteTerminalAsync(rejectedOutcome, ct).ConfigureAwait(false);
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
                            _clock.GetUtcNow()
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
                                _clock.GetUtcNow(),
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
                                    _clock.GetUtcNow()
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
                                _clock.GetUtcNow(),
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
                                    _clock.GetUtcNow()
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
                            _clock.GetUtcNow()
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
        if (ShouldEmitOutput(result))
        {
            await _outputs
                .Writer.WriteAsync(new PipelineOutput<TOutput>(outputEnvelope, result), ct)
                .ConfigureAwait(false);
        }

        if (_sink is not null)
            await WriteSinkAsync(outputEnvelope, ct).ConfigureAwait(false);

        return null;
    }

    private async ValueTask WriteSinkAsync(
        ProcessingEnvelope<TOutput> outputEnvelope,
        CancellationToken ct)
    {
        await _sinkWriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EmitAsync(
                    new SinkWriteStartedEvent(
                        _spec.PipelineId,
                        _runtime.RunId,
                        outputEnvelope.TraceId,
                        outputEnvelope.Attempt,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);

            try
            {
                await _sink!.WriteAsync(outputEnvelope, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await EmitAsync(
                        new SinkWriteFailedEvent(
                            _spec.PipelineId,
                            _runtime.RunId,
                            outputEnvelope.TraceId,
                            outputEnvelope.Attempt,
                            _clock.GetUtcNow(),
                            ex
                        ),
                        ct
                    )
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _sinkWriteGate.Release();
        }
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
        var error =
            outcome.Error
            ?? new SmartPipeError(
                outcome.Kind.ToString(),
                ErrorType.Permanent,
                outcome.Kind.ToString()
            );
        var result = ProcessingResult<TOutput>.Failure(error, outcome.TraceId);
        if (ShouldEmitOutput(result))
        {
            await _outputs
                .Writer.WriteAsync(new PipelineOutput<TOutput>(null, result), ct)
                .ConfigureAwait(false);
        }
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

    private bool ShouldEmitOutput(ProcessingResult<TOutput> result)
    {
        return _options.OutputMode switch
        {
            PipelineOutputMode.EmitAll => true,
            PipelineOutputMode.FailuresOnlyWhenSinkAttached => _sink is null || !result.IsSuccess,
            PipelineOutputMode.SuppressWhenSinkAttached => _sink is null,
            PipelineOutputMode.SuppressAll => false,
            _ => throw new InvalidOperationException(
                $"Unsupported output mode '{_options.OutputMode}'."),
        };
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
        await _observerDispatcher.EmitAsync(pipelineEvent, ct).ConfigureAwait(false);
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
