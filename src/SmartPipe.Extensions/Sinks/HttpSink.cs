using System.Net.Http.Json;
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
                    await PostAsync(envelope.Payload, token);
                },
                ct
            );
        else
            await PostAsync(envelope.Payload, ct);
    }

    private async Task PostAsync(T value, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(_url, value, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
