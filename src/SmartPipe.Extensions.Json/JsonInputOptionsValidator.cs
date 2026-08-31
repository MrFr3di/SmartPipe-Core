using Microsoft.Extensions.Logging;

namespace SmartPipe.Extensions;

internal static class JsonInputOptionsValidator
{
    public static JsonFileSourceOptions Validate(JsonFileSourceOptions? options, ILogger? logger)
        => Validate(options, logger is not null);

    internal static JsonFileSourceOptions Validate(JsonFileSourceOptions? options, bool loggerAvailable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options.Format, options.InvalidRecordBehavior, options.MaxDepth,
            options.MaxRecordSizeBytes, options.MaxUnframedInputSizeBytes, loggerAvailable, nameof(options));
        if (options.InvalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog
            && options.Format is not (JsonFileFormat.Ndjson or JsonFileFormat.BatchJsonLines))
            throw new ArgumentException(
                "SkipAndLog requires an explicit Ndjson or BatchJsonLines format with independently framed JSON records.",
                nameof(options));
        return options with { };
    }

    public static DeadLetterSourceOptions Validate(DeadLetterSourceOptions? options, ILogger? logger)
        => Validate(options, logger is not null);

    internal static DeadLetterSourceOptions Validate(DeadLetterSourceOptions? options, bool loggerAvailable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options.Format, options.InvalidRecordBehavior, options.MaxDepth,
            options.MaxRecordSizeBytes, options.MaxUnframedInputSizeBytes, loggerAvailable, nameof(options));
        if (options.Format == JsonFileFormat.BatchJsonLines)
            throw new ArgumentException("BatchJsonLines is not supported by DeadLetterSource.", nameof(options));
        if (options.Format == JsonFileFormat.Array
            && options.InvalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog)
            throw new ArgumentException(
                "SkipAndLog is supported only for independently framed JSON records.",
                nameof(options));
        return options with { };
    }

    internal static JsonFileSinkOptions Validate(JsonFileSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Format))
            throw new ArgumentOutOfRangeException(nameof(options), options.Format, "The JSON format is not defined.");
        if (!Enum.IsDefined(options.OpenMode))
            throw new ArgumentOutOfRangeException(nameof(options), options.OpenMode, "The JSON open mode is not defined.");
        if (options.Format == JsonFileFormat.Auto)
            throw new ArgumentException("Auto format is valid only for JSON sources.", nameof(options));
        if (options.Format == JsonFileFormat.Array && options.OpenMode == JsonFileOpenMode.Append)
            throw new ArgumentException("A root JSON array cannot be appended safely.", nameof(options));
        if (options.FlushInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.FlushInterval, "Flush interval must be greater than zero.");
        return options with { };
    }

    internal static DeadLetterSinkOptions Validate(DeadLetterSinkOptions? options, bool loggerAvailable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RetryDelays);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (!Enum.IsDefined(options.FailureMode))
            throw new ArgumentOutOfRangeException(nameof(options), options.FailureMode, "The dead-letter failure mode is not defined.");
        if (options.RetryDelays.Any(static delay => delay < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delays cannot be negative.");
        if (options.FailureMode == Sinks.DeadLetterWriteFailureMode.LogAndDrop && !loggerAvailable)
            throw new ArgumentException("LogAndDrop requires a logger factory.", nameof(options));
        return options with { RetryDelays = options.RetryDelays.ToArray() };
    }

    private static void ValidateCommon(
        JsonFileFormat format,
        InvalidJsonRecordBehavior invalidRecordBehavior,
        int maxDepth,
        int maxRecordSizeBytes,
        long maxUnframedInputSizeBytes,
        bool loggerAvailable,
        string parameterName)
    {
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(parameterName, format, "The JSON format is not defined.");
        if (!Enum.IsDefined(invalidRecordBehavior))
            throw new ArgumentOutOfRangeException(parameterName, invalidRecordBehavior, "The invalid-record behavior is not defined.");
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(parameterName, maxDepth, "MaxDepth must be greater than zero.");
        if (maxRecordSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(parameterName, maxRecordSizeBytes, "MaxRecordSizeBytes must be greater than zero.");
        if (maxUnframedInputSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(parameterName, maxUnframedInputSizeBytes, "MaxUnframedInputSizeBytes must be greater than zero.");
        if (invalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog && !loggerAvailable)
            throw new ArgumentException("SkipAndLog requires a logger.", parameterName);
    }
}
