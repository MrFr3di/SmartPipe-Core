#nullable enable
using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>
/// Thread-safe object pool that retains reusable objects up to a configured maximum retained capacity.
/// </summary>
/// <typeparam name="T">Type of pooled objects.</typeparam>
public class ObjectPool<T>
    where T : class
{
    private const int DefaultCapacity = 256;
    private const int DefaultMaxCapacity = 1024;

    private readonly Lock _gate = new();
    private readonly T[] _items;
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private int _count;

    /// <summary>Creates a new object pool with pre-allocated objects.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="factory"/> returns null.</exception>
    public ObjectPool(Func<T> factory)
        : this(factory, capacity: DefaultCapacity, maxCapacity: DefaultMaxCapacity)
    {
    }

    /// <summary>Creates a new object pool with pre-allocated objects.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <param name="capacity">Initial pool capacity (number of pre-allocated objects).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="factory"/> returns null.</exception>
    public ObjectPool(Func<T> factory, int capacity)
        : this(factory, reset: null, capacity, maxCapacity: Math.Max(DefaultMaxCapacity, capacity))
    {
    }

    /// <summary>Creates a new object pool with pre-allocated objects.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <param name="capacity">Initial pool capacity (number of pre-allocated objects).</param>
    /// <param name="maxCapacity">Maximum number of objects retained by the pool. This does not limit how many objects can be created over the pool lifetime.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> or <paramref name="maxCapacity"/> is negative, or <paramref name="maxCapacity"/> is less than <paramref name="capacity"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="factory"/> returns null.</exception>
#pragma warning disable RS0027 // Existing 1.x optional constructor preserved for source compatibility.
    public ObjectPool(Func<T> factory, int capacity = DefaultCapacity, int maxCapacity = DefaultMaxCapacity)
        : this(factory, reset: null, capacity, maxCapacity)
    {
    }
#pragma warning restore RS0027

    /// <summary>Creates a new object pool with pre-allocated objects and an optional reset callback.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <param name="reset">Optional callback invoked before an object is stored for reuse.</param>
    /// <param name="capacity">Initial pool capacity (number of pre-allocated objects).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="factory"/> returns null.</exception>
    public ObjectPool(Func<T> factory, Action<T>? reset, int capacity)
        : this(factory, reset, capacity, maxCapacity: Math.Max(DefaultMaxCapacity, capacity))
    {
    }

    /// <summary>Creates a new object pool with pre-allocated objects and an optional reset callback.</summary>
    /// <param name="factory">Factory function to create new objects.</param>
    /// <param name="reset">Optional callback invoked before an object is stored for reuse.</param>
    /// <param name="capacity">Initial pool capacity (number of pre-allocated objects).</param>
    /// <param name="maxCapacity">Maximum number of objects retained by the pool. This does not limit how many objects can be created over the pool lifetime.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="factory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> or <paramref name="maxCapacity"/> is negative, or <paramref name="maxCapacity"/> is less than <paramref name="capacity"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="factory"/> returns null.</exception>
    public ObjectPool(Func<T> factory, Action<T>? reset, int capacity, int maxCapacity)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _reset = reset;

        if (capacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maxCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCapacity));
        }

        if (maxCapacity < capacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCapacity),
                "Maximum retained capacity must be greater than or equal to the initial capacity.");
        }

        _items = new T[maxCapacity];
        for (int i = 0; i < capacity; i++)
        {
            _items[i] = Create();
        }

        _count = capacity;
    }

    /// <summary>Rents an object from the pool or creates a new one if empty.</summary>
    /// <returns>A pooled or new object.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the factory returns null.</exception>
    /// <remarks>When the retained pool is empty, the factory creates a new object outside the pool lock.</remarks>
    public T Rent()
    {
        T? item = null;

        lock (_gate)
        {
            if (_count > 0)
            {
                _count--;
                item = _items[_count];
                _items[_count] = null!;
            }
        }

        return item ?? Create();
    }

    /// <summary>Returns an object to the pool for reuse.</summary>
    /// <param name="item">Object to return.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="item"/> is null.</exception>
    /// <remarks>The reset callback runs outside the pool lock. If reset throws or the retained pool is full, the item is not retained.</remarks>
    public void Return(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _reset?.Invoke(item);

        lock (_gate)
        {
            if (_count < _items.Length)
            {
                _items[_count] = item;
                _count++;
            }
        }
    }

    private T Create()
    {
        T item = _factory();
        if (item is null)
        {
            throw new InvalidOperationException("Object pool factory returned null.");
        }

        return item;
    }
}
