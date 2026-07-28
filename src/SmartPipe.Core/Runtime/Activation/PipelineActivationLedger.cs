#nullable enable

namespace SmartPipe.Core;

internal sealed class PipelineActivationLedger
{
    private readonly object _gate = new();
    private readonly List<ActivatedComponentLease> _leases = [];
    private Task<IReadOnlyList<Exception>>? _cleanupTask;

    public void Append(ActivatedComponentLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.Role);
        if (!Enum.IsDefined(lease.Ownership))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lease.Ownership),
                lease.Ownership,
                "Component ownership is invalid.");
        }

        if (lease.StageKey is { } stageKey)
            PipelineStageKeyGuard.ThrowIfInvalid(stageKey, nameof(lease.StageKey));

        if (lease.Ownership == PipelineComponentOwnership.RuntimeOwned)
            ArgumentNullException.ThrowIfNull(lease.RuntimeOwnedCleanup);
        else if (lease.RuntimeOwnedCleanup is not null)
            throw new ArgumentException("Only runtime-owned leases may define cleanup.", nameof(lease));

        lock (_gate)
        {
            if (_cleanupTask is not null)
                throw new InvalidOperationException("Cannot append a lease after cleanup has started.");

            _leases.Add(lease);
        }
    }

    public ValueTask<IReadOnlyList<Exception>> RollbackAsync() => CleanupAsync();

    public ValueTask<IReadOnlyList<Exception>> DisposeAsync() => CleanupAsync();

    internal ValueTask<IReadOnlyList<Exception>> DisposeAsync(
        Func<ActivatedComponentLease, Func<ValueTask>, ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return CleanupAsync(cleanup);
    }

    private ValueTask<IReadOnlyList<Exception>> CleanupAsync(
        Func<ActivatedComponentLease, Func<ValueTask>, ValueTask>? cleanupInvoker = null)
    {
        TaskCompletionSource<IReadOnlyList<Exception>>? starter = null;
        ActivatedComponentLease[]? leases = null;
        Task<IReadOnlyList<Exception>> task;

        lock (_gate)
        {
            if (_cleanupTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cleanupTask = starter.Task;
                leases = _leases.ToArray();
            }

            task = _cleanupTask;
        }

        if (starter is not null)
            _ = CompleteCleanupAsync(starter, leases!, cleanupInvoker);

        return new(task);
    }

    private static async Task CompleteCleanupAsync(
        TaskCompletionSource<IReadOnlyList<Exception>> completion,
        ActivatedComponentLease[] leases,
        Func<ActivatedComponentLease, Func<ValueTask>, ValueTask>? cleanupInvoker)
    {
        try
        {
            List<Exception>? errors = null;
            for (var index = leases.Length - 1; index >= 0; index--)
            {
                var lease = leases[index];
                var cleanup = lease.RuntimeOwnedCleanup;
                if (cleanup is null)
                    continue;

                try
                {
                    if (cleanupInvoker is null)
                        await cleanup().ConfigureAwait(false);
                    else
                        await cleanupInvoker(lease, cleanup).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    errors ??= [];
                    errors.Add(error);
                }
            }

            IReadOnlyList<Exception> result =
                errors is null ? Array.Empty<Exception>() : errors.AsReadOnly();
            completion.SetResult(result);
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
    }
}
