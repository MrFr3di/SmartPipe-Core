using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions;

/// <summary>Physical layout used by JSON file sources and sinks.</summary>
public enum JsonFileFormat
{
    /// <summary>Detect a root array or a sequence of top-level JSON values.</summary>
    Auto = 0,
    /// <summary>One root JSON array.</summary>
    Array = 1,
    /// <summary>One JSON value per record.</summary>
    Ndjson = 2,
    /// <summary>One JSON array per flushed record.</summary>
    BatchJsonLines = 3,
}

/// <summary>Behavior when an independently framed JSON record is invalid.</summary>
public enum InvalidJsonRecordBehavior
{
    /// <summary>Stop reading and throw.</summary>
    Throw = 0,
    /// <summary>Log the invalid record and continue at the next safe record boundary.</summary>
    SkipAndLog = 1,
}

/// <summary>How a JSON output file is opened.</summary>
public enum JsonFileOpenMode
{
    /// <summary>Create or replace the output file.</summary>
    Create = 0,
    /// <summary>Append to an existing output file.</summary>
    Append = 1,
}

/// <summary>Options for <see cref="Selectors.JsonFileSource{T}"/>.</summary>
public sealed record JsonFileSourceOptions
{
    /// <summary>Gets the input layout.</summary>
    public JsonFileFormat Format { get; init; } = JsonFileFormat.Auto;
    /// <summary>Gets the invalid-record behavior.</summary>
    public InvalidJsonRecordBehavior InvalidRecordBehavior { get; init; } = InvalidJsonRecordBehavior.Throw;
    /// <summary>Gets the maximum JSON nesting depth.</summary>
    public int MaxDepth { get; init; } = 64;
    /// <summary>Gets the maximum encoded size of one framed logical record.</summary>
    public int MaxRecordSizeBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum encoded size of one complete unframed JSON input
    /// (a root array, or an auto-detected legacy top-level value sequence).</summary>
    public long MaxUnframedInputSizeBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>Options for <see cref="Sinks.JsonFileSink{T}"/>.</summary>
public sealed record JsonFileSinkOptions
{
    /// <summary>Gets the output layout.</summary>
    public JsonFileFormat Format { get; init; } = JsonFileFormat.BatchJsonLines;
    /// <summary>Gets how the output file is opened.</summary>
    public JsonFileOpenMode OpenMode { get; init; } = JsonFileOpenMode.Append;
    /// <summary>Gets the number of items buffered before a flush.</summary>
    public int FlushInterval { get; init; } = 1000;
}

/// <summary>Options for <see cref="Selectors.DeadLetterSource{T}"/>.</summary>
public sealed record DeadLetterSourceOptions
{
    /// <summary>Gets the input layout.</summary>
    public JsonFileFormat Format { get; init; } = JsonFileFormat.Auto;
    /// <summary>Gets the invalid-record behavior.</summary>
    public InvalidJsonRecordBehavior InvalidRecordBehavior { get; init; } = InvalidJsonRecordBehavior.Throw;
    /// <summary>Gets the maximum JSON nesting depth.</summary>
    public int MaxDepth { get; init; } = 64;
    /// <summary>Gets the maximum encoded size of one framed logical record.</summary>
    public int MaxRecordSizeBytes { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum encoded size of one complete unframed JSON input
    /// (a root array, or an auto-detected legacy top-level value sequence).</summary>
    public long MaxUnframedInputSizeBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>Options for <see cref="DeadLetterSink{T}"/>.</summary>
public sealed record DeadLetterSinkOptions
{
    /// <summary>Gets the behavior after retries are exhausted.</summary>
    public DeadLetterWriteFailureMode FailureMode { get; init; } = DeadLetterWriteFailureMode.Throw;
    /// <summary>Gets whether every successful record is flushed immediately.</summary>
    public bool FlushEachWrite { get; init; } = true;
    /// <summary>Gets delays applied before subsequent write attempts.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)];
    /// <summary>Gets the time provider used for retry delays.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
