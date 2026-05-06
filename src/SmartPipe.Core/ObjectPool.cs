#nullable enable
using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Lock-free object pool with ABA-safe Rent/Return.
/// Uses version stamps to prevent ABA race conditions.</summary>
/// <typeparam name="T">Type of pooled objects.</typeparam>
public class ObjectPool<T> where T : class
{
    private readonly T?[] _items;
    private readonly int[] _versions;
    private readonly Func<T> _factory;
    private int _index;

    /// <summary>Creates a new object pool with pre-allocated objects.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <param name="capacity">Maximum pool capacity.</param>
    /// <exception cref="ArgumentNullException">Thrown when factory is null.</exception>
    public ObjectPool(Func<T> factory, int capacity = 256)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _items = new T[capacity];
        _versions = new int[capacity];
        for (int i = 0; i < capacity; i++) _items[i] = _factory();
        _index = capacity;
    }

    /// <summary>Rents an object from the pool or creates a new one if empty.</summary>
    /// <returns>A pooled or new object.</returns>
    /// <remarks>Uses version stamps to prevent ABA race conditions.</remarks>
    public T Rent()
    {
        const int maxRetries = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            int i = Interlocked.Decrement(ref _index);
            if (i < 0 || i >= _items.Length)
            {
                Interlocked.Increment(ref _index);
                return _factory(); // Pool exhausted
            }

            // Read version before attempting exchange
            int version = Volatile.Read(ref _versions[i]);
            T? obj = Interlocked.Exchange(ref _items[i], null);

            if (obj != null)
            {
                // Verify version hasn't changed (ABA check)
                int currentVersion = Volatile.Read(ref _versions[i]);
                if (version == currentVersion)
                    return obj;

                // ABA detected: slot was recycled while we were exchanging.
                // Put object back and retry with a different slot.
                Interlocked.Exchange(ref _items[i], obj);
                // Don't increment version here - the object was never successfully rented
            }

            // Slot was empty or ABA detected - release our claim on this slot
            Interlocked.Increment(ref _index);
        }

        return _factory(); // Max retries exceeded, create new
    }

    /// <summary>Returns an object to the pool for reuse.</summary>
    /// <param name="item">Object to return.</param>
    /// <exception cref="ArgumentNullException">Thrown when item is null.</exception>
    /// <remarks>Increments version stamp to prevent ABA issues.</remarks>
    public void Return(T item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        int i = Interlocked.Increment(ref _index) - 1;
        if (i >= 0 && i < _items.Length)
        {
            // Store item first, then increment version as atomic commit
            Volatile.Write(ref _items[i], item);
            Interlocked.Increment(ref _versions[i]);
        }
        else
        {
            Interlocked.Decrement(ref _index);
        }
    }
}
