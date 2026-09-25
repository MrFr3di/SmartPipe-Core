#nullable enable

using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;
using SmartPipe.Extensions.Http;
using SmartPipe.Extensions.Json;

namespace SmartPipe.Extensions.Http.Json;

/// <summary>Creates source-generated JSON request content for HTTP sinks.</summary>
public static class HttpJsonRequestContent
{
    /// <summary>Creates a factory for HTTP POST requests containing one envelope payload as JSON.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="requestUri">The destination URI.</param>
    /// <param name="typeInfo">Source-generated metadata for the payload.</param>
    /// <returns>A factory that creates a fresh request for each write.</returns>
    public static HttpRequestFactory<T> Post<T>(Uri requestUri, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        var frozenTypeInfo = JsonMetadataSnapshot.ForValue(typeInfo);
        return CreateRequest;

        ValueTask<HttpRequestMessage> CreateRequest(
            ProcessingEnvelope<T> envelope,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(envelope);
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(envelope.Payload, frozenTypeInfo),
            };
            return ValueTask.FromResult(request);
        }
    }
}
