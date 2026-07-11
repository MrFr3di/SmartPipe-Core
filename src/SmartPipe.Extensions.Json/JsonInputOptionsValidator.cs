using Microsoft.Extensions.Logging;

namespace SmartPipe.Extensions;

internal static class JsonInputOptionsValidator
{
    public static JsonFileSourceOptions Validate(JsonFileSourceOptions? options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options.Format, options.InvalidRecordBehavior, options.MaxDepth,
            options.MaxRecordSizeBytes, options.MaxDocumentSizeBytes, logger, nameof(options));
        return options with { };
    }

    public static DeadLetterSourceOptions Validate(DeadLetterSourceOptions? options, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommon(options.Format, options.InvalidRecordBehavior, options.MaxDepth,
            options.MaxRecordSizeBytes, options.MaxDocumentSizeBytes, logger, nameof(options));
        if (options.Format == JsonFileFormat.BatchJsonLines)
            throw new ArgumentException("BatchJsonLines is not supported by DeadLetterSource.", nameof(options));
        return options with { };
    }

    private static void ValidateCommon(
        JsonFileFormat format,
        InvalidJsonRecordBehavior invalidRecordBehavior,
        int maxDepth,
        int maxRecordSizeBytes,
        long maxDocumentSizeBytes,
        ILogger? logger,
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
        if (maxDocumentSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(parameterName, maxDocumentSizeBytes, "MaxDocumentSizeBytes must be greater than zero.");
        if (invalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog && logger == null)
            throw new ArgumentException("SkipAndLog requires a logger.", parameterName);
    }
}
