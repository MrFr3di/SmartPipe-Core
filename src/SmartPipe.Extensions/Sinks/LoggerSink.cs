using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>
/// Sink that logs processing results using ILogger.
/// Works with any logging provider (Serilog, NLog, Azure Monitor).
/// </summary>
/// <typeparam name="T">Data type.</typeparam>
public class LoggerSink<T> : IPipelineSink<T>
{
    private readonly ILogger<LoggerSink<T>> _logger;

    /// <summary>Create logger sink with given ILogger.</summary>
    /// <param name="logger">Logger instance.</param>
    public LoggerSink(ILogger<LoggerSink<T>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Processed item [TraceId: {TraceId}] successfully. Value: {@Value}",
            envelope.TraceId,
            envelope.Payload
        );

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
