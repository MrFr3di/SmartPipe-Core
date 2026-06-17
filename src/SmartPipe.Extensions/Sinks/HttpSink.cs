using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Sends items to HTTP API endpoint. Supports Polly resilience pipeline.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class HttpSink<T> : IPipelineSink<T>
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ResiliencePipeline? _resilience;
    private readonly JsonTypeInfo<T>? _jsonTypeInfo;
    private readonly bool _useTraceIdIdempotencyKey;
    private const string IdempotencyKeyHeaderName = "Idempotency-Key";

    /// <summary>Create HTTP sink for given endpoint.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    public HttpSink(HttpClient http, string url)
        : this(http, url, resilience: null)
    {
    }

    /// <summary>Create HTTP sink for given endpoint.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="resilience">Optional resilience pipeline.</param>
    public HttpSink(HttpClient http, string url, ResiliencePipeline? resilience)
        : this(http, url, resilience, jsonTypeInfo: null, useTraceIdIdempotencyKey: false)
    {
    }

    /// <summary>Create HTTP sink with optional TraceId idempotency key support.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="resilience">Optional resilience pipeline.</param>
    /// <param name="useTraceIdIdempotencyKey">Whether to send TraceId as the Idempotency-Key header.</param>
    public HttpSink(
        HttpClient http,
        string url,
        ResiliencePipeline? resilience,
        bool useTraceIdIdempotencyKey)
        : this(http, url, resilience, jsonTypeInfo: null, useTraceIdIdempotencyKey)
    {
    }

    /// <summary>Create HTTP sink with source-generated JSON metadata.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="jsonTypeInfo">Source-generated JSON type metadata.</param>
    public HttpSink(HttpClient http, string url, JsonTypeInfo<T> jsonTypeInfo)
        : this(http, url, jsonTypeInfo, resilience: null, useTraceIdIdempotencyKey: false)
    {
    }

    /// <summary>Create HTTP sink with source-generated JSON metadata.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="jsonTypeInfo">Source-generated JSON type metadata.</param>
    /// <param name="resilience">Optional resilience pipeline.</param>
    /// <param name="useTraceIdIdempotencyKey">Whether to send TraceId as the Idempotency-Key header.</param>
    public HttpSink(
        HttpClient http,
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        ResiliencePipeline? resilience,
        bool useTraceIdIdempotencyKey)
        : this(http, url, resilience, jsonTypeInfo, useTraceIdIdempotencyKey)
    {
    }

    private HttpSink(
        HttpClient http,
        string url,
        ResiliencePipeline? resilience,
        JsonTypeInfo<T>? jsonTypeInfo,
        bool useTraceIdIdempotencyKey)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _resilience = resilience;
        _jsonTypeInfo = jsonTypeInfo;
        _useTraceIdIdempotencyKey = useTraceIdIdempotencyKey;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload == null)
            return;

        if (_resilience != null)
            await _resilience.ExecuteAsync(
                async token =>
                {
                    await PostAsync(envelope, token);
                },
                ct
            );
        else
            await PostAsync(envelope, ct);
    }

    private async Task PostAsync(ProcessingEnvelope<T> envelope, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        if (_useTraceIdIdempotencyKey)
            request.Headers.TryAddWithoutValidation(IdempotencyKeyHeaderName, envelope.TraceId.ToString());

        request.Content = _jsonTypeInfo is null
            ? JsonContent.Create(envelope.Payload)
            : JsonContent.Create(envelope.Payload, _jsonTypeInfo);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
