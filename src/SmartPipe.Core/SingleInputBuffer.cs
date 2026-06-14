#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class SingleInputBuffer<T> : IInputBuffer<T>
{
    private readonly Channel<T> _channel;
    private readonly int _capacity;
    private long _bufferedItems;

    public SingleInputBuffer(int capacity, BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");

        _capacity = capacity;
        _channel = Channel.CreateBounded<T>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = fullMode,
                SingleReader = false,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    public int ActiveLaneCount => 1;

    public int TotalLaneCount => 1;

    public InputBufferSnapshot CaptureSnapshot()
    {
        var bufferedItems = Math.Max(0, Volatile.Read(ref _bufferedItems));
        var pressure = CalculatePressure(bufferedItems, _capacity);

        return new InputBufferSnapshot(
            ActiveLaneCount: 1,
            TotalLaneCount: 1,
            ActiveBufferedItems: bufferedItems,
            InactiveBufferedItems: 0,
            TotalBufferedItems: bufferedItems,
            ActiveQueuePressure: pressure,
            TotalQueuePressure: pressure);
    }

    public IInputBufferReader<T> CreateReader(int readerIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readerIndex);
        return new Reader(this);
    }

    public async ValueTask WriteAsync(T item, CancellationToken ct)
    {
        while (await _channel.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _bufferedItems);
            if (_channel.Writer.TryWrite(item))
                return;

            Interlocked.Decrement(ref _bufferedItems);
        }

        throw new ChannelClosedException();
    }

    public void RequestActiveLaneCount(int activeLaneCount)
    {
        if (activeLaneCount != 1)
            throw new ArgumentOutOfRangeException(
                nameof(activeLaneCount),
                activeLaneCount,
                "Single input buffer always has exactly one active lane."
            );
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private static double CalculatePressure(long bufferedItems, int capacity) =>
        capacity <= 0 ? 0 : Math.Min(1.0, Math.Max(0, (double)bufferedItems / capacity));

    private sealed class Reader(SingleInputBuffer<T> owner) : IInputBufferReader<T>
    {
        public async ValueTask<T> ReadAsync(CancellationToken ct)
        {
            var item = await owner._channel.Reader.ReadAsync(ct).ConfigureAwait(false);
            Interlocked.Decrement(ref owner._bufferedItems);
            return item;
        }
    }
}
