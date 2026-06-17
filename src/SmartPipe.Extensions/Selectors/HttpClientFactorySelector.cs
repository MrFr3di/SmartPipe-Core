#nullable enable

using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Reads HTTP JSON arrays using an <see cref="IHttpClientFactory"/> named or default client.</summary>
/// <typeparam name="T">Response item type.</typeparam>
public sealed class HttpClientFactorySelector<T> : IPipelineSource<T>
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _clientName;
    private readonly string _requestUri;
    private readonly JsonTypeInfo<List<T>>? _listTypeInfo;
    private readonly JsonTypeInfo<T>? _itemTypeInfo;
    private readonly HttpSelectorStreamingMode? _streamingMode;
    private readonly ResiliencePipeline? _pipeline;
    private readonly ILogger<HttpSelector<T>>? _logger;

    /// <summary>Create a factory-backed HTTP selector using the default client.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri)
        : this(httpClientFactory, requestUri, clientName: "", pipeline: null, listTypeInfo: null, logger: null)
    {
    }

    /// <summary>Create a factory-backed HTTP selector using a named client.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        string clientName)
        : this(httpClientFactory, requestUri, clientName, pipeline: null, listTypeInfo: null, logger: null)
    {
    }

    /// <summary>Create a factory-backed HTTP selector with a resilience pipeline.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    /// <param name="pipeline">Optional resilience pipeline.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        string clientName,
        ResiliencePipeline? pipeline)
        : this(httpClientFactory, requestUri, clientName, pipeline, listTypeInfo: null, logger: null)
    {
    }

    /// <summary>Create a factory-backed HTTP selector with source-generated JSON metadata.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    /// <param name="pipeline">Optional resilience pipeline.</param>
    /// <param name="listTypeInfo">Optional source-generated JSON metadata for the response array.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        string clientName,
        ResiliencePipeline? pipeline,
        JsonTypeInfo<List<T>>? listTypeInfo)
        : this(httpClientFactory, requestUri, clientName, pipeline, listTypeInfo, logger: null)
    {
    }

    /// <summary>Create a factory-backed HTTP selector.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    /// <param name="pipeline">Optional resilience pipeline.</param>
    /// <param name="listTypeInfo">Optional source-generated JSON metadata for the response array.</param>
    /// <param name="logger">Optional logger.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        string clientName,
        ResiliencePipeline? pipeline,
        JsonTypeInfo<List<T>>? listTypeInfo,
        ILogger<HttpSelector<T>>? logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _clientName = clientName ?? throw new ArgumentNullException(nameof(clientName));
        _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        _listTypeInfo = listTypeInfo;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <summary>Create a factory-backed streaming HTTP selector.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="itemTypeInfo">Source-generated JSON metadata for response items.</param>
    /// <param name="streamingMode">Streaming response format.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        JsonTypeInfo<T> itemTypeInfo,
        HttpSelectorStreamingMode streamingMode)
        : this(httpClientFactory, requestUri, itemTypeInfo, streamingMode, clientName: "", pipeline: null, logger: null)
    {
    }

    /// <summary>Create a factory-backed streaming HTTP selector.</summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="requestUri">Request URI to fetch data from.</param>
    /// <param name="itemTypeInfo">Source-generated JSON metadata for response items.</param>
    /// <param name="streamingMode">Streaming response format.</param>
    /// <param name="clientName">Named client. Empty uses the default client.</param>
    /// <param name="pipeline">Optional resilience pipeline.</param>
    /// <param name="logger">Optional logger.</param>
    public HttpClientFactorySelector(
        IHttpClientFactory httpClientFactory,
        string requestUri,
        JsonTypeInfo<T> itemTypeInfo,
        HttpSelectorStreamingMode streamingMode,
        string clientName,
        ResiliencePipeline? pipeline,
        ILogger<HttpSelector<T>>? logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _clientName = clientName ?? throw new ArgumentNullException(nameof(clientName));
        _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        _itemTypeInfo = itemTypeInfo ?? throw new ArgumentNullException(nameof(itemTypeInfo));
        if (!Enum.IsDefined(streamingMode))
            throw new ArgumentOutOfRangeException(nameof(streamingMode), streamingMode, "Streaming mode is invalid.");
        _streamingMode = streamingMode;
        _pipeline = pipeline;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(CancellationToken ct = default)
    {
        var client = _httpClientFactory.CreateClient(_clientName);
        if (_streamingMode is not null)
        {
            var streamingSelector = new HttpSelector<T>(
                client,
                _requestUri,
                _itemTypeInfo!,
                _streamingMode.Value,
                _pipeline,
                _logger);
            return streamingSelector.ReadEnvelopesAsync(ct);
        }

        var selector = _listTypeInfo is null
            ? new HttpSelector<T>(client, _requestUri, _pipeline, _logger)
            : new HttpSelector<T>(client, _requestUri, _listTypeInfo, _pipeline, _logger);
        return selector.ReadEnvelopesAsync(ct);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
