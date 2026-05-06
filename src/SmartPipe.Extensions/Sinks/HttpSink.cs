using System.Net.Http.Json;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Sends items to HTTP API endpoint. Supports Polly resilience pipeline.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class HttpSink<T> : ISink<T>
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ResiliencePipeline? _resilience;

    /// <summary>Create HTTP sink for given endpoint.</summary>
    /// <param name="http">HTTP client.</param>
    /// <param name="url">Target URL.</param>
    /// <param name="resilience">Optional resilience pipeline.</param>
    public HttpSink(HttpClient http, string url, ResiliencePipeline? resilience = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _url = url ?? throw new ArgumentNullException(nameof(url));
        _resilience = resilience;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (!result.IsSuccess || result.Value == null) return;

        if (_resilience != null)
            await _resilience.ExecuteAsync(async token => { await _http.PostAsJsonAsync(_url, result.Value, token); }, ct);
        else
            await _http.PostAsJsonAsync(_url, result.Value, ct);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
