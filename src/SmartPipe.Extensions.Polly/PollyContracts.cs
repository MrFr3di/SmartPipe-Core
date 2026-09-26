#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Extensions.Polly;

/// <summary>Declares whether a Polly decorator owns the inner transform it wraps.</summary>
public enum PollyInnerTransformOwnership
{
    /// <summary>The decorator initializes the inner transform but never disposes it.</summary>
    Borrowed = 0,

    /// <summary>The decorator initializes the inner transform and disposes it exactly once.</summary>
    Owned = 1,
}

/// <summary>Maps the final non-cancellation exception of a Polly execution to a stage failure.</summary>
/// <param name="exception">The final exception after every configured Polly strategy has run.</param>
/// <returns>
/// A <see cref="SmartPipeError"/> that becomes <see cref="StageResult{T}.Failure(SmartPipeError)"/>,
/// or <see langword="null"/> to rethrow <paramref name="exception"/> unchanged.
/// </returns>
public delegate SmartPipeError? PollyTransformExceptionMapper(Exception exception);

/// <summary>Configures a <see cref="PollyTransformDecorator{TInput, TOutput}"/>.</summary>
/// <remarks>The decorator and component factories snapshot these values when they are composed.</remarks>
public sealed record PollyTransformDecoratorOptions
{
    /// <summary>Gets the low-cardinality operation name passed to the Polly context.</summary>
    /// <remarks>
    /// Null, or 1 to 128 characters with no whitespace or control characters. Choose a stable
    /// operation name; never use payload values, run or trace identifiers, or URIs.
    /// </remarks>
    public string? OperationKey { get; init; }

    /// <summary>Gets the optional mapper for the final non-cancellation exception.</summary>
    /// <remarks>
    /// The mapper never sees a <see cref="StageResult{T}"/> failure, an
    /// <see cref="OperationCanceledException"/>, or its own failure. By default no mapper runs and the
    /// original exception is rethrown with its identity and stack trace.
    /// </remarks>
    public PollyTransformExceptionMapper? ExceptionMapper { get; init; }
}

internal sealed class PollyTransformDecoratorSettings
{
    internal const int MaxOperationKeyLength = 128;

    private PollyTransformDecoratorSettings(string? operationKey, PollyTransformExceptionMapper? exceptionMapper)
    {
        OperationKey = operationKey;
        ExceptionMapper = exceptionMapper;
    }

    public static PollyTransformDecoratorSettings Default { get; } = new(null, null);

    public string? OperationKey { get; }

    public PollyTransformExceptionMapper? ExceptionMapper { get; }

    public static PollyTransformDecoratorSettings Create(PollyTransformDecoratorOptions? options)
    {
        if (options is null)
            return Default;

        ValidateOperationKey(options.OperationKey, nameof(options));
        return new(options.OperationKey, options.ExceptionMapper);
    }

    public static void ThrowIfUndefined(PollyInnerTransformOwnership innerOwnership, string parameterName)
    {
        if (!Enum.IsDefined(innerOwnership))
            throw new ArgumentOutOfRangeException(parameterName, innerOwnership, "Inner transform ownership is invalid.");
    }

    private static void ValidateOperationKey(string? operationKey, string parameterName)
    {
        if (operationKey is null)
            return;

        if (operationKey.Length is 0 or > MaxOperationKeyLength)
        {
            throw new ArgumentException(
                $"OperationKey must contain 1 to {MaxOperationKeyLength} characters.",
                parameterName);
        }

        foreach (var character in operationKey)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                throw new ArgumentException(
                    "OperationKey must not contain whitespace or control characters.",
                    parameterName);
            }
        }
    }
}
