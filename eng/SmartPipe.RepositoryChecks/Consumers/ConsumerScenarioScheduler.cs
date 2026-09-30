using System.Runtime.ExceptionServices;

namespace SmartPipe.RepositoryChecks.Consumers;

internal static class ConsumerScenarioScheduler
{
    internal static async Task<IReadOnlyList<ConsumerScenarioResult>> RunAsync(
        IReadOnlyList<ConsumerScenario> scenarios,
        int maxParallelism,
        Func<ConsumerScenario, CancellationToken, Task<ConsumerScenarioResult>> runScenario,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxParallelism);
        var results = new ConsumerScenarioResult[scenarios.Count];
        using var nativeAotGate = new SemaphoreSlim(1, 1);
        ExceptionDispatchInfo? failure = null;
        try
        {
            // ForEachAsync creates bounded workers, rather than a task for every scenario.
            await Parallel.ForEachAsync(
                Enumerable.Range(0, scenarios.Count),
                new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct },
                async (index, workerToken) =>
                {
                    try
                    {
                        var scenario = scenarios[index];
                        if (scenario.Mode == ConsumerMode.PublishNativeAot)
                        {
                            await nativeAotGate.WaitAsync(workerToken).ConfigureAwait(false);
                            try
                            {
                                workerToken.ThrowIfCancellationRequested();
                                results[index] = await runScenario(scenario, workerToken).ConfigureAwait(false);
                            }
                            finally { nativeAotGate.Release(); }
                        }
                        else
                        {
                            workerToken.ThrowIfCancellationRequested();
                            results[index] = await runScenario(scenario, workerToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        // Retain the initiating failure before ForEachAsync cancels sibling workers.
                        Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
                        throw;
                    }
                }).ConfigureAwait(false);
        }
        catch when (failure is not null)
        {
            // ForEachAsync has awaited every worker, including process cancellation cleanup.
            failure.Throw();
            throw;
        }
        return results;
    }
}
