using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// HTTP-based data source that fetches data from an API endpoint.
/// Integrates with Microsoft.Extensions.Http.Resilience for retry and circuit breaker.
/// </summary>
/// <typeparam name="T">Response type.</typeparam>
public class HttpSelector<T> : ISource<T>
{
    private readonly HttpClient _httpClient;
    private readonly string _requestUri;
    private readonly ResiliencePipeline? _pipeline;
    private readonly ILogger<HttpSelector<T>>? _logger;

    public HttpSelector(
        HttpClient httpClient,
        string requestUri,
        ResiliencePipeline? pipeline = null,
        ILogger<HttpSelector<T>>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        _pipeline = pipeline;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger?.LogInformation("Fetching data from {Uri}", _requestUri);

        var response = _pipeline != null
            ? await _pipeline.ExecuteAsync(
                async token => await _httpClient.GetAsync(_requestUri, token), ct)
            : await _httpClient.GetAsync(_requestUri, ct);

        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct);

        if (items != null)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ProcessingContext<T>(item);
            }
        }

        _logger?.LogInformation("Fetched {Count} items from {Uri}", items?.Count ?? 0, _requestUri);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
