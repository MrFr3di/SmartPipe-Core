#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Extensions.Http;

#pragma warning disable RS0026 // Direct-client and factory overloads intentionally share optional policy parameters.
/// <summary>Connects HTTP components to typed pipeline definitions.</summary>
public static class HttpPipelineDefinitionBuilderExtensions
{
    /// <summary>Begins a definition with an HTTP source using an application-owned client.</summary>
    public static PipelineDefinitionBuilder<T> FromHttp<T>(
        PipelineKey pipelineKey,
        HttpClient client,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptions? options = null) =>
        PipelineDefinitionBuilder.From(
            pipelineKey,
            HttpPipelineComponents.Source(client, requestFactory, responseReader, options));

    /// <summary>Begins a definition with an HTTP source using a client from an application-owned factory.</summary>
    public static PipelineDefinitionBuilder<T> FromHttp<T>(
        PipelineKey pipelineKey,
        IHttpClientFactory clientFactory,
        string clientName,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptions? options = null) =>
        PipelineDefinitionBuilder.From(
            pipelineKey,
            HttpPipelineComponents.Source(clientFactory, clientName, requestFactory, responseReader, options));

    /// <summary>Attaches an HTTP sink to a definition using an application-owned client.</summary>
    public static PipelineDefinition<T, T> ToHttp<T>(
        this PipelineDefinitionBuilder<T> builder,
        HttpClient client,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(HttpPipelineComponents.Sink(client, requestFactory, idempotencyKeySelector, options));
    }

    /// <summary>Attaches an HTTP sink to a transformed definition using an application-owned client.</summary>
    public static PipelineDefinition<TPipelineInput, TCurrent> ToHttp<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        HttpClient client,
        HttpRequestFactory<TCurrent> requestFactory,
        Func<ProcessingEnvelope<TCurrent>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(HttpPipelineComponents.Sink(client, requestFactory, idempotencyKeySelector, options));
    }

    /// <summary>Attaches an HTTP sink to a definition using a client from an application-owned factory.</summary>
    public static PipelineDefinition<T, T> ToHttp<T>(
        this PipelineDefinitionBuilder<T> builder,
        IHttpClientFactory clientFactory,
        string clientName,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(HttpPipelineComponents.Sink(
            clientFactory,
            clientName,
            requestFactory,
            idempotencyKeySelector,
            options));
    }

    /// <summary>Attaches an HTTP sink to a transformed definition using a client from an application-owned factory.</summary>
    public static PipelineDefinition<TPipelineInput, TCurrent> ToHttp<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        IHttpClientFactory clientFactory,
        string clientName,
        HttpRequestFactory<TCurrent> requestFactory,
        Func<ProcessingEnvelope<TCurrent>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.To(HttpPipelineComponents.Sink(
            clientFactory,
            clientName,
            requestFactory,
            idempotencyKeySelector,
            options));
    }
}
#pragma warning restore RS0026
