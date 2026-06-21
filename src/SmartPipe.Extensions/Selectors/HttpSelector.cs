using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// HTTP-based data source that fetches data from an API endpoint.
/// Integrates with Microsoft.Extensions.Http.Resilience for retry and circuit breaker.
/// </summary>
/// <typeparam name="T">Response type.</typeparam>
public class HttpSelector<T> : IPipelineSource<T>
{
    private readonly HttpClient _httpClient;
    private readonly string _requestUri;
    private readonly ResiliencePipeline? _pipeline;
    private readonly ILogger<HttpSelector<T>>? _logger;
    private readonly JsonTypeInfo<List<T>>? _listTypeInfo;
    private readonly JsonTypeInfo<T>? _itemTypeInfo;
    private readonly HttpSelectorStreamingMode? _streamingMode;

    /// <summary>Create HTTP source for given endpoint.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    public HttpSelector(HttpClient httpClient, string requestUri)
        : this(httpClient, requestUri, pipeline: null, logger: null)
    {
    }

    /// <summary>Create HTTP source for given endpoint with a resilience pipeline.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="pipeline">Resilience pipeline (retry/circuit-breaker).</param>
    public HttpSelector(HttpClient httpClient, string requestUri, ResiliencePipeline pipeline)
        : this(httpClient, requestUri, pipeline, logger: null)
    {
    }

    /// <summary>Create HTTP source for given endpoint with logging.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="logger">Logger.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        ILogger<HttpSelector<T>> logger)
        : this(httpClient, requestUri, pipeline: null, logger)
    {
    }

    /// <summary>Create HTTP source for given endpoint.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="pipeline">Optional resilience pipeline (retry/circuit-breaker).</param>
    /// <param name="logger">Optional logger.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        ResiliencePipeline? pipeline,
        ILogger<HttpSelector<T>>? logger
    )
        : this(httpClient, requestUri, pipeline, logger, listTypeInfo: null)
    {
    }

    /// <summary>Create HTTP source with source-generated JSON metadata.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="listTypeInfo">Source-generated JSON metadata for the response array.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        JsonTypeInfo<List<T>> listTypeInfo)
        : this(httpClient, requestUri, listTypeInfo, pipeline: null, logger: null)
    {
    }

    /// <summary>Create streaming HTTP source with source-generated JSON metadata.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="itemTypeInfo">Source-generated JSON metadata for response items.</param>
    /// <param name="streamingMode">Streaming response format.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        JsonTypeInfo<T> itemTypeInfo,
        HttpSelectorStreamingMode streamingMode)
        : this(httpClient, requestUri, pipeline: null, logger: null, listTypeInfo: null, itemTypeInfo, streamingMode)
    {
    }

    /// <summary>Create HTTP source with source-generated JSON metadata.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="listTypeInfo">Source-generated JSON metadata for the response array.</param>
    /// <param name="pipeline">Optional resilience pipeline (retry/circuit-breaker).</param>
    /// <param name="logger">Optional logger.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        JsonTypeInfo<List<T>> listTypeInfo,
        ResiliencePipeline? pipeline,
        ILogger<HttpSelector<T>>? logger)
        : this(httpClient, requestUri, pipeline, logger, listTypeInfo)
    {
    }

    /// <summary>Create streaming HTTP source with source-generated JSON metadata.</summary>
    /// <param name="httpClient">HTTP client instance.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="itemTypeInfo">Source-generated JSON metadata for response items.</param>
    /// <param name="streamingMode">Streaming response format.</param>
    /// <param name="pipeline">Optional resilience pipeline (retry/circuit-breaker).</param>
    /// <param name="logger">Optional logger.</param>
    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        JsonTypeInfo<T> itemTypeInfo,
        HttpSelectorStreamingMode streamingMode,
        ResiliencePipeline? pipeline,
        ILogger<HttpSelector<T>>? logger)
        : this(httpClient, requestUri, pipeline, logger, listTypeInfo: null, itemTypeInfo, streamingMode)
    {
    }

    private HttpSelector(
        HttpClient httpClient,
        string requestUri,
        ResiliencePipeline? pipeline,
        ILogger<HttpSelector<T>>? logger,
        JsonTypeInfo<List<T>>? listTypeInfo,
        JsonTypeInfo<T>? itemTypeInfo = null,
        HttpSelectorStreamingMode? streamingMode = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        if (streamingMode is not null && !Enum.IsDefined(streamingMode.Value))
            throw new ArgumentOutOfRangeException(nameof(streamingMode), streamingMode, "Streaming mode is invalid.");
        if (streamingMode is not null)
            ArgumentNullException.ThrowIfNull(itemTypeInfo);

        _pipeline = pipeline;
        _logger = logger;
        _listTypeInfo = listTypeInfo;
        _itemTypeInfo = itemTypeInfo;
        _streamingMode = streamingMode;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        _logger?.LogInformation("Fetching data from {Uri}", _requestUri);

        using var response = await SendAsync(ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (_streamingMode is not null)
        {
            await foreach (var item in ReadStreamingAsync(response, ct).ConfigureAwait(false))
                yield return ProcessingEnvelope<T>.Create(item);
            yield break;
        }

        var items = _listTypeInfo is null
            ? await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct)
            : await response.Content.ReadFromJsonAsync(_listTypeInfo, ct);

        if (items != null)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return ProcessingEnvelope<T>.Create(item);
            }
        }

        _logger?.LogInformation("Fetched {Count} items from {Uri}", items?.Count ?? 0, _requestUri);
    }

    private async ValueTask<HttpResponseMessage> SendAsync(CancellationToken ct)
    {
        return _pipeline != null
            ? await _pipeline.ExecuteAsync(
                async token => await _httpClient.GetAsync(
                    _requestUri,
                    HttpCompletionOption.ResponseHeadersRead,
                    token),
                ct
            )
            : await _httpClient.GetAsync(_requestUri, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async IAsyncEnumerable<T> ReadStreamingAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var itemTypeInfo = _itemTypeInfo
            ?? throw new InvalidOperationException("Streaming HTTP selector requires item JsonTypeInfo.");

        switch (_streamingMode)
        {
            case HttpSelectorStreamingMode.JsonArray:
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                {
                    await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable(stream, itemTypeInfo, ct)
                        .ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (item is not null)
                            yield return item;
                    }
                }
                break;
            case HttpSelectorStreamingMode.Ndjson:
                await using (var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                using (var reader = new StreamReader(stream))
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                        if (line is null)
                            break;
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var item = JsonSerializer.Deserialize(line, itemTypeInfo);
                        if (item is not null)
                            yield return item;
                    }
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported HTTP streaming mode '{_streamingMode}'.");
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
