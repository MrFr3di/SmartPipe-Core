namespace SmartPipe.Core;

/// <summary>Immutable context of a pipeline element. Thread-safe trace ID generation.</summary>
/// <typeparam name="T">Type of payload data.</typeparam>
public record class ProcessingContext<T>
{
    private static long _counter;

    /// <summary>Unique trace ID across the entire pipeline (monotonic, thread-safe).</summary>
    public ulong TraceId { get; init; }

    /// <summary>Payload data to process.</summary>
    public T Payload { get; init; }

    /// <summary>Metadata dictionary for additional context (source file name, timestamp, etc.).</summary>
    public Dictionary<string, string> Metadata { get; init; }

    /// <summary>Timestamp (TickCount64) when the element entered the pipeline.</summary>
    public long EnterPipelineTicks { get; init; }

    /// <summary>Create a new context with auto-generated TraceId.</summary>
    /// <param name="payload">Payload data.</param>
    /// <param name="metadata">Optional metadata dictionary (copied, not referenced).</param>
    public ProcessingContext(T payload, Dictionary<string, string>? metadata = null)
    {
        TraceId = (ulong)Interlocked.Increment(ref _counter);
        Payload = payload;
        Metadata = metadata is null ? new() : new Dictionary<string, string>(metadata); // Copy to ensure immutability
        EnterPipelineTicks = Environment.TickCount64;
    }

    /// <summary>Add metadata entry, returning a new immutable instance.</summary>
    /// <param name="key">Metadata key.</param>
    /// <param name="value">Metadata value.</param>
    public ProcessingContext<T> AddMetadata(string key, string value) =>
        this with { Metadata = new Dictionary<string, string>(Metadata) { [key] = value } };
}
