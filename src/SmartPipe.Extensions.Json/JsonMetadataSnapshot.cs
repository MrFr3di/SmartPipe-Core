#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Json;

internal static class JsonMetadataSnapshot
{
    public static (JsonTypeInfo<T> Item, JsonTypeInfo<List<T>> Batch) ForFile<T>(
        JsonTypeInfo<T>? itemTypeInfo,
        JsonTypeInfo<List<T>>? batchTypeInfo,
        int? maxDepth = null)
    {
        ArgumentNullException.ThrowIfNull(itemTypeInfo);
        ArgumentNullException.ThrowIfNull(batchTypeInfo);
        if (itemTypeInfo.Type != typeof(T) || batchTypeInfo.Type != typeof(List<T>))
            throw new ArgumentException("JSON type metadata does not match the source item and batch types.");
        if (!ReferenceEquals(itemTypeInfo.Options, batchTypeInfo.Options))
            throw new ArgumentException("Item and batch JSON type metadata must come from the same serializer context.");

        var options = CloneOptions(itemTypeInfo.Options, maxDepth);
        return (Resolve<T>(options), Resolve<List<T>>(options));
    }

    public static JsonTypeInfo<T> ForValue<T>(JsonTypeInfo<T>? typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Type != typeof(T))
            throw new ArgumentException("JSON type metadata does not match the requested value type.");

        return Resolve<T>(CloneOptions(typeInfo.Options, maxDepth: null));
    }

    public static JsonTypeInfo<DeadLetterEnvelope<T>> ForDeadLetterEnvelope<T>(
        JsonTypeInfo<DeadLetterEnvelope<T>>? typeInfo,
        int? maxDepth = null)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeInfo.Type != typeof(DeadLetterEnvelope<T>))
            throw new ArgumentException("JSON type metadata does not match the dead-letter envelope type.");

        return Resolve<DeadLetterEnvelope<T>>(CloneOptions(typeInfo.Options, maxDepth));
    }

    private static JsonSerializerOptions CloneOptions(JsonSerializerOptions source, int? maxDepth)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TypeInfoResolver is null)
            throw new ArgumentException("Source-generated JSON metadata must provide a type-info resolver.");

        var clone = new JsonSerializerOptions(source)
        {
            TypeInfoResolver = source.TypeInfoResolver,
        };
        if (maxDepth.HasValue)
            clone.MaxDepth = maxDepth.Value;

        clone.MakeReadOnly();
        return clone;
    }

    private static JsonTypeInfo<T> Resolve<T>(JsonSerializerOptions options)
    {
        try
        {
            if (options.GetTypeInfo(typeof(T)) is JsonTypeInfo<T> typeInfo)
                return typeInfo;
        }
        catch (NotSupportedException exception)
        {
            throw new ArgumentException(
                $"The JSON metadata resolver cannot resolve '{typeof(T)}'.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ArgumentException(
                $"The JSON metadata resolver cannot resolve '{typeof(T)}'.",
                exception);
        }

        throw new ArgumentException(
            $"The JSON metadata resolver returned incompatible metadata for '{typeof(T)}'.");
    }
}
