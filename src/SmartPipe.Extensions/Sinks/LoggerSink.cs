using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>
/// Sink that logs processing results using ILogger.
/// Works with any logging provider (Serilog, NLog, Azure Monitor).
/// </summary>
/// <typeparam name="T">Data type.</typeparam>
public class LoggerSink<T> : ISink<T>
{
    private readonly ILogger<LoggerSink<T>> _logger;

    /// <summary>Create logger sink with given ILogger.</summary>
    /// <param name="logger">Logger instance.</param>
    public LoggerSink(ILogger<LoggerSink<T>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Processed item [TraceId: {TraceId}] successfully. Value: {@Value}",
                result.TraceId, result.Value);
        }
        else
        {
            _logger.LogError(
                "Failed item [TraceId: {TraceId}]: {ErrorMessage}",
                result.TraceId, result.Error?.Message);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
