namespace SmartPipe.Core.Tests.Resilience;

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
            await workers.CancelAsync();
            try
            {
                // Ignore completed worker failures during cleanup, preserving the initiating error.
                await work.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception) when (work.IsCompleted)
            {
            }
            finally
            {
                if (!work.IsCompleted)
                {
                    // A non-cooperative worker must not make the test wait forever or leave faults unobserved.
                    _ = work.ContinueWith(static task => _ = task.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            throw;
        }
    }
}
