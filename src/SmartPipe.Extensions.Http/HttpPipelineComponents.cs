#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Extensions.Http;

#pragma warning disable RS0026 // Direct-client and factory overloads are a frozen part of this initial HTTP contract.
/// <summary>Creates runtime-owned HTTP source and sink components.</summary>
public static class HttpPipelineComponents
{
    /// <summary>Creates an HTTP source using an application-owned client.</summary>
    public static PipelineComponent<IPipelineSource<T>> Source<T>(
        HttpClient client,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptions? options = null)
    {
        var clients = HttpClientSource.Borrowed(client);
        return CreateSource(clients, requestFactory, responseReader, options);
    }

    /// <summary>Creates an HTTP source using a client from an application-owned factory.</summary>
    public static PipelineComponent<IPipelineSource<T>> Source<T>(
        IHttpClientFactory clientFactory,
        string clientName,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptions? options = null)
    {
        var clients = HttpClientSource.FromFactory(clientFactory, clientName);
        return CreateSource(clients, requestFactory, responseReader, options);
    }

    /// <summary>Creates an HTTP sink using an application-owned client.</summary>
    public static PipelineComponent<IPipelineSink<T>> Sink<T>(
        HttpClient client,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        var clients = HttpClientSource.Borrowed(client);
        return CreateSink(clients, requestFactory, idempotencyKeySelector, options);
    }

    /// <summary>Creates an HTTP sink using a client from an application-owned factory.</summary>
    public static PipelineComponent<IPipelineSink<T>> Sink<T>(
        IHttpClientFactory clientFactory,
        string clientName,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? options = null)
    {
        var clients = HttpClientSource.FromFactory(clientFactory, clientName);
        return CreateSink(clients, requestFactory, idempotencyKeySelector, options);
    }

    private static PipelineComponent<IPipelineSource<T>> CreateSource<T>(
        HttpClientSource clients,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptions? options)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(responseReader);
        var snapshot = HttpOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSource<T>>((context, _) =>
            ValueTask.FromResult<IPipelineSource<T>>(
                new HttpPipelineSource<T>(clients, requestFactory, responseReader, snapshot, context)));
    }

    private static PipelineComponent<IPipelineSink<T>> CreateSink<T>(
        HttpClientSource clients,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector,
        HttpSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(requestFactory);
        var snapshot = HttpOptionsSnapshot.Create(options);

        return PipelineComponent.RuntimeOwned<IPipelineSink<T>>((context, _) =>
            ValueTask.FromResult<IPipelineSink<T>>(
                new HttpPipelineSink<T>(clients, requestFactory, idempotencyKeySelector, snapshot, context.TimeProvider)));
    }
}
#pragma warning restore RS0026
