#nullable enable

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Validates and freezes Entity Framework Core options at composition time.</summary>
/// <remarks>
/// Validation happens before any context is created, so an invalid option can never reach a provider. The
/// snapshot is immutable and per-descriptor, so a caller that mutates its option record afterwards cannot
/// change the behaviour of an already composed descriptor.
/// </remarks>
internal sealed class EfCoreOptionsSnapshot
{
    private EfCoreOptionsSnapshot(string operationName, EfCoreQueryTrackingMode? trackingMode)
    {
        OperationName = operationName;
        TrackingMode = trackingMode;
    }

    /// <summary>Gets the validated operation name.</summary>
    internal string OperationName { get; }

    /// <summary>Gets the validated tracking mode, or <see langword="null"/> for the compiled path.</summary>
    internal EfCoreQueryTrackingMode? TrackingMode { get; }

    /// <summary>Validates and freezes queryable-path options.</summary>
    internal static EfCoreOptionsSnapshot Create(EfCoreQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.TrackingMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TrackingMode,
                "The Entity Framework Core query tracking mode is invalid.");
        }

        return new(ValidateOperationName(options.OperationName, nameof(options)), options.TrackingMode);
    }

    /// <summary>Validates and freezes compiled-path options.</summary>
    internal static EfCoreOptionsSnapshot Create(EfCoreCompiledQueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(ValidateOperationName(options.OperationName, nameof(options)), trackingMode: null);
    }

    private static string ValidateOperationName(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The operation name must not be empty or whitespace.", parameterName);
        if (value.Length > 64)
            throw new ArgumentException("The operation name must be at most 64 characters long.", parameterName);

        foreach (var character in value)
        {
            if (char.IsControl(character))
                throw new ArgumentException("The operation name must not contain control characters.", parameterName);
        }

        return value;
    }
}
