namespace SmartPipe.Core.Tests.Resilience;

/// <summary>Requires complete stress work and preserves its original failure after bounded best-effort cleanup.</summary>
internal static class StressWorkload
{
    internal static async Task WaitForCompletionAsync(
        Task work,
        CancellationTokenSource workers,
        TimeSpan deadline,
        CancellationToken testCancellation)
    {
        try
        {
            await work.WaitAsync(deadline, testCancellation);
            testCancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            Task cancellation;
            try
            {
                cancellation = workers.CancelAsync();
            }
            catch (Exception)
            {
                // A cancellation request failure must not replace the initiating error or skip worker cleanup.
                cancellation = Task.CompletedTask;
            }

            var cleanup = Task.WhenAll(work, cancellation);
            try
            {
                // Bound the whole cleanup, including non-cooperative cancellation callbacks.
                await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // Cleanup is best-effort; always rethrow the initiating failure below.
            }
            finally
            {
                ObserveFault(work);
                ObserveFault(cancellation);
                ObserveFault(cleanup);
            }
            throw;
        }
    }

    private static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        // Non-cooperative tasks may finish after the test; observe any eventual faults without awaiting them forever.
        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
