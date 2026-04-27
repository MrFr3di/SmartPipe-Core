namespace SmartPipe.Core;

/// <summary>
/// Mutable context of a pipeline element. Thread-safe trace ID generation.
/// Supports ObjectPool reuse via Reset() — zero allocations in hot path.
/// </summary>
/// <typeparam name="T">Type of payload data.</typeparam>
public class ProcessingContext<T>
{
    private static long _counter;

    /// <summary>Unique trace ID across the entire pipeline (monotonic, thread-safe).</summary>
    public ulong TraceId { get; set; }

    /// <summary>Payload data to process.</summary>
    public T Payload { get; set; } = default!;

    /// <summary>Metadata dictionary for additional context.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>Timestamp (TickCount64) when the element entered the pipeline.</summary>
    public long EnterPipelineTicks { get; set; }

    // Data Lineage keys for Metadata dictionary
    public const string LineageSource = "lineage_source";
    public const string LineagePipeline = "lineage_pipeline";
    public const string LineageEnteredAt = "lineage_entered_at";
    public const string LineageTransform = "lineage_transform";

    /// <summary>Create an empty context (for ObjectPool).</summary>
    public ProcessingContext()
    {
        TraceId = (ulong)Interlocked.Increment(ref _counter);
        EnterPipelineTicks = Environment.TickCount64;
    }

    /// <summary>Create a new context with payload.</summary>
    public ProcessingContext(T payload) : this()
    {
        Payload = payload;
    }

    /// <summary>Create a new context with payload and metadata.</summary>
    public ProcessingContext(T payload, Dictionary<string, string>? metadata) : this(payload)
    {
        if (metadata != null)
        {
            foreach (var kv in metadata)
                Metadata[kv.Key] = kv.Value;
        }
    }

    /// <summary>Reset state for ObjectPool reuse. Generates new TraceId.</summary>
    public void Reset()
    {
        TraceId = (ulong)Interlocked.Increment(ref _counter);
        Payload = default!;
        Metadata.Clear();
        EnterPipelineTicks = Environment.TickCount64;
    }
}
