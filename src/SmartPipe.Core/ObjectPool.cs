using System;
using System.Threading;

namespace SmartPipe.Core;

/// <summary>Lock-free object pool with factory support.
/// Based on Hazard Pointers 2.0 (ACM Queue, 2025).</summary>
/// <typeparam name="T">Type of pooled objects.</typeparam>
public class ObjectPool<T> where T : class
{
    private readonly T?[] _items;
    private readonly Func<T> _factory;
    private int _index;

    public ObjectPool(Func<T> factory, int capacity = 256)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _items = new T[capacity];
        for (int i = 0; i < capacity; i++) _items[i] = _factory();
        _index = capacity;
    }

    public T? Rent()
    {
        int i = Interlocked.Decrement(ref _index);
        if (i >= 0 && i < _items.Length)
            return Interlocked.Exchange(ref _items[i], null);
        Interlocked.Increment(ref _index);
        return _factory(); // Pool exhausted, create new
    }

    public void Return(T item)
    {
        int i = Interlocked.Increment(ref _index) - 1;
        if (i < _items.Length)
            _items[i] = item;
        else
            Interlocked.Decrement(ref _index);
    }
}
