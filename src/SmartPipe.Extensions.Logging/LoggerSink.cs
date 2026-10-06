using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Sink that logs processing results using <see cref="ILogger{TCategoryName}"/>.</summary>
/// <typeparam name="T">Data type.</typeparam>
public partial class LoggerSink<T> : IPipelineSink<T>
{
    private const int MaximumAllowedFormattedPayloadLength = 64 * 1024;

    private readonly ILogger<LoggerSink<T>> _logger;
    private readonly LoggerSinkOptions<T>? _options;

    /// <summary>Creates the legacy raw-payload logger sink.</summary>
    /// <param name="logger">Logger instance.</param>
    /// <remarks>This constructor preserves the shipped raw-payload compatibility behavior.</remarks>
    public LoggerSink(ILogger<LoggerSink<T>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Creates a logger sink with an explicit payload exposure policy.</summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="options">Safe payload exposure options.</param>
    public LoggerSink(ILogger<LoggerSink<T>> logger, LoggerSinkOptions<T> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = ValidateOptions(options);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (_options is null)
        {
            _logger.LogInformation(
                "Processed item [TraceId: {TraceId}] successfully. Value: {@Value}",
                envelope.TraceId,
                envelope.Payload);

            return ValueTask.CompletedTask;
        }

        if (_options.PayloadMode is LoggerSinkPayloadMode.UnsafeRaw)
        {
            _logger.LogInformation(
                "Processed item [TraceId: {TraceId}] successfully. Value: {@Value}",
                envelope.TraceId,
                envelope.Payload);

            return ValueTask.CompletedTask;
        }

        if (!_logger.IsEnabled(LogLevel.Information))
            return ValueTask.CompletedTask;

        if (_options.PayloadMode is LoggerSinkPayloadMode.Formatted)
        {
            var formattedPayload = _options.Formatter!(envelope.Payload);
            formattedPayload = formattedPayload is null || formattedPayload.Length <= _options.MaximumFormattedPayloadLength
                ? formattedPayload
                : formattedPayload[.._options.MaximumFormattedPayloadLength];

            if (_options.IncludeTraceId)
                LogFormatted(_logger, envelope.TraceId, formattedPayload);
            else
                LogFormattedWithoutTrace(_logger, formattedPayload);
        }
        else if (_options.IncludeTraceId)
        {
            LogProcessed(_logger, envelope.TraceId);
        }
        else
        {
            LogProcessedWithoutTrace(_logger);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static LoggerSinkOptions<T> ValidateOptions(LoggerSinkOptions<T>? options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Enum.IsDefined(options.PayloadMode))
            throw new ArgumentOutOfRangeException(nameof(options), "PayloadMode is not defined.");

        if (options.MaximumFormattedPayloadLength is <= 0 or > MaximumAllowedFormattedPayloadLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"MaximumFormattedPayloadLength must be between 1 and {MaximumAllowedFormattedPayloadLength}.");
        }

        if (options.PayloadMode is LoggerSinkPayloadMode.Formatted && options.Formatter is null)
            throw new ArgumentException("A formatter is required for formatted payload mode.", nameof(options));

        if (options.PayloadMode is not LoggerSinkPayloadMode.Formatted && options.Formatter is not null)
            throw new ArgumentException("Formatter is only valid for formatted payload mode.", nameof(options));

        return options;
    }

    [LoggerMessage(1000, LogLevel.Information, "Processed item [TraceId: {TraceId}] successfully.", EventName = "SmartPipeItem")]
    private static partial void LogProcessed(ILogger logger, ulong traceId);

    [LoggerMessage(1000, LogLevel.Information, "Processed item successfully.", EventName = "SmartPipeItemWithoutTrace")]
    private static partial void LogProcessedWithoutTrace(ILogger logger);

    [LoggerMessage(1000, LogLevel.Information, "Processed item [TraceId: {TraceId}] successfully. FormattedPayload: {FormattedPayload}", EventName = "SmartPipeItemFormatted")]
    private static partial void LogFormatted(ILogger logger, ulong traceId, string? formattedPayload);

    [LoggerMessage(1000, LogLevel.Information, "Processed item successfully. FormattedPayload: {FormattedPayload}", EventName = "SmartPipeItemFormattedWithoutTrace")]
    private static partial void LogFormattedWithoutTrace(ILogger logger, string? formattedPayload);
}
