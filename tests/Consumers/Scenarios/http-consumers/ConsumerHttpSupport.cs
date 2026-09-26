using SmartPipe.Core;

namespace SmartPipe.Consumers.Http;

/// <summary>Counts sends and disposals of an HTTP handler to prove client ownership.</summary>
sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public int SendCount { get; private set; }
    public int DisposeCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        SendCount++;
        return base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCount++;
        base.Dispose(disposing);
    }
}

/// <summary>An application-owned client factory that serves one expected client name.</summary>
sealed class NamedClientFactory(string expectedName, HttpMessageHandler handler, Uri baseAddress, bool disposeHandler)
    : IHttpClientFactory
{
    public int CreateCount { get; private set; }
    public HttpClient? CreatedClient { get; private set; }

    public HttpClient CreateClient(string name)
    {
        if (name != expectedName)
            throw new InvalidOperationException($"Unexpected HTTP client name: {name}");

        CreateCount++;
        CreatedClient = new HttpClient(handler, disposeHandler) { BaseAddress = baseAddress };
        return CreatedClient;
    }
}

static class ConsumerPipeline
{
    /// <summary>Runs a sink-backed pipeline to completion and rejects any published failure.</summary>
    /// <remarks>Sink-backed runs publish only failures by default; successful items are observed at the sink.</remarks>
    public static async Task RunToCompletionAsync<TInput, TOutput>(
        PipelineDefinition<TInput, TOutput> definition,
        string failureMessage)
    {
        await using var run = await definition.StartAsync().ConfigureAwait(false);
        await foreach (var output in run.Outputs.ReadAllAsync().ConfigureAwait(false))
            ConsumerCheck.Require(output.Result.IsSuccess, failureMessage);

        await run.Completion.ConfigureAwait(false);
    }
}

static class ConsumerCheck
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
