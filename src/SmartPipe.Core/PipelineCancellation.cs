using System;
using System.Threading;
using System.Threading.Tasks;

namespace SmartPipe.Core;

/// <summary>Timeout utilities for pipeline operations.</summary>
public static class PipelineCancellation
{
    /// <summary>Create a CancellationTokenSource with timeout.</summary>
    /// <param name="timeout">Timeout duration.</param>
    /// <returns>CTS configured with the timeout.</returns>
    public static CancellationTokenSource CreateTimeout(TimeSpan timeout) => new(timeout);

    /// <summary>Execute an operation with timeout. Returns Failure on timeout.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="task">Async operation to wrap.</param>
    /// <param name="timeout">Timeout duration.</param>
    /// <param name="traceId">Trace ID for error reporting.</param>
    /// <returns>Processing result, or Failure if timeout occurred.</returns>
    public static async ValueTask<ProcessingResult<T>> WithTimeoutAsync<T>(
        this ValueTask<ProcessingResult<T>> task, TimeSpan timeout, ulong traceId)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await task.AsTask().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return ProcessingResult<T>.Failure(
                new SmartPipeError($"Timed out after {timeout.TotalSeconds:F1}s", ErrorType.Transient, "Timeout"),
                traceId);
        }
    }
}
