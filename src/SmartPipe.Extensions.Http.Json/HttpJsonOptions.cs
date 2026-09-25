#nullable enable

namespace SmartPipe.Extensions.Http.Json;

/// <summary>Controls how a JSON null element is handled in an HTTP JSON response.</summary>
public enum HttpJsonNullItemPolicy
{
    /// <summary>Stop reading and throw a JSON exception.</summary>
    Throw = 0,
    /// <summary>Skip the null element and continue.</summary>
    Skip = 1,
}

/// <summary>Controls how an oversized HTTP NDJSON record is handled.</summary>
public enum HttpJsonOversizeRecordPolicy
{
    /// <summary>Stop reading and throw a JSON exception.</summary>
    Throw = 0,
    /// <summary>Discard the complete oversized record and continue at the next line.</summary>
    Skip = 1,
}

/// <summary>Options for reading a root JSON array from an HTTP response.</summary>
public sealed record HttpJsonArrayOptions
{
    /// <summary>Gets the maximum JSON nesting depth.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Gets the maximum number of unframed response-body bytes to read.</summary>
    public long MaxUnframedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Gets the policy for a null array element.</summary>
    public HttpJsonNullItemPolicy NullItemPolicy { get; init; } = HttpJsonNullItemPolicy.Throw;
}

/// <summary>Options for reading newline-delimited JSON from an HTTP response.</summary>
public sealed record HttpNdjsonOptions
{
    /// <summary>Gets the maximum JSON nesting depth.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Gets the maximum encoded size of one line record.</summary>
    public int MaxRecordSizeBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Gets the maximum number of unframed response-body bytes to read.</summary>
    public long MaxUnframedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Gets the policy for a JSON null record.</summary>
    public HttpJsonNullItemPolicy NullItemPolicy { get; init; } = HttpJsonNullItemPolicy.Throw;

    /// <summary>Gets the policy for a record larger than <see cref="MaxRecordSizeBytes"/>.</summary>
    public HttpJsonOversizeRecordPolicy OversizeRecordPolicy { get; init; } = HttpJsonOversizeRecordPolicy.Throw;
}

internal sealed record HttpJsonArrayOptionsSnapshot(
    int MaxDepth,
    long MaxUnframedBytes,
    HttpJsonNullItemPolicy NullItemPolicy);

internal sealed record HttpNdjsonOptionsSnapshot(
    int MaxDepth,
    int MaxRecordSizeBytes,
    long MaxUnframedBytes,
    HttpJsonNullItemPolicy NullItemPolicy,
    HttpJsonOversizeRecordPolicy OversizeRecordPolicy);

internal static class HttpJsonOptionsSnapshotFactory
{
    public static HttpJsonArrayOptionsSnapshot Create(HttpJsonArrayOptions? options)
    {
        options ??= new HttpJsonArrayOptions();
        if (options.MaxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be greater than zero.");
        if (options.MaxUnframedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxUnframedBytes must be greater than zero.");
        if (!Enum.IsDefined(options.NullItemPolicy))
            throw new ArgumentOutOfRangeException(nameof(options), options.NullItemPolicy, "The null-item policy is not defined.");

        return new(options.MaxDepth, options.MaxUnframedBytes, options.NullItemPolicy);
    }

    public static HttpNdjsonOptionsSnapshot Create(HttpNdjsonOptions? options)
    {
        options ??= new HttpNdjsonOptions();
        if (options.MaxDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be greater than zero.");
        if (options.MaxRecordSizeBytes <= 0 || options.MaxRecordSizeBytes > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxRecordSizeBytes, "MaxRecordSizeBytes must be positive and fit in a managed byte array.");
        if (options.MaxUnframedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxUnframedBytes must be greater than zero.");
        if (!Enum.IsDefined(options.NullItemPolicy))
            throw new ArgumentOutOfRangeException(nameof(options), options.NullItemPolicy, "The null-item policy is not defined.");
        if (!Enum.IsDefined(options.OversizeRecordPolicy))
            throw new ArgumentOutOfRangeException(nameof(options), options.OversizeRecordPolicy, "The oversized-record policy is not defined.");

        return new(
            options.MaxDepth,
            options.MaxRecordSizeBytes,
            options.MaxUnframedBytes,
            options.NullItemPolicy,
            options.OversizeRecordPolicy);
    }
}
