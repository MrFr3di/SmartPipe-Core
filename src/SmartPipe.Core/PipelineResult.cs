#nullable enable

namespace SmartPipe.Core;

/// <summary>Typed pipeline output result with Partial Success support.</summary>
/// <typeparam name="T">Type of result value.</typeparam>
public readonly record struct PipelineResult<T>
{
    private PipelineResult(bool success, T? value, SmartPipeError? error, ulong traceId) =>
        (IsSuccess, Value, Error, TraceId) = (success, value, error, traceId);

    /// <summary>Whether the pipeline item completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>Result value when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Structured error when <see cref="IsSuccess"/> is false.</summary>
    public SmartPipeError? Error { get; }

    /// <summary>Trace identifier associated with the item.</summary>
    public ulong TraceId { get; }

    /// <summary>Create a successful result.</summary>
    public static PipelineResult<T> Success(T value, ulong traceId) =>
        new(true, value, null, traceId);

    /// <summary>Create a failed result.</summary>
    public static PipelineResult<T> Failure(SmartPipeError error, ulong traceId) =>
        new(false, default, error, traceId);

    /// <summary>Implicit conversion to bool for clean syntax: if (result).</summary>
    public static implicit operator bool(PipelineResult<T> result) => result.IsSuccess;
}
