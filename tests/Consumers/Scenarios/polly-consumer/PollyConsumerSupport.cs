using SmartPipe.Core;

namespace SmartPipe.Consumers.Resilience;

/// <summary>Fails the first attempt for every payload, then multiplies it, recording lifecycle calls.</summary>
sealed class FlakyMultiplier(int factor) : IPipelineTransformer<int, int>
{
    private readonly Lock _gate = new();
    private readonly HashSet<ulong> _failedOnce = [];
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);
    public int InitializeCount { get; private set; }
    public int DisposeCount { get; private set; }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        InitializeCount++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _attempts);
        bool firstAttempt;
        lock (_gate)
            firstAttempt = _failedOnce.Add(envelope.TraceId);

        return firstAttempt
            ? ValueTask.FromException<StageResult<int>>(new InvalidOperationException("transient"))
            : ValueTask.FromResult(StageResult<int>.Success(envelope.Payload * factor));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Always throws, so the final exception reaches the configured mapper.</summary>
sealed class AlwaysFailing : IPipelineTransformer<int, int>
{
    public int Attempts { get; private set; }
    public int DisposeCount { get; private set; }

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
    {
        Attempts++;
        return ValueTask.FromException<StageResult<int>>(new TimeoutException($"attempt {Attempts}"));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Collects every payload written by the pipeline.</summary>
sealed class CollectingSink : IPipelineSink<int>
{
    private readonly List<int> _items = [];

    public IReadOnlyList<int> Items => _items;

    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default)
    {
        lock (_items)
            _items.Add(envelope.Payload);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

static class ConsumerSource
{
    public static PipelineComponent<IPipelineSource<int>> Of(params int[] values) =>
        PipelineComponent.RuntimeOwned<IPipelineSource<int>>((_, _) =>
            ValueTask.FromResult<IPipelineSource<int>>(PipelineSource.FromAsyncEnumerable(ReadAsync(values))));

    private static async IAsyncEnumerable<int> ReadAsync(int[] values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return value;
        }
    }
}

static class ConsumerCheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
