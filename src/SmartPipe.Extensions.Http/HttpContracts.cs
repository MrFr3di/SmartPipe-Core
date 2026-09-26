#nullable enable

using System.Net;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http;

/// <summary>Creates a new HTTP request for one sink write.</summary>
/// <typeparam name="T">The payload type being written.</typeparam>
/// <param name="envelope">The envelope being written.</param>
/// <param name="cancellationToken">Cancellation token for request creation.</param>
/// <returns>A new request whose ownership transfers to the HTTP sink.</returns>
public delegate ValueTask<HttpRequestMessage> HttpRequestFactory<T>(
    ProcessingEnvelope<T> envelope,
    CancellationToken cancellationToken);

/// <summary>Reads materialized values from a successful HTTP response.</summary>
/// <typeparam name="T">The payload type read from the response.</typeparam>
/// <param name="response">The borrowed response for the duration of enumeration.</param>
/// <param name="cancellationToken">Cancellation token for response-body reading.</param>
/// <returns>An asynchronous sequence of materialized values.</returns>
public delegate IAsyncEnumerable<T> HttpResponseReader<out T>(
    HttpResponseMessage response,
    CancellationToken cancellationToken);

/// <summary>Controls HTTP non-success response details.</summary>
public sealed record HttpResponsePolicy
{
    /// <summary>Gets the maximum number of response-body bytes retained in an opted-in error preview.</summary>
    public int MaxErrorBodyBytes { get; init; } = 16 * 1024;

    /// <summary>Gets whether non-success exceptions may include a bounded response-body preview.</summary>
    public bool IncludeErrorBodyPreview { get; init; }
}

/// <summary>Configures an HTTP source.</summary>
public sealed record HttpSourceOptions
{
    /// <summary>Gets the operation label used in HTTP status exceptions.</summary>
    public string OperationName { get; init; } = "http-source";

    /// <summary>Gets an optional timeout applied after response headers arrive.</summary>
    public TimeSpan? BodyTimeout { get; init; }

    /// <summary>Gets the policy for non-success responses.</summary>
    public HttpResponsePolicy ResponsePolicy { get; init; } = new();
}

/// <summary>Configures an HTTP sink.</summary>
public sealed record HttpSinkOptions
{
    /// <summary>Gets the operation label used in HTTP status exceptions.</summary>
    public string OperationName { get; init; } = "http-sink";

    /// <summary>Gets an optional timeout applied after response headers arrive.</summary>
    public TimeSpan? BodyTimeout { get; init; }

    /// <summary>Gets the idempotency header name used when a selector returns a key.</summary>
    public string IdempotencyHeaderName { get; init; } = "Idempotency-Key";

    /// <summary>Gets the policy for non-success responses.</summary>
    public HttpResponsePolicy ResponsePolicy { get; init; } = new();
}

/// <summary>An HTTP response did not have a successful status code.</summary>
public sealed class HttpResponseStatusException : HttpRequestException
{
    internal HttpResponseStatusException(
        string operationName,
        HttpStatusCode statusCode,
        string? bodyPreview,
        bool bodyPreviewTruncated)
        : base($"{operationName} returned HTTP status {(int)statusCode} ({statusCode}).", null, statusCode)
    {
        ErrorBodyPreview = bodyPreview;
        ErrorBodyPreviewTruncated = bodyPreviewTruncated;
    }

    /// <summary>Gets an opted-in, bounded UTF-8 preview of the response body.</summary>
    public string? ErrorBodyPreview { get; }

    /// <summary>Gets whether the preview was truncated after the configured byte limit.</summary>
    public bool ErrorBodyPreviewTruncated { get; }
}

internal sealed record HttpResponsePolicySnapshot(int MaxErrorBodyBytes, bool IncludeErrorBodyPreview);

internal sealed record HttpSourceOptionsSnapshot(
    string OperationName,
    TimeSpan? BodyTimeout,
    HttpResponsePolicySnapshot ResponsePolicy);

internal sealed record HttpSinkOptionsSnapshot(
    string OperationName,
    TimeSpan? BodyTimeout,
    string IdempotencyHeaderName,
    HttpResponsePolicySnapshot ResponsePolicy);

internal static class HttpOptionsSnapshot
{
    public static HttpSourceOptionsSnapshot Create(HttpSourceOptions? options)
    {
        options ??= new HttpSourceOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationName);
        var timeout = ValidateBodyTimeout(options.BodyTimeout, nameof(options.BodyTimeout));
        return new(options.OperationName, timeout, SnapshotResponsePolicy(options.ResponsePolicy));
    }

    public static HttpSinkOptionsSnapshot Create(HttpSinkOptions? options)
    {
        options ??= new HttpSinkOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OperationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.IdempotencyHeaderName);
        if (!HttpRequestValidation.IsRequestHeaderName(options.IdempotencyHeaderName))
            throw new ArgumentException("The idempotency header name must be a valid HTTP token that can be sent as a request header.", nameof(options));

        var timeout = ValidateBodyTimeout(options.BodyTimeout, nameof(options.BodyTimeout));
        return new(
            options.OperationName,
            timeout,
            options.IdempotencyHeaderName,
            SnapshotResponsePolicy(options.ResponsePolicy));
    }

    private static HttpResponsePolicySnapshot SnapshotResponsePolicy(HttpResponsePolicy? policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxErrorBodyBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(policy), "MaxErrorBodyBytes must be positive.");

        return new(policy.MaxErrorBodyBytes, policy.IncludeErrorBodyPreview);
    }

    private static TimeSpan? ValidateBodyTimeout(TimeSpan? timeout, string parameterName)
    {
        if (timeout is null)
            return null;

        if (timeout.Value <= TimeSpan.Zero || timeout.Value.TotalMilliseconds > uint.MaxValue - 1d)
            throw new ArgumentOutOfRangeException(parameterName, "BodyTimeout must be positive and supported by the timer API.");

        return timeout.Value;
    }
}
