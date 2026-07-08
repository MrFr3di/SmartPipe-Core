#nullable enable

using System.Collections.Concurrent;

namespace SmartPipe.Core;

internal sealed class LateStageAttemptRegistry
{
    private readonly PipelineTime _time;
    private readonly ConcurrentDictionary<long, LateStageAttempt> _attempts = [];
    private readonly object _registrationGate = new();
    private long _nextAttemptId;
    private bool _sealed;

    public LateStageAttemptRegistry(PipelineTime time)
    {
        _time = time;
    }

    public void Register(
        string stageId,
        string stageName,
        ulong traceId,
        int attempt,
        Task execution,
        CancellationTokenSource timeoutCancellation,
        TimeSpan finalizationTimeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(timeoutCancellation);

        LateStageAttempt lateAttempt;
        lock (_registrationGate)
        {
            if (_sealed)
            {
                _ = ObserveRejectedAttemptAsync(execution, timeoutCancellation);
                throw new InvalidOperationException(
                    "Late stage attempt registration occurred after registry sealing.");
            }

            var id = ++_nextAttemptId;
            lateAttempt = new LateStageAttempt(
                id,
                stageId,
                stageName,
                traceId,
                attempt,
                execution,
                timeoutCancellation,
                finalizationTimeout);

            if (!_attempts.TryAdd(id, lateAttempt))
            {
                _ = ObserveRejectedAttemptAsync(execution, timeoutCancellation);
                throw new InvalidOperationException(
                    "Late stage attempt registration failed.");
            }
        }

        _ = ObserveLateStageAttemptAsync(lateAttempt);
    }

    public void Seal()
    {
        lock (_registrationGate)
            _sealed = true;
    }

    public bool HasRunningAttempt(string stageId)
    {
        foreach (var attempt in _attempts.Values)
        {
            if (attempt.StageId == stageId && !attempt.Execution.IsCompleted)
                return true;
        }

        return false;
    }

    public async ValueTask<Exception[]> WaitForAllAsync()
    {
        Seal();

        var attempts = _attempts.Values.ToArray();
        if (attempts.Length == 0)
            return [];

        var waits = attempts.Select(WaitForLateStageAttemptAsync).ToArray();
        try
        {
            await Task.WhenAll(waits).ConfigureAwait(false);
        }
        catch
        {
            // Faults are collected below so cleanup can continue.
        }

        List<Exception>? errors = null;
        foreach (var wait in waits)
        {
            if (!wait.IsFaulted || wait.Exception is null)
                continue;

            errors ??= [];
            errors.AddRange(wait.Exception.InnerExceptions);
        }

        return errors?.ToArray() ?? [];
    }

    public async Task WaitForStageAttemptsToCompleteAsync(string stageId)
    {
        while (true)
        {
            var attempts = _attempts.Values
                .Where(attempt => attempt.StageId == stageId && !attempt.Execution.IsCompleted)
                .Select(attempt => attempt.Execution)
                .ToArray();

            if (attempts.Length == 0)
                return;

            try
            {
                await Task.WhenAll(attempts).ConfigureAwait(false);
            }
            catch
            {
                // The timeout result remains the observable stage outcome.
            }
        }
    }

    private async Task ObserveLateStageAttemptAsync(LateStageAttempt attempt)
    {
        try
        {
            await ObserveCompletedLateStageExecutionAsync(attempt.Execution).ConfigureAwait(false);
        }
        finally
        {
            _attempts.TryRemove(attempt.Id, out _);
            attempt.TimeoutCancellation.Dispose();
        }
    }

    private static async Task ObserveRejectedAttemptAsync(
        Task execution,
        CancellationTokenSource timeoutCancellation)
    {
        try
        {
            await ObserveCompletedLateStageExecutionAsync(execution).ConfigureAwait(false);
        }
        finally
        {
            timeoutCancellation.Dispose();
        }
    }

    private async Task WaitForLateStageAttemptAsync(LateStageAttempt attempt)
    {
        if (attempt.Execution.IsCompleted)
        {
            await ObserveCompletedLateStageExecutionAsync(attempt.Execution).ConfigureAwait(false);
            return;
        }

        if (attempt.FinalizationTimeout == Timeout.InfiniteTimeSpan)
        {
            await ObserveCompletedLateStageExecutionAsync(attempt.Execution).ConfigureAwait(false);
            return;
        }

        try
        {
            await _time.WaitAsync(attempt.Execution, attempt.FinalizationTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Late stage attempt {attempt.StageId}#{attempt.Attempt} for trace {attempt.TraceId} did not complete within {attempt.FinalizationTimeout}.",
                ex);
        }
        catch
        {
            // The timeout result remains the observable stage outcome.
        }
    }

    private static async Task ObserveCompletedLateStageExecutionAsync(Task execution)
    {
        try
        {
            await execution.ConfigureAwait(false);
        }
        catch
        {
            // Late faults after a timeout are already represented by the timeout result.
        }
    }

    private sealed record LateStageAttempt(
        long Id,
        string StageId,
        string StageName,
        ulong TraceId,
        int Attempt,
        Task Execution,
        CancellationTokenSource TimeoutCancellation,
        TimeSpan FinalizationTimeout);
}
