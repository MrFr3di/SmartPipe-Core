#nullable enable

using System.Text.Json.Serialization.Metadata;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Sends items to HTTP using an <see cref="IHttpClientFactory"/> named or default client.</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class HttpClientFactorySink<T> : IPipelineSink<T>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _clientName;
    private readonly string _url;
    private readonly JsonTypeInfo<T>? _jsonTypeInfo;
    private readonly ResiliencePipeline? _resilience;
    private readonly bool _useTraceIdIdempotencyKey;

    /// <summary>Create a factory-backed HTTP sink.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    /// <param name="resilience">Optional resilience pipeline.</param>
    /// <param name="jsonTypeInfo">Optional source-generated JSON metadata.</param>
    /// <param name="useTraceIdIdempotencyKey">Whether to send TraceId as the Idempotency-Key header.</param>
    public HttpClientFactorySink(
        IHttpClientFactory httpClientFactory,
        string url,
        string clientName = "",
        ResiliencePipeline? resilience = null,
        JsonTypeInfo<T>? jsonTypeInfo = null,
        bool useTraceIdIdempotencyKey = false)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _clientName = clientName ?? throw new ArgumentNullException(nameof(clientName));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _jsonTypeInfo = jsonTypeInfo;
        _resilience = resilience;
        _useTraceIdIdempotencyKey = useTraceIdIdempotencyKey;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(_clientName);
        var sink = _jsonTypeInfo is null
            ? new HttpSink<T>(client, _url, _resilience, _useTraceIdIdempotencyKey)
            : new HttpSink<T>(client, _url, _jsonTypeInfo, _resilience, _useTraceIdIdempotencyKey);
        return sink.WriteAsync(envelope, ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
