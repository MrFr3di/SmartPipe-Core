#nullable enable

namespace SmartPipe.Core;

/// <summary>Classifies the result produced by an envelope-aware pipeline stage.</summary>
public enum StageResultKind
{
    /// <summary>The stage completed successfully.</summary>
    Success,

    /// <summary>The stage failed.</summary>
    Failure,

    /// <summary>The stage filtered the item out.</summary>
    Filtered,

    /// <summary>The stage was skipped by policy.</summary>
    Skipped,

    /// <summary>The stage was cancelled.</summary>
    Cancelled,

    /// <summary>The stage timed out.</summary>
    TimedOut,
}

/// <summary>Envelope-aware result returned by modern pipeline transformers.</summary>
/// <typeparam name="T">Result payload type.</typeparam>
/// <remarks>
/// Use the static factory methods to create valid stage results. The default value of
/// <see cref="StageResult{T}"/> is invalid and must not be emitted by user code.
/// </remarks>
public readonly record struct StageResult<T>
{
    private StageResult(StageResultKind kind, T? value, SmartPipeError? error)
    {
        Kind = kind;
        Value = value;
        Error = error;
        IsValid = true;
    }

    /// <summary>Gets a value indicating whether the result represents a successful stage.</summary>
    public bool IsSuccess => Kind == StageResultKind.Success;

    /// <summary>Gets the stage output value when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Gets the structured error when the stage did not succeed.</summary>
    public SmartPipeError? Error { get; }

    /// <summary>Gets the stage result kind.</summary>
    public StageResultKind Kind { get; }

    /// <summary>Gets a value indicating whether the result was created through a factory method.</summary>
    public bool IsValid { get; }

    /// <summary>Creates a successful stage result.</summary>
    /// <param name="value">Stage output value.</param>
    /// <returns>A valid successful result.</returns>
    public static StageResult<T> Success(T value) => new(StageResultKind.Success, value, null);

    /// <summary>Creates a failed stage result.</summary>
    /// <param name="error">Failure details.</param>
    /// <returns>A valid failed result.</returns>
    public static StageResult<T> Failure(SmartPipeError error) =>
        new(StageResultKind.Failure, default, error);

    /// <summary>Creates a filtered stage result.</summary>
    /// <returns>A valid filtered result.</returns>
    public static StageResult<T> Filtered() =>
        new(
            StageResultKind.Filtered,
            default,
            new SmartPipeError("Filtered out", ErrorType.Permanent, "Filtered")
        );

    /// <summary>Creates a cancelled stage result.</summary>
    /// <returns>A valid cancelled result.</returns>
    public static StageResult<T> Cancelled() =>
        new(
            StageResultKind.Cancelled,
            default,
            new SmartPipeError("Cancelled", ErrorType.Permanent, "Cancelled")
        );

    /// <summary>Creates a timed out stage result.</summary>
    /// <param name="error">Timeout error details.</param>
    /// <returns>A valid timeout result.</returns>
    public static StageResult<T> TimedOut(SmartPipeError error) =>
        new(StageResultKind.TimedOut, default, error);

    /// <summary>Converts a legacy processing result to a stage result.</summary>
    /// <param name="result">Legacy result.</param>
    /// <returns>A stage result with equivalent success or failure information.</returns>
    public static StageResult<T> FromProcessingResult(ProcessingResult<T> result)
    {
        if (result.IsSuccess)
            return Success(result.Value!);

        if (result.Error?.Category == "Filtered")
            return Filtered();

        return Failure(
            result.Error ?? new SmartPipeError("Unknown stage failure", ErrorType.Permanent)
        );
    }

    /// <summary>Converts this stage result to a legacy processing result.</summary>
    /// <param name="traceId">Trace identifier for the legacy result.</param>
    /// <returns>A legacy processing result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the stage result is invalid.</exception>
    public ProcessingResult<T> ToProcessingResult(ulong traceId)
    {
        if (!IsValid)
            throw new InvalidOperationException(
                "default(StageResult<T>) is invalid. Use StageResult factory methods."
            );

        return IsSuccess
            ? ProcessingResult<T>.Success(Value!, traceId)
            : ProcessingResult<T>.Failure(
                Error ?? new SmartPipeError(Kind.ToString(), ErrorType.Permanent, Kind.ToString()),
                traceId
            );
    }
}
