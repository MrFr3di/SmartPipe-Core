#nullable enable

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Extensions.Http;
using SmartPipe.Extensions.Json;
using SmartPipe.Shared;
using SmartPipe.Shared.JsonFraming;

namespace SmartPipe.Extensions.Http.Json;

/// <summary>Creates bounded streaming readers for JSON HTTP responses.</summary>
public static class HttpJsonResponseReaders
{
    /// <summary>Creates a reader for a root JSON array.</summary>
    /// <typeparam name="T">The array item type.</typeparam>
    /// <param name="itemTypeInfo">Source-generated metadata for each item.</param>
    /// <param name="options">The array size, depth, and null-item policies.</param>
    /// <returns>A reader that streams complete array items.</returns>
    public static HttpResponseReader<T> JsonArray<T>(
        JsonTypeInfo<T> itemTypeInfo,
        HttpJsonArrayOptions? options = null)
    {
        var snapshot = HttpJsonOptionsSnapshotFactory.Create(options);
        var frozenTypeInfo = JsonMetadataSnapshot.ForValue(itemTypeInfo, snapshot.MaxDepth);
        return (response, cancellationToken) => ReadArrayAsync(
            response,
            frozenTypeInfo,
            snapshot,
            cancellationToken);
    }

    /// <summary>Creates a reader for newline-delimited JSON records.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="itemTypeInfo">Source-generated metadata for each record.</param>
    /// <param name="options">The record, total-size, depth, and null policies.</param>
    /// <returns>A reader that streams complete records.</returns>
    public static HttpResponseReader<T> Ndjson<T>(
        JsonTypeInfo<T> itemTypeInfo,
        HttpNdjsonOptions? options = null)
    {
        var snapshot = HttpJsonOptionsSnapshotFactory.Create(options);
        var frozenTypeInfo = JsonMetadataSnapshot.ForValue(itemTypeInfo, snapshot.MaxDepth);
        return (response, cancellationToken) => ReadNdjsonAsync(
            response,
            frozenTypeInfo,
            snapshot,
            cancellationToken);
    }

    private static async IAsyncEnumerable<T> ReadArrayAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> itemTypeInfo,
        HttpJsonArrayOptionsSnapshot options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var content = response.Content ?? throw new InvalidOperationException("The HTTP response has no body content.");
        var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limitedBody = new UnframedInputLimitStream(body, options.MaxUnframedBytes, CreateLimitException);

        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable(
            limitedBody,
            itemTypeInfo,
            cancellationToken).ConfigureAwait(false))
        {
            if (IsSkippedNull(item, options.NullItemPolicy, "The HTTP JSON array contains a null item."))
                continue;

            yield return item!;
        }
    }

    private static async IAsyncEnumerable<T> ReadNdjsonAsync<T>(
        HttpResponseMessage response,
        JsonTypeInfo<T> itemTypeInfo,
        HttpNdjsonOptionsSnapshot options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var content = response.Content ?? throw new InvalidOperationException("The HTTP response has no body content.");
        var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var limitedBody = new UnframedInputLimitStream(body, options.MaxUnframedBytes, CreateLimitException);

        await foreach (var record in Utf8LineRecordReader.ReadAsync(
            limitedBody,
            options.MaxRecordSizeBytes,
            cancellationToken).ConfigureAwait(false))
        {
            if (record.TooLarge)
            {
                if (options.OversizeRecordPolicy == HttpJsonOversizeRecordPolicy.Throw)
                    throw new JsonException("An HTTP NDJSON record exceeds the configured record-size limit.");
                continue;
            }

            var item = JsonSerializer.Deserialize(record.Bytes, itemTypeInfo);
            if (IsSkippedNull(item, options.NullItemPolicy, "The HTTP NDJSON response contains a null item."))
                continue;

            yield return item!;
        }
    }

    /// <summary>Applies the null-item policy at a complete item boundary.</summary>
    /// <returns><see langword="true"/> when the item is null and must be skipped.</returns>
    private static bool IsSkippedNull<T>(T? item, HttpJsonNullItemPolicy policy, string message)
    {
        if (item is not null)
            return false;
        if (policy == HttpJsonNullItemPolicy.Throw)
            throw new JsonException(message);
        return true;
    }

    private static JsonException CreateLimitException(long maximumBytes) =>
        new($"The HTTP JSON response body exceeds the configured {maximumBytes}-byte limit.");
}
