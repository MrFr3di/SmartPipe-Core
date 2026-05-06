#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core.Tests.Infrastructure;

/// <summary>Deterministic simulator for testing multi-threaded pipeline scenarios without real race conditions.
/// Based on FoundationDB and MSR Coyote testing approaches.</summary>
public class PipelineSimulator
{
    private readonly Random _rng;
    private int _step;

    /// <summary>Current simulation step.</summary>
    public int Step => _step;

    /// <summary>Create simulator with optional seed for reproducibility.</summary>
    /// <param name="seed">Random seed for deterministic behavior.</param>
    public PipelineSimulator(int? seed = null)
    {
        _rng = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>Simulate processing delay.</summary>
    /// <param name="minMs">Minimum delay in milliseconds.</param>
    /// <param name="maxMs">Maximum delay in milliseconds.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task SimulateDelayAsync(int minMs = 1, int maxMs = 10, CancellationToken ct = default)
    {
        int delay = _rng.Next(minMs, maxMs);
        Interlocked.Increment(ref _step);
        await Task.Delay(delay, ct);
    }

    /// <summary>Simulate a transient failure with given probability.</summary>
    /// <param name="failureProbability">Probability of failure in [0.0, 1.0].</param>
    /// <returns>True if a failure should be simulated.</returns>
    public bool SimulateFailure(double failureProbability = 0.1)
    {
        Interlocked.Increment(ref _step);
        return _rng.NextDouble() < failureProbability;
    }

    /// <summary>Generate simulated items.</summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <param name="factory">Factory function taking index and returning item.</param>
    /// <param name="count">Number of items to generate.</param>
    /// <returns>Async enumerable of processing contexts.</returns>
    public async IAsyncEnumerable<ProcessingContext<T>> GenerateAsync<T>(
        Func<int, T> factory, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Interlocked.Increment(ref _step);
            yield return new ProcessingContext<T>(factory(i));
            await Task.Yield();
        }
    }

    /// <summary>Reset simulation state.</summary>
    public void Reset() => _step = 0;
}
