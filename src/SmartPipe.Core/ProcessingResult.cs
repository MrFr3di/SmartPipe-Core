#nullable enable

namespace SmartPipe.Core;

/// <summary>Pipeline step result with Partial Success support. Errors become data, not exceptions.</summary>
/// <typeparam name="T">Type of result value.</typeparam>
public readonly record struct ProcessingResult<T>
{
    /// <summary>Whether the step completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>Result value (valid only if IsSuccess is true).</summary>
    public T? Value { get; }

    /// <summary>Structured error (valid only if IsSuccess is false).</summary>
    public SmartPipeError? Error { get; }

    /// <summary>Trace ID from the original context.</summary>
    public ulong TraceId { get; }

    private ProcessingResult(bool success, T? value, SmartPipeError? error, ulong traceId)
        => (IsSuccess, Value, Error, TraceId) = (success, value, error, traceId);

    /// <summary>Create a successful result.</summary>
    /// <param name="value">Result value.</param>
    /// <param name="traceId">Trace ID from the original context.</param>
    public static ProcessingResult<T> Success(T value, ulong traceId) => new(true, value, null, traceId);

    /// <summary>Create a failed result.</summary>
    /// <param name="error">Structured error describing the failure.</param>
    /// <param name="traceId">Trace ID from the original context.</param>
    public static ProcessingResult<T> Failure(SmartPipeError error, ulong traceId) => new(false, default, error, traceId);

    /// <summary>Implicit conversion to bool for clean syntax: if (result).</summary>
    /// <param name="r">Processing result.</param>
    public static implicit operator bool(ProcessingResult<T> r) => r.IsSuccess;
}
