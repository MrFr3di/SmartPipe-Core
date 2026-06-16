#nullable enable

namespace SmartPipe.Core;

/// <summary>Classifies a terminal item result emitted by a typed pipeline run.</summary>
public enum PipelineResultKind
{
    /// <summary>The item completed successfully.</summary>
    Success,

    /// <summary>The item failed according to stage failure policy.</summary>
    Failure,

    /// <summary>The item was filtered by a stage as normal control flow.</summary>
    Filtered,

    /// <summary>The item was skipped as normal control flow.</summary>
    Skipped,
}

/// <summary>Typed pipeline output result with Partial Success support.</summary>
/// <typeparam name="T">Type of result value.</typeparam>
public readonly record struct PipelineResult<T>
{
    private PipelineResult(
        PipelineResultKind kind,
        bool success,
        T? value,
        SmartPipeError? error,
        ulong traceId) =>
        (Kind, IsSuccess, Value, Error, TraceId) = (kind, success, value, error, traceId);

    /// <summary>Terminal result classification.</summary>
    public PipelineResultKind Kind { get; }

    /// <summary>Whether the pipeline item completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>Whether the pipeline item represents a failure terminal state.</summary>
    public bool IsFailure => Kind == PipelineResultKind.Failure;

    /// <summary>Result value when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Structured error when <see cref="IsSuccess"/> is false.</summary>
    public SmartPipeError? Error { get; }

    /// <summary>Trace identifier associated with the item.</summary>
    public ulong TraceId { get; }

    /// <summary>Create a successful result.</summary>
    public static PipelineResult<T> Success(T value, ulong traceId) =>
        new(PipelineResultKind.Success, true, value, null, traceId);

    /// <summary>Create a failed result.</summary>
    public static PipelineResult<T> Failure(SmartPipeError error, ulong traceId) =>
        new(PipelineResultKind.Failure, false, default, error, traceId);

    /// <summary>Create a filtered terminal result.</summary>
    public static PipelineResult<T> Filtered(ulong traceId) =>
        new(PipelineResultKind.Filtered, false, default, null, traceId);

    /// <summary>Create a skipped terminal result.</summary>
    public static PipelineResult<T> Skipped(ulong traceId) =>
        new(PipelineResultKind.Skipped, false, default, null, traceId);

    /// <summary>Implicit conversion to bool for clean syntax: if (result).</summary>
    public static implicit operator bool(PipelineResult<T> result) => result.IsSuccess;
}
