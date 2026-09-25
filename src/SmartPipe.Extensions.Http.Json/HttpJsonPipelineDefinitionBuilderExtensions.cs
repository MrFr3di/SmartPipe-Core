#nullable enable

using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;
using SmartPipe.Extensions.Http;

namespace SmartPipe.Extensions.Http.Json;

#pragma warning disable RS0026 // Direct-client and factory overloads intentionally share parameters.
/// <summary>Connects HTTP JSON codecs to Core pipeline definitions.</summary>
public static class HttpJsonPipelineDefinitionBuilderExtensions
{
    /// <summary>Begins a pipeline with a root JSON array from an HTTP response.</summary>
    public static PipelineDefinitionBuilder<T> FromHttpJsonArray<T>(
        PipelineKey pipelineKey,
        HttpClient client,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        JsonTypeInfo<T> itemTypeInfo,
        HttpJsonArrayOptions? jsonOptions = null,
        HttpSourceOptions? httpOptions = null) =>
        HttpPipelineDefinitionBuilderExtensions.FromHttp(
            pipelineKey,
            client,
            requestFactory,
            HttpJsonResponseReaders.JsonArray(itemTypeInfo, jsonOptions),
            httpOptions);

    /// <summary>Begins a pipeline with a root JSON array using a named factory client.</summary>
    public static PipelineDefinitionBuilder<T> FromHttpJsonArray<T>(
        PipelineKey pipelineKey,
        IHttpClientFactory clientFactory,
        string clientName,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        JsonTypeInfo<T> itemTypeInfo,
        HttpJsonArrayOptions? jsonOptions = null,
        HttpSourceOptions? httpOptions = null) =>
        HttpPipelineDefinitionBuilderExtensions.FromHttp(
            pipelineKey,
            clientFactory,
            clientName,
            requestFactory,
            HttpJsonResponseReaders.JsonArray(itemTypeInfo, jsonOptions),
            httpOptions);

    /// <summary>Begins a pipeline with newline-delimited JSON from an HTTP response.</summary>
    public static PipelineDefinitionBuilder<T> FromHttpNdjson<T>(
        PipelineKey pipelineKey,
        HttpClient client,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        JsonTypeInfo<T> itemTypeInfo,
        HttpNdjsonOptions? jsonOptions = null,
        HttpSourceOptions? httpOptions = null) =>
        HttpPipelineDefinitionBuilderExtensions.FromHttp(
            pipelineKey,
            client,
            requestFactory,
            HttpJsonResponseReaders.Ndjson(itemTypeInfo, jsonOptions),
            httpOptions);

    /// <summary>Begins a pipeline with newline-delimited JSON using a named factory client.</summary>
    public static PipelineDefinitionBuilder<T> FromHttpNdjson<T>(
        PipelineKey pipelineKey,
        IHttpClientFactory clientFactory,
        string clientName,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        JsonTypeInfo<T> itemTypeInfo,
        HttpNdjsonOptions? jsonOptions = null,
        HttpSourceOptions? httpOptions = null) =>
        HttpPipelineDefinitionBuilderExtensions.FromHttp(
            pipelineKey,
            clientFactory,
            clientName,
            requestFactory,
            HttpJsonResponseReaders.Ndjson(itemTypeInfo, jsonOptions),
            httpOptions);

    /// <summary>Attaches a JSON POST sink to an initial pipeline definition using an application-owned client.</summary>
    public static PipelineDefinition<T, T> ToHttpJson<T>(
        this PipelineDefinitionBuilder<T> builder,
        HttpClient client,
        Uri requestUri,
        JsonTypeInfo<T> typeInfo,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return HttpPipelineDefinitionBuilderExtensions.ToHttp(
            builder,
            client,
            HttpJsonRequestContent.Post(requestUri, typeInfo),
            idempotencyKeySelector,
            httpOptions);
    }

    /// <summary>Attaches a JSON POST sink to a transformed pipeline using an application-owned client.</summary>
    public static PipelineDefinition<TPipelineInput, TCurrent> ToHttpJson<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        HttpClient client,
        Uri requestUri,
        JsonTypeInfo<TCurrent> typeInfo,
        Func<ProcessingEnvelope<TCurrent>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return HttpPipelineDefinitionBuilderExtensions.ToHttp(
            builder,
            client,
            HttpJsonRequestContent.Post(requestUri, typeInfo),
            idempotencyKeySelector,
            httpOptions);
    }

    /// <summary>Attaches a JSON POST sink to an initial pipeline definition using a named factory client.</summary>
    public static PipelineDefinition<T, T> ToHttpJson<T>(
        this PipelineDefinitionBuilder<T> builder,
        IHttpClientFactory clientFactory,
        string clientName,
        Uri requestUri,
        JsonTypeInfo<T> typeInfo,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return HttpPipelineDefinitionBuilderExtensions.ToHttp(
            builder,
            clientFactory,
            clientName,
            HttpJsonRequestContent.Post(requestUri, typeInfo),
            idempotencyKeySelector,
            httpOptions);
    }

    /// <summary>Attaches a JSON POST sink to a transformed pipeline using a named factory client.</summary>
    public static PipelineDefinition<TPipelineInput, TCurrent> ToHttpJson<TPipelineInput, TCurrent>(
        this PipelineDefinitionBuilder<TPipelineInput, TCurrent> builder,
        IHttpClientFactory clientFactory,
        string clientName,
        Uri requestUri,
        JsonTypeInfo<TCurrent> typeInfo,
        Func<ProcessingEnvelope<TCurrent>, string?>? idempotencyKeySelector = null,
        HttpSinkOptions? httpOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return HttpPipelineDefinitionBuilderExtensions.ToHttp(
            builder,
            clientFactory,
            clientName,
            HttpJsonRequestContent.Post(requestUri, typeInfo),
            idempotencyKeySelector,
            httpOptions);
    }
}
#pragma warning restore RS0026
