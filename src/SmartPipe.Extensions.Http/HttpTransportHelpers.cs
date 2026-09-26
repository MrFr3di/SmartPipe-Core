#nullable enable

using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;

namespace SmartPipe.Extensions.Http;

internal static class HttpRequestValidation
{
    public static void Validate(HttpRequestMessage request, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        var requestUri = request.RequestUri
            ?? throw new InvalidOperationException("The request factory returned a request without a URI.");
        if (!requestUri.IsAbsoluteUri
            && (client.BaseAddress is null || !Uri.TryCreate(client.BaseAddress, requestUri, out var resolvedUri) || !resolvedUri.IsAbsoluteUri))
        {
            throw new InvalidOperationException("The request URI must be absolute or resolvable against HttpClient.BaseAddress.");
        }

        ValidateHeaders(request.Headers);
        if (request.Content is not null)
            ValidateHeaders(request.Content.Headers);
    }

    public static void AddIdempotencyKey(
        HttpRequestMessage request,
        string headerName,
        string? key)
    {
        if (key is null)
            return;

        ValidateIdempotencyKey(key);
        if (request.Headers.TryGetValues(headerName, out var existingValues))
        {
            var values = existingValues.ToArray();
            if (values.Length != 1 || !string.Equals(values[0], key, StringComparison.Ordinal))
                throw new InvalidOperationException("The request contains a conflicting or multiple idempotency header values.");
            return;
        }

        if (!request.Headers.TryAddWithoutValidation(headerName, key))
            throw new InvalidOperationException("The idempotency header could not be added to the request.");
    }

    /// <summary>Returns whether a token names a header that may be set on request headers.</summary>
    /// <remarks>Content headers such as <c>Content-Type</c> are valid tokens but cannot carry a request idempotency key.</remarks>
    public static bool IsRequestHeaderName(string value)
    {
        if (!IsToken(value))
            return false;

        using var probe = new HttpRequestMessage();
        return probe.Headers.TryAddWithoutValidation(value, "probe");
    }

    public static bool IsToken(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var character in value)
        {
            if (!((character >= 'a' && character <= 'z')
                || (character >= 'A' && character <= 'Z')
                || (character >= '0' && character <= '9')
                || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
                    or '^' or '_' or '`' or '|' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateHeaders(HttpHeaders headers)
    {
        foreach (var header in headers)
        {
            if (!IsToken(header.Key))
                throw new InvalidOperationException("The request contains an invalid HTTP header name.");

            foreach (var value in header.Value)
            {
                foreach (var character in value)
                {
                    if ((character < 0x20 && character != '\t') || character == 0x7f)
                        throw new InvalidOperationException("The request contains an invalid HTTP header value.");
                }
            }
        }
    }

    internal static void ValidateIdempotencyKey(string key)
    {
        if (key.Length is < 1 or > 255)
            throw new InvalidOperationException("An idempotency key must contain between 1 and 255 printable ASCII characters.");

        if (key.AsSpan().ContainsAnyExceptInRange('!', '~'))
            throw new InvalidOperationException("An idempotency key must contain only printable ASCII characters without whitespace.");
    }
}

/// <summary>Supplies the client for one HTTP operation and records whether the adapter owns it.</summary>
internal sealed class HttpClientSource
{
    private readonly HttpClient? _client;
    private readonly IHttpClientFactory? _factory;
    private readonly string? _clientName;

    private HttpClientSource(HttpClient? client, IHttpClientFactory? factory, string? clientName)
    {
        _client = client;
        _factory = factory;
        _clientName = clientName;
    }

    public static HttpClientSource Borrowed(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new(client, null, null);
    }

    public static HttpClientSource FromFactory(IHttpClientFactory factory, string clientName)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        return new(null, factory, clientName);
    }

    /// <summary>Returns a borrowed client, or creates a factory client the caller must dispose.</summary>
    public HttpClient Acquire(out bool owned)
    {
        if (_factory is null)
        {
            owned = false;
            return _client!;
        }

        var client = _factory.CreateClient(_clientName!)
            ?? throw new InvalidOperationException("The HTTP client factory returned null.");
        owned = true;
        return client;
    }
}

internal sealed class HttpBodyCancellationScope : IDisposable
{
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource? _timeoutSource;
    private readonly CancellationTokenSource? _linkedSource;

    public HttpBodyCancellationScope(CancellationToken callerToken, TimeSpan? timeout, TimeProvider timeProvider)
    {
        _callerToken = callerToken;
        if (timeout is null)
        {
            Token = callerToken;
            return;
        }

        _timeoutSource = new CancellationTokenSource(timeout.Value, timeProvider);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _timeoutSource.Token);
        Token = _linkedSource.Token;
    }

    public CancellationToken Token { get; }

    public bool IsLateCancellation(Exception exception) =>
        exception is OperationCanceledException
        && Token.IsCancellationRequested;

    /// <summary>Maps a body-phase cancellation to its cause: the caller token or the body timeout.</summary>
    /// <remarks>
    /// Attribution uses which source fired, not the token carried by the exception, because a response
    /// reader may observe cancellation through its own linked token.
    /// </remarks>
    public Exception Translate(Exception exception)
    {
        if (exception is not OperationCanceledException canceled)
            return exception;

        if (_callerToken.IsCancellationRequested)
        {
            return canceled.CancellationToken == _callerToken
                ? exception
                : new OperationCanceledException(canceled.Message, canceled, _callerToken);
        }

        if (_timeoutSource?.IsCancellationRequested == true)
            return new TimeoutException("The HTTP response body exceeded its configured timeout.", exception);

        return exception;
    }

    public void Dispose()
    {
        _linkedSource?.Dispose();
        _timeoutSource?.Dispose();
    }
}

internal static class HttpResponseStatus
{
    /// <summary>Throws <see cref="HttpResponseStatusException"/> for a non-success response.</summary>
    /// <remarks>The body is read only when the policy opts into a bounded preview, under the body-phase token.</remarks>
    public static async ValueTask ThrowIfUnsuccessfulAsync(
        HttpResponseMessage response,
        string operationName,
        HttpResponsePolicySnapshot policy,
        HttpBodyCancellationScope bodyCancellation)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? preview = null;
        var previewTruncated = false;
        if (policy.IncludeErrorBodyPreview)
        {
            (preview, previewTruncated) = await HttpResponseErrorPreview.ReadAsync(
                response.Content,
                policy.MaxErrorBodyBytes,
                bodyCancellation.Token).ConfigureAwait(false);
        }

        throw new HttpResponseStatusException(operationName, response.StatusCode, preview, previewTruncated);
    }
}

internal static class HttpResponseErrorPreview
{
    public static async ValueTask<(string Preview, bool Truncated)> ReadAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var retained = new MemoryStream();
        var buffer = new byte[Math.Min(8192, maxBytes)];
        long consumed = 0;
        var maxAndSentinel = (long)maxBytes + 1;
        var truncated = false;

        while (consumed < maxAndSentinel)
        {
            var requestCount = (int)Math.Min(buffer.Length, maxAndSentinel - consumed);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requestCount), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            var retainedCount = (int)Math.Min(read, (long)maxBytes - consumed);
            if (retainedCount > 0)
                await retained.WriteAsync(buffer.AsMemory(0, retainedCount), cancellationToken).ConfigureAwait(false);

            consumed += read;
            if (consumed > maxBytes)
                truncated = true;
        }

        return (Encoding.UTF8.GetString(retained.GetBuffer(), 0, checked((int)retained.Length)), truncated);
    }
}

internal static class HttpCleanup
{
    public static void AddDisposeFailure(IDisposable? resource, List<Exception> failures)
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    /// <summary>Surfaces cleanup failures without hiding the outcome of the operation.</summary>
    /// <remarks>
    /// A lone primary failure is left to propagate unchanged. With cleanup failures, a cancellation stays an
    /// <see cref="OperationCanceledException"/> with the same token and a body timeout stays a
    /// <see cref="TimeoutException"/>; each carries an <see cref="AggregateException"/> of the primary failure followed
    /// by the cleanup failures. Any other primary failure is combined into an <see cref="AggregateException"/> in the same order.
    /// </remarks>
    public static void ThrowIfNeeded(Exception? primary, List<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
            return;

        if (primary is not null)
        {
            var failures = new List<Exception>(cleanupFailures.Count + 1) { primary };
            failures.AddRange(cleanupFailures);
            var combined = new AggregateException("The HTTP operation and resource cleanup both failed.", failures);
            throw primary switch
            {
                OperationCanceledException canceled => new OperationCanceledException(canceled.Message, combined, canceled.CancellationToken),
                TimeoutException timeout => new TimeoutException(timeout.Message, combined),
                _ => combined,
            };
        }

        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();

        throw new AggregateException("Multiple HTTP resources failed to clean up.", cleanupFailures);
    }
}
