using System.Runtime.ExceptionServices;

namespace SmartPipe.Extensions;

internal sealed class SharedAsyncDisposeState
{
    private readonly object _gate = new();
    private Task? _task;

    public Task GetOrStart(Func<Task> disposeCore)
    {
        ArgumentNullException.ThrowIfNull(disposeCore);
        TaskCompletionSource? starter = null;
        Task task;

        lock (_gate)
        {
            if (_task is null)
            {
                starter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _task = starter.Task;
            }

            task = _task;
        }

        if (starter is not null)
            _ = RunAsync(starter, disposeCore);

        return task;
    }

    public static void ThrowIfFailed(Exception? primaryFailure, Exception? cleanupFailure)
    {
        if (primaryFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(
                "Async finalization failed and resource cleanup also failed.",
                primaryFailure,
                cleanupFailure);
        }

        var failure = primaryFailure ?? cleanupFailure;
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task RunAsync(TaskCompletionSource completion, Func<Task> disposeCore)
    {
        try
        {
            await disposeCore().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }
}
