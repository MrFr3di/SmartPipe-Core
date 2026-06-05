#nullable enable

using System.Threading.Channels;

namespace SmartPipe.Core;

internal sealed class AdaptiveChannelSet<T> : IInputBuffer<T>
{
    private readonly Lane[] _lanes;
    private readonly int _totalCapacity;
    private long _nextWriteIndex = -1;
    private int _activeLaneCount;

    public AdaptiveChannelSet(
        int capacity,
        int totalLaneCount,
        int initialActiveLaneCount,
        BoundedChannelFullMode fullMode)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
        if (totalLaneCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(totalLaneCount),
                totalLaneCount,
                "Total lane count must be greater than zero."
            );
        if (totalLaneCount > capacity)
            throw new ArgumentOutOfRangeException(
                nameof(totalLaneCount),
                totalLaneCount,
                "Total lane count cannot exceed capacity."
            );
        if (initialActiveLaneCount < 1 || initialActiveLaneCount > totalLaneCount)
            throw new ArgumentOutOfRangeException(
                nameof(initialActiveLaneCount),
                initialActiveLaneCount,
                "Initial active lane count must be between one and total lane count."
            );

        _totalCapacity = capacity;
        _activeLaneCount = initialActiveLaneCount;
        _lanes = CreateLanes(capacity, totalLaneCount, fullMode);
    }

    public int ActiveLaneCount => Volatile.Read(ref _activeLaneCount);

    public int TotalLaneCount => _lanes.Length;

    public InputBufferSnapshot CaptureSnapshot()
    {
        var activeLaneCount = ActiveLaneCount;
        long activeBufferedItems = 0;
        long inactiveBufferedItems = 0;
        var activeCapacity = 0;

        for (var i = 0; i < _lanes.Length; i++)
        {
            var lane = _lanes[i];
            var bufferedItems = Math.Max(0, Volatile.Read(ref lane.BufferedItems));

            if (i < activeLaneCount)
            {
                activeBufferedItems += bufferedItems;
                activeCapacity += lane.Capacity;
            }
            else
            {
                inactiveBufferedItems += bufferedItems;
            }
        }

        var totalBufferedItems = activeBufferedItems + inactiveBufferedItems;

        return new InputBufferSnapshot(
            activeLaneCount,
            _lanes.Length,
            activeBufferedItems,
            inactiveBufferedItems,
            totalBufferedItems,
            CalculatePressure(activeBufferedItems, activeCapacity),
            CalculatePressure(totalBufferedItems, _totalCapacity));
    }

    public IInputBufferReader<T> CreateReader(int readerIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(readerIndex);
        return new Reader(this, readerIndex);
    }

    public async ValueTask WriteAsync(T item, CancellationToken ct)
    {
        while (true)
        {
            var activeLaneCount = ActiveLaneCount;
            var laneIndex = (int)(Interlocked.Increment(ref _nextWriteIndex) % activeLaneCount);
            var lane = _lanes[laneIndex];

            if (!await lane.Channel.Writer.WaitToWriteAsync(ct).ConfigureAwait(false))
                throw new ChannelClosedException();

            Interlocked.Increment(ref lane.BufferedItems);
            if (lane.Channel.Writer.TryWrite(item))
                return;

            Interlocked.Decrement(ref lane.BufferedItems);
        }
    }

    public void RequestActiveLaneCount(int activeLaneCount)
    {
        if (activeLaneCount < 1 || activeLaneCount > _lanes.Length)
            throw new ArgumentOutOfRangeException(
                nameof(activeLaneCount),
                activeLaneCount,
                "Active lane count must be between one and total lane count."
            );

        Volatile.Write(ref _activeLaneCount, activeLaneCount);
    }

    public void Complete(Exception? error = null)
    {
        foreach (var lane in _lanes)
            lane.Channel.Writer.TryComplete(error);
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private static Lane[] CreateLanes(
        int capacity,
        int totalLaneCount,
        BoundedChannelFullMode fullMode)
    {
        var baseCapacity = capacity / totalLaneCount;
        var remainder = capacity % totalLaneCount;
        var lanes = new Lane[totalLaneCount];

        for (var i = 0; i < lanes.Length; i++)
        {
            var laneCapacity = baseCapacity + (i < remainder ? 1 : 0);
            lanes[i] = new Lane(ChannelPool.CreateBoundedMultiReaderMultiWriter<T>(laneCapacity, fullMode), laneCapacity);
        }

        return lanes;
    }

    private static double CalculatePressure(long bufferedItems, int capacity) =>
        capacity <= 0 ? 0 : Math.Min(1.0, Math.Max(0, (double)bufferedItems / capacity));

    private bool TryReadFromAnyLane(int readerIndex, out T item)
    {
        var laneCount = _lanes.Length;
        var start = readerIndex % laneCount;

        for (var offset = 0; offset < laneCount; offset++)
        {
            var laneIndex = (start + offset) % laneCount;
            if (!ShouldReadLane(laneIndex))
                continue;

            var lane = _lanes[laneIndex];
            if (!lane.Channel.Reader.TryRead(out item!))
                continue;

            Interlocked.Decrement(ref lane.BufferedItems);
            return true;
        }

        item = default!;
        return false;
    }

    private bool ShouldReadLane(int laneIndex) =>
        laneIndex < ActiveLaneCount || Volatile.Read(ref _lanes[laneIndex].BufferedItems) > 0;

    private async ValueTask WaitForReadableLaneAsync(int readerIndex, CancellationToken ct)
    {
        var laneCount = _lanes.Length;
        var start = readerIndex % laneCount;
        var waitTasks = new List<Task<bool>>(laneCount);

        for (var offset = 0; offset < laneCount; offset++)
        {
            var laneIndex = (start + offset) % laneCount;
            if (ShouldReadLane(laneIndex))
                waitTasks.Add(_lanes[laneIndex].Channel.Reader.WaitToReadAsync(ct).AsTask());
        }

        if (waitTasks.Count == 0)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return;
        }

        while (waitTasks.Count > 0)
        {
            var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);
            waitTasks.Remove(completed);

            if (await completed.ConfigureAwait(false))
                return;
        }

        throw new ChannelClosedException();
    }

    private sealed class Reader(AdaptiveChannelSet<T> owner, int readerIndex) : IInputBufferReader<T>
    {
        public async ValueTask<T> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (owner.TryReadFromAnyLane(readerIndex, out var item))
                    return item;

                await owner.WaitForReadableLaneAsync(readerIndex, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class Lane(Channel<T> channel, int capacity)
    {
        public Channel<T> Channel { get; } = channel;

        public int Capacity { get; } = capacity;

        public long BufferedItems;
    }
}
