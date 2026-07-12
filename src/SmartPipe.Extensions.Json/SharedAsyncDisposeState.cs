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
