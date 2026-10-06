#nullable enable

using System.Collections.Concurrent;

namespace SmartPipe.Core;

internal sealed class PipelineComponentLifetimeManager<TInput, TOutput>
{
    private readonly PipelineActivationLedger _lifetime;
    private readonly LateStageAttemptRegistry _lateAttemptRegistry;
    private readonly ConcurrentDictionary<string, Func<ValueTask>> _deferredStageDisposals = [];
    private readonly object _disposeGate = new();
    private Task<PipelineComponentCleanupResult>? _disposeTask;

    public PipelineComponentLifetimeManager(
        PipelineActivationLedger lifetime,
        LateStageAttemptRegistry lateAttemptRegistry)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _lateAttemptRegistry = lateAttemptRegistry
            ?? throw new ArgumentNullException(nameof(lateAttemptRegistry));
    }

    public ValueTask<PipelineComponentCleanupResult> DisposeAsync()
    {
        TaskCompletionSource<PipelineComponentCleanupResult>? starter = null;
        Task<PipelineComponentCleanupResult> task;
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
            }

            task = _disposeTask;
        }

        if (starter is not null)
            _ = RunDisposeAsync(starter);

        return new(task);
    }

    private async Task RunDisposeAsync(TaskCompletionSource<PipelineComponentCleanupResult> completion)
    {
        try
        {
            completion.SetResult(await DisposeCoreAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
    }

    private async ValueTask<PipelineComponentCleanupResult> DisposeCoreAsync()
    {
        var lateAttemptErrors = await _lateAttemptRegistry.WaitForAllAsync().ConfigureAwait(false);
        var cleanupErrors = await _lifetime.DisposeAsync(DisposeLeaseAsync).ConfigureAwait(false);
        var disposeErrors = cleanupErrors.ToArray();
        return new(
            lateAttemptErrors.Concat(disposeErrors).ToArray(),
            disposeErrors);
    }

    private ValueTask DisposeLeaseAsync(
        ActivatedComponentLease lease,
        Func<ValueTask> cleanup)
    {
        if (lease.StageKey is { } stageKey
            && _lateAttemptRegistry.HasRunningAttempt(stageKey.Value))
        {
            _deferredStageDisposals.TryAdd(stageKey.Value, cleanup);
            return ValueTask.CompletedTask;
        }

        return cleanup();
    }

    public async ValueTask<Exception[]> DisposeDeferredStagesAsync()
    {
        if (_deferredStageDisposals.IsEmpty)
            return [];

        List<Exception>? errors = null;
        foreach (var (stageId, dispose) in _deferredStageDisposals.ToArray())
        {
            await _lateAttemptRegistry.WaitForStageAttemptsToCompleteAsync(stageId).ConfigureAwait(false);
            if (!_deferredStageDisposals.TryRemove(stageId, out _))
                continue;

            try
            {
                await dispose().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add(ex);
            }
        }

        return errors?.ToArray() ?? [];
    }
}

internal readonly record struct PipelineComponentCleanupResult(
    Exception[] CompletionErrors,
    Exception[] DisposeErrors);
