#nullable enable

using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Two-Stage Reservoir Sampling (Algorithm R).
/// Maintains a representative sample of size k from an infinite stream in O(k) memory.
/// Thread-safe: sampling operations are synchronized.</summary>
/// <typeparam name="T">Type of items to sample.</typeparam>
/// <remarks>Each item has equal probability of being in the final sample.</remarks>
public class ReservoirSampler<T>
{
    private readonly T[] _reservoir;
    private long _count;
    private readonly Lock _reservoirLock = new();

    /// <summary>Gets the sample capacity.</summary>
    public int Capacity => _reservoir.Length;

    /// <summary>Gets the total number of items processed.</summary>
    public long Count
    {
        get
        {
            lock (_reservoirLock)
            {
                return _count;
            }
        }
    }

    /// <summary>Gets a snapshot of the populated sample entries.</summary>
    public T[] Sample => GetSampleSnapshot();

    /// <summary>Creates a new reservoir sampler.</summary>
    /// <param name="capacity">Maximum sample size (default: 1000).</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is not positive.</exception>
    public ReservoirSampler(int capacity = 1000)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");

        _reservoir = new T[capacity];
    }

    /// <summary>Adds an item to the sample using reservoir sampling algorithm.</summary>
    /// <param name="item">Item to potentially include in the sample.</param>
    /// <remarks>First 'capacity' items are stored directly, then probabilistic replacement.</remarks>
    public void Add(T item)
    {
        lock (_reservoirLock)
        {
            _count++;
            long n = _count;

            if (n <= _reservoir.Length)
            {
                _reservoir[(int)n - 1] = item;
                return;
            }

            if (Random.Shared.NextDouble() < (double)_reservoir.Length / n)
            {
                _reservoir[Random.Shared.Next(_reservoir.Length)] = item;
            }
        }
    }

    /// <summary>Returns a snapshot copy of the currently populated sample entries.</summary>
    /// <returns>Snapshot containing at most <see cref="Capacity"/> populated entries.</returns>
    internal T[] GetSampleSnapshot()
    {
        lock (_reservoirLock)
        {
            int populatedCount = (int)Math.Min(_count, _reservoir.Length);
            var snapshot = new T[populatedCount];
            Array.Copy(_reservoir, snapshot, populatedCount);
            return snapshot;
        }
    }

    /// <summary>Resets the sampler, clearing all data.</summary>
    public void Reset()
    {
        lock (_reservoirLock)
        {
            _count = 0;
            Array.Clear(_reservoir);
        }
    }
}
