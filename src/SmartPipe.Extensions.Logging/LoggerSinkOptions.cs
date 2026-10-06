using System;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Controls the payload exposure of the safe <see cref="LoggerSink{T}"/> constructor.</summary>
public sealed record LoggerSinkOptions<T>
{
    /// <summary>Gets the payload logging mode.</summary>
    public LoggerSinkPayloadMode PayloadMode { get; init; } = LoggerSinkPayloadMode.None;

    /// <summary>Gets whether the safe event includes the envelope trace identifier.</summary>
    public bool IncludeTraceId { get; init; } = true;

    /// <summary>Gets the formatter used when <see cref="PayloadMode"/> is formatted.</summary>
    public Func<T, string?>? Formatter { get; init; }

    /// <summary>Gets the maximum number of characters emitted by <see cref="Formatter"/>.</summary>
    public int MaximumFormattedPayloadLength { get; init; } = 1024;
}

/// <summary>Payload exposure modes for the safe logger sink constructor.</summary>
public enum LoggerSinkPayloadMode
{
    /// <summary>Do not log the payload.</summary>
    None = 0,

    /// <summary>Log only the bounded string returned by the configured formatter.</summary>
    Formatted = 1,

    /// <summary>Explicitly opt in to the legacy raw-payload event.</summary>
    UnsafeRaw = 2,
}
