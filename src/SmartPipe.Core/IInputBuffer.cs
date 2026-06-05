#nullable enable

namespace SmartPipe.Core;

internal interface IInputBuffer<T> : IAsyncDisposable
{
    int ActiveLaneCount { get; }

    int TotalLaneCount { get; }

    InputBufferSnapshot CaptureSnapshot();

    IInputBufferReader<T> CreateReader(int readerIndex);

    ValueTask WriteAsync(T item, CancellationToken ct);

    void RequestActiveLaneCount(int activeLaneCount);

    void Complete(Exception? error = null);
}

internal interface IInputBufferReader<T>
{
    ValueTask<T> ReadAsync(CancellationToken ct);
}

internal readonly record struct InputBufferSnapshot(
    int ActiveLaneCount,
    int TotalLaneCount,
    long ActiveBufferedItems,
    long InactiveBufferedItems,
    long TotalBufferedItems,
    double ActiveQueuePressure,
    double TotalQueuePressure);
