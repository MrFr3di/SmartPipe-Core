#nullable enable

namespace SmartPipe.Core;

internal sealed class StageExecutor
{
    private readonly string _pipelineId;
    private readonly string _runId;
    private readonly LineageMode _lineageMode;
    private readonly IPipelineClock _clock;
    private readonly Func<ITypedPipelineStage, CircuitBreaker?> _getBreaker;
    private readonly Func<
        ITypedPipelineStage,
        SmartPipeError,
        int,
        DateTimeOffset,
        RetryDecision> _getRetryDecision;
    private readonly Func<
        ITypedPipelineStage,
        TypedStageExecutionResult,
        int,
        TimeSpan,
        SmartPipeError,
        CancellationToken,
        ValueTask> _emitRetryScheduledAsync;
    private readonly Func<
        ITypedPipelineStage,
        TypedStageExecutionResult,
        SmartPipeError,
        CancellationToken,
        ValueTask> _emitRetryExhaustedAsync;
    private readonly Func<
        ITypedPipelineStage,
        object,
        SmartPipeError,
        CancellationToken,
        ValueTask> _writeDeadLetterAsync;
    private readonly Func<TypedStageExecutionResult, CancellationToken, ValueTask> _writeTerminalAsync;
    private readonly Func<PipelineEvent, CancellationToken, ValueTask> _emitAsync;
    private readonly Func<
        ITypedPipelineStage,
        object,
        DateTimeOffset,
        CancellationToken,
        ValueTask<TypedStageExecutionResult>> _executeStageAttemptAsync;

    public StageExecutor(
        string pipelineId,
        string runId,
        LineageMode lineageMode,
        IPipelineClock clock,
        Func<ITypedPipelineStage, CircuitBreaker?> getBreaker,
        Func<ITypedPipelineStage, SmartPipeError, int, DateTimeOffset, RetryDecision> getRetryDecision,
        Func<
            ITypedPipelineStage,
            TypedStageExecutionResult,
            int,
            TimeSpan,
            SmartPipeError,
            CancellationToken,
            ValueTask> emitRetryScheduledAsync,
        Func<
            ITypedPipelineStage,
            TypedStageExecutionResult,
            SmartPipeError,
            CancellationToken,
            ValueTask> emitRetryExhaustedAsync,
        Func<ITypedPipelineStage, object, SmartPipeError, CancellationToken, ValueTask> writeDeadLetterAsync,
        Func<TypedStageExecutionResult, CancellationToken, ValueTask> writeTerminalAsync,
        Func<PipelineEvent, CancellationToken, ValueTask> emitAsync,
        Func<
            ITypedPipelineStage,
            object,
            DateTimeOffset,
            CancellationToken,
            ValueTask<TypedStageExecutionResult>> executeStageAttemptAsync)
    {
        _pipelineId = pipelineId ?? throw new ArgumentNullException(nameof(pipelineId));
        _runId = runId ?? throw new ArgumentNullException(nameof(runId));
        _lineageMode = lineageMode;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _getBreaker = getBreaker ?? throw new ArgumentNullException(nameof(getBreaker));
        _getRetryDecision = getRetryDecision ?? throw new ArgumentNullException(nameof(getRetryDecision));
        _emitRetryScheduledAsync = emitRetryScheduledAsync
            ?? throw new ArgumentNullException(nameof(emitRetryScheduledAsync));
        _emitRetryExhaustedAsync = emitRetryExhaustedAsync
            ?? throw new ArgumentNullException(nameof(emitRetryExhaustedAsync));
        _writeDeadLetterAsync = writeDeadLetterAsync
            ?? throw new ArgumentNullException(nameof(writeDeadLetterAsync));
        _writeTerminalAsync = writeTerminalAsync
            ?? throw new ArgumentNullException(nameof(writeTerminalAsync));
        _emitAsync = emitAsync ?? throw new ArgumentNullException(nameof(emitAsync));
        _executeStageAttemptAsync = executeStageAttemptAsync
            ?? throw new ArgumentNullException(nameof(executeStageAttemptAsync));
    }

    public async ValueTask<StageExecutionResult> ExecuteAsync(
        ITypedPipelineStage stage,
        object current,
        CancellationToken ct)
    {
        var breaker = _getBreaker(stage);
        var stageStartedAtUtc = _clock.GetUtcNow();
        while (true)
        {
            var correlation = stage.GetCorrelation(current);
            var breakerProbe = default(CircuitBreakerProbe);
            var hasBreakerProbe = false;

            if (breaker is not null)
            {
                var state = breaker.State;
                var allowed =
                    state == CircuitState.Open || state == CircuitState.HalfOpen
                        ? breaker.TryAcquireHalfOpenProbe(out breakerProbe)
                        : breaker.AllowRequest();
                hasBreakerProbe = state == CircuitState.Open || state == CircuitState.HalfOpen;

                if (!allowed)
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
                        _lineageMode,
                        _clock,
                        stageStartedAtUtc
                    );
                    await _emitAsync(
                            new CircuitBreakerRejectedEvent(
                                _pipelineId,
                                _runId,
                                correlation.TraceId,
                                stage.StageId,
                                correlation.Attempt,
                                _clock.GetUtcNow(),
                                cbError
                            ),
                            ct
                        )
                        .ConfigureAwait(false);

                    if (stage.FailureOptions.Retry is not null)
                        await _emitRetryExhaustedAsync(stage, rejectedOutcome, cbError, ct)
                            .ConfigureAwait(false);

                    var action = stage.FailureOptions.Retry is not null
                        ? stage.FailureOptions.OnRetryExhausted
                        : stage.FailureOptions.OnPermanentFailure;
                    return await CompleteTerminalFailureAsync(
                            stage,
                            current,
                            rejectedOutcome,
                            cbError,
                            action,
                            ct)
                        .ConfigureAwait(false);
                }
            }

            await _emitAsync(
                    new StageStartedEvent(
                        _pipelineId,
                        _runId,
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
                outcome = await _executeStageAttemptAsync(stage, current, stageStartedAtUtc, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _emitAsync(
                        new StageFailedEvent(
                            _pipelineId,
                            _runId,
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
            finally
            {
                if (hasBreakerProbe)
                    breakerProbe.Dispose();
            }

            if (outcome.IsTerminalNonFailure)
            {
                await RecordBreakerSuccessAsync(breaker, stage, ct).ConfigureAwait(false);
                await CompleteTerminalNonFailureAsync(stage, outcome, ct)
                    .ConfigureAwait(false);
                return new StageExecutionResult(current, null, StopProcessing: true);
            }

            if (!outcome.IsSuccess)
            {
                var failure = await HandleFailureAsync(
                        stage,
                        current,
                        outcome,
                        breaker,
                        stageStartedAtUtc,
                        ct)
                    .ConfigureAwait(false);
                if (failure.ShouldRetry)
                {
                    current = failure.Envelope!;
                    continue;
                }

                return failure.Result;
            }

            await RecordBreakerSuccessAsync(breaker, stage, ct).ConfigureAwait(false);

            await _emitAsync(
                    new StageSucceededEvent(
                        _pipelineId,
                        _runId,
                        correlation.TraceId,
                        stage.StageId,
                        stage.StageName,
                        correlation.Attempt,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);

            return new StageExecutionResult(outcome.Envelope!, null, StopProcessing: false);
        }
    }

    private async ValueTask RecordBreakerSuccessAsync(
        CircuitBreaker? breaker,
        ITypedPipelineStage stage,
        CancellationToken ct)
    {
        if (breaker is null)
            return;

        var wasHalfOpen = breaker.State == CircuitState.HalfOpen;
        breaker.RecordSuccess();
        if (wasHalfOpen && breaker.State == CircuitState.Closed)
        {
            await _emitAsync(
                    new CircuitBreakerClosedEvent(
                        _pipelineId,
                        _runId,
                        stage.StageId,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<StageFailureHandlingResult> HandleFailureAsync(
        ITypedPipelineStage stage,
        object current,
        TypedStageExecutionResult outcome,
        CircuitBreaker? breaker,
        DateTimeOffset stageStartedAtUtc,
        CancellationToken ct)
    {
        var wasOpen = breaker?.State == CircuitState.Open;
        breaker?.RecordFailure();
        var justOpened = breaker is not null && breaker.State == CircuitState.Open && !wasOpen;
        if (justOpened)
        {
            await _emitAsync(
                    new CircuitBreakerOpenedEvent(
                        _pipelineId,
                        _runId,
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
        await _emitAsync(
                new StageFailedEvent(
                    _pipelineId,
                    _runId,
                    outcome.TraceId,
                    stage.StageId,
                    outcome.Attempt,
                    _clock.GetUtcNow(),
                    error
                ),
                ct
            )
            .ConfigureAwait(false);

        if (justOpened)
        {
            if (stage.FailureOptions.Retry is not null)
                await _emitRetryExhaustedAsync(stage, outcome, error, ct).ConfigureAwait(false);

            return StageFailureHandlingResult.Complete(
                await CompleteTerminalFailureAsync(
                        stage,
                        current,
                        outcome,
                        error,
                        stage.FailureOptions.Retry is not null
                            ? stage.FailureOptions.OnRetryExhausted
                            : stage.FailureOptions.OnPermanentFailure,
                        ct)
                    .ConfigureAwait(false));
        }

        var retry = await TryRetryAsync(
                stage,
                current,
                outcome,
                error,
                outcome.Attempt,
                stageStartedAtUtc,
                ct
            )
            .ConfigureAwait(false);
        if (retry.ShouldRetry)
            return StageFailureHandlingResult.Retry(retry.Envelope!);

        if (retry.Exhausted)
            await _emitRetryExhaustedAsync(stage, outcome, error, ct).ConfigureAwait(false);

        var action = retry.Exhausted
            ? stage.FailureOptions.OnRetryExhausted
            : stage.FailureOptions.OnPermanentFailure;
        var result = await CompleteTerminalFailureAsync(
                stage,
                current,
                outcome,
                error,
                action,
                ct)
            .ConfigureAwait(false);

        return StageFailureHandlingResult.Complete(result);
    }

    private async ValueTask<StageExecutionResult> CompleteTerminalFailureAsync(
        ITypedPipelineStage stage,
        object current,
        TypedStageExecutionResult outcome,
        SmartPipeError error,
        FailureAction action,
        CancellationToken ct)
    {
        if (action == FailureAction.DeadLetter)
            await _writeDeadLetterAsync(stage, current, error, ct).ConfigureAwait(false);

        if (action == FailureAction.Skip)
            return new StageExecutionResult(current, action, StopProcessing: true);

        if (action == FailureAction.FaultPipeline)
            throw new PipelineFailureActionException(
                stage.StageId,
                stage.StageName,
                error
            );

        await _writeTerminalAsync(outcome, ct).ConfigureAwait(false);
        return new StageExecutionResult(
            current,
            action == FailureAction.StopPipeline ? action : null,
            StopProcessing: true);
    }

    private async ValueTask CompleteTerminalNonFailureAsync(
        ITypedPipelineStage stage,
        TypedStageExecutionResult outcome,
        CancellationToken ct)
    {
        if (outcome.Kind == StageResultKind.Filtered)
        {
            await _emitAsync(
                    new ItemFilteredEvent(
                        _pipelineId,
                        _runId,
                        outcome.TraceId,
                        stage.StageId,
                        stage.StageName,
                        outcome.Attempt,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);
        }

        await _writeTerminalAsync(outcome, ct).ConfigureAwait(false);
    }

    private async ValueTask<RetryStageResult> TryRetryAsync(
        ITypedPipelineStage stage,
        object current,
        TypedStageExecutionResult outcome,
        SmartPipeError error,
        int attempt,
        DateTimeOffset stageStartedAtUtc,
        CancellationToken ct)
    {
        var decision = _getRetryDecision(stage, error, attempt, stageStartedAtUtc);
        if (decision.Kind == RetryDecisionKind.Retry)
        {
            await _emitRetryScheduledAsync(
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

            var next = stage.WithAttempt(current, decision.NextAttempt);
            await _emitAsync(
                    new RetryAttemptedEvent(
                        _pipelineId,
                        _runId,
                        outcome.TraceId,
                        stage.StageId,
                        decision.NextAttempt,
                        _clock.GetUtcNow()
                    ),
                    ct
                )
                .ConfigureAwait(false);
            return new RetryStageResult(true, false, next);
        }

        return new RetryStageResult(false, decision.Kind == RetryDecisionKind.Exhausted, null);
    }

    private readonly record struct RetryStageResult(
        bool ShouldRetry,
        bool Exhausted,
        object? Envelope);

    private readonly record struct StageFailureHandlingResult(
        bool ShouldRetry,
        object? Envelope,
        StageExecutionResult Result)
    {
        public static StageFailureHandlingResult Retry(object envelope)
        {
            return new StageFailureHandlingResult(true, envelope, default);
        }

        public static StageFailureHandlingResult Complete(StageExecutionResult result)
        {
            return new StageFailureHandlingResult(false, null, result);
        }
    }
}

internal readonly record struct StageExecutionResult(
    object Envelope,
    FailureAction? FailureAction,
    bool StopProcessing);

internal enum RetryDecisionKind
{
    NotRetryable,
    Retry,
    Exhausted,
}

internal readonly record struct RetryDecision(
    RetryDecisionKind Kind,
    int NextAttempt,
    TimeSpan Delay
);
