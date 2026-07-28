#nullable enable

namespace SmartPipe.Core;

/// <summary>Identifies a pipeline definition.</summary>
/// <remarks>Key values are preserved exactly and compared using ordinal, case-sensitive equality.</remarks>
public readonly record struct PipelineKey
{
    private readonly string? _value;

    /// <summary>Creates a pipeline key.</summary>
    /// <param name="value">The exact, non-empty key value.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is empty or whitespace.</exception>
    public PipelineKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>Gets the exact key value.</summary>
    /// <exception cref="InvalidOperationException">Thrown for <see langword="default"/>.</exception>
    public string Value =>
        _value ?? throw new InvalidOperationException(
            "The pipeline key is uninitialized. default(PipelineKey) is not valid.");

    /// <summary>Gets whether this value is the uninitialized sentinel.</summary>
    public bool IsEmpty => _value is null;

    /// <inheritdoc />
    public override string ToString() => _value ?? string.Empty;
}

internal static class PipelineKeyGuard
{
    public static void ThrowIfInvalid(PipelineKey key, string? paramName = null)
    {
        if (key.IsEmpty)
            throw new ArgumentException("Pipeline key must be initialized.", paramName ?? "pipelineKey");
    }
}
