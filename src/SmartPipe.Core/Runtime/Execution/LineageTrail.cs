#nullable enable

using System.Collections;

namespace SmartPipe.Core;

/// <summary>
/// Immutable lineage view with amortized O(1) append.
/// </summary>
/// <remarks>
/// Views share one backing array. A view exposes only its own prefix, and a slot past that prefix is
/// written at most once by the first view that claims it; any later append from an older view copies
/// the prefix into a new array. Every published view therefore stays immutable while a linear stage
/// chain appends without re-copying its whole history per stage.
/// </remarks>
internal sealed class LineageTrail : IReadOnlyList<LineageEntry>
{
    private const int InitialCapacity = 4;

    private readonly Buffer _buffer;
    private readonly int _count;

    private LineageTrail(Buffer buffer, int count)
    {
        _buffer = buffer;
        _count = count;
    }

    public int Count => _count;

    public LineageEntry this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return _buffer.Items[index];
        }
    }

    public static IReadOnlyList<LineageEntry> Append(IReadOnlyList<LineageEntry> current, LineageEntry next)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        if (current is LineageTrail trail)
            return trail.Append(next);

        var count = current.Count;
        var items = new LineageEntry[Math.Max(InitialCapacity, count * 2)];
        for (var index = 0; index < count; index++)
            items[index] = current[index];

        items[count] = next;
        return new LineageTrail(new Buffer(items, count + 1), count + 1);
    }

    public IEnumerator<LineageEntry> GetEnumerator()
    {
        var items = _buffer.Items;
        for (var index = 0; index < _count; index++)
            yield return items[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private LineageTrail Append(LineageEntry next)
    {
        var buffer = _buffer;
        if (_count < buffer.Items.Length
            && Interlocked.CompareExchange(ref buffer.Used, _count + 1, _count) == _count)
        {
            buffer.Items[_count] = next;
            return new LineageTrail(buffer, _count + 1);
        }

        var items = new LineageEntry[Math.Max(InitialCapacity, _count * 2)];
        Array.Copy(buffer.Items, items, _count);
        items[_count] = next;
        return new LineageTrail(new Buffer(items, _count + 1), _count + 1);
    }

    private sealed class Buffer(LineageEntry[] items, int used)
    {
        public readonly LineageEntry[] Items = items;
        public int Used = used;
    }
}
