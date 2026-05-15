#nullable enable

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Two-Stage Reservoir Sampling (Algorithm R).
/// Maintains a representative sample of size k from an infinite stream in O(k) memory.
/// Thread-safe: uses ThreadLocal of Random for concurrent access to random number generation.</summary>
/// <typeparam name="T">Type of items to sample.</typeparam>
/// <remarks>Each item has equal probability of being in the final sample.</remarks>
public class ReservoirSampler<T>
{
    private readonly T[] _reservoir;
    private readonly ThreadLocal<Random> _rng;
    private long _count;
    private readonly Lock _reservoirLock = new(); // Protects reservoir array from concurrent writes

    /// <summary>Gets the sample capacity.</summary>
    public int Capacity => _reservoir.Length;

    /// <summary>Gets the total number of items processed.</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>Gets the current sample array (read-only reference).</summary>
    public T[] Sample => _reservoir;

    /// <summary>Creates a new reservoir sampler.</summary>
    /// <param name="capacity">Maximum sample size (default: 1000).</param>
    public ReservoirSampler(int capacity = 1000)
    {
        _reservoir = new T[capacity];
        _rng = new ThreadLocal<Random>(() => new Random());
    }

    /// <summary>Adds an item to the sample using reservoir sampling algorithm.</summary>
    /// <param name="item">Item to potentially include in the sample.</param>
    /// <remarks>First 'capacity' items are stored directly, then probabilistic replacement.</remarks>
    public void Add(T item)
    {
        long n = Interlocked.Increment(ref _count);
        if (n <= _reservoir.Length)
        {
            lock (_reservoirLock)
            {
                _reservoir[n - 1] = item;
            }
            return;
        }
        // ThreadLocal<Random>.Value always returns non-null because factory creates Random on first access
        Random rng = _rng.Value!;
        if (rng.NextDouble() < (double)_reservoir.Length / n)
        {
            lock (_reservoirLock)
            {
                _reservoir[rng.Next(_reservoir.Length)] = item;
            }
        }
    }

    /// <summary>Resets the sampler, clearing all data.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        lock (_reservoirLock)
        {
            Array.Clear(_reservoir);
        }
    }
}