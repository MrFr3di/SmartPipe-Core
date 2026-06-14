using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Reads failed items from DeadLetterSink JSON for reprocessing.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class DeadLetterSource<T> : IPipelineSource<T>
{
    private readonly string _path;
    private readonly Func<JsonElement, T?> _deserializeValue;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Create source for given dead letter JSON file.</summary>
    /// <param name="path">Path to dead letter JSON file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON replay is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON replay may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSource(string path) =>
        (_path, _deserializeValue) = (
            path ?? throw new ArgumentNullException(nameof(path)),
            static element => element.Deserialize<T>(_jsonOptions)
        );

    /// <summary>Create source for given dead letter JSON file using source-generated JSON metadata.</summary>
    /// <param name="path">Path to dead letter JSON file.</param>
    /// <param name="valueTypeInfo">Source-generated type information for replayed values.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSource(string path, JsonTypeInfo<T> valueTypeInfo)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        ArgumentNullException.ThrowIfNull(valueTypeInfo);
        _deserializeValue = element => element.Deserialize(valueTypeInfo);
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
            throw new FileNotFoundException($"Dead letter file not found: {_path}");
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var json = await File.ReadAllTextAsync(_path, ct);

        JsonDocument? doc = null;
        JsonException? rootParseFailure = null;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            rootParseFailure = ex;
        }

        if (doc != null)
        {
            using (doc)
            {
                foreach (var context in ProcessRoot(doc.RootElement))
                    yield return context;
            }

            yield break;
        }

        foreach (var context in ProcessNdjson(json, rootParseFailure, ct))
            yield return context;
    }

    private IEnumerable<ProcessingEnvelope<T>> ProcessRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                var context = ProcessElement(element);
                if (context != null)
                    yield return context;
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var context = ProcessElement(root);
            if (context != null)
                yield return context;
        }
        else
        {
            throw new JsonException($"Unexpected JSON root element type: {root.ValueKind}");
        }
    }

    private List<ProcessingEnvelope<T>> ProcessNdjson(
        string json,
        JsonException? rootParseFailure,
        CancellationToken ct)
    {
        var contexts = new List<ProcessingEnvelope<T>>();
        using var reader = new StringReader(json);
        var parsedAnyLine = false;

        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            parsedAnyLine = true;
            try
            {
                using var lineDoc = JsonDocument.Parse(line);
                contexts.AddRange(ProcessRoot(lineDoc.RootElement));
            }
            catch (JsonException) when (rootParseFailure != null)
            {
                throw rootParseFailure;
            }
        }

        if (!parsedAnyLine && rootParseFailure != null)
            throw rootParseFailure;

        return contexts;
    }

    private ProcessingEnvelope<T>? ProcessElement(JsonElement element)
    {
        if (!element.TryGetProperty("OriginalPayload", out var payloadProp))
            return null;

        var value = _deserializeValue(payloadProp);
        if (value is null)
            return null;

        var pipelineId = ReadString(element, "PipelineId") ?? "dead-letter-replay";
        var runId = ReadString(element, "RunId") ?? Guid.NewGuid().ToString("N");
        var traceId = ReadTraceId(element);
        var metadata = ReadMetadata(element);
        var createdAtUtc = ReadDateTimeOffset(element, "FailedAtUtc");

        return ProcessingEnvelope<T>.Create(
            value,
            pipelineId,
            runId,
            traceId,
            metadata,
            createdAtUtc);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static ulong ReadTraceId(JsonElement element)
    {
        return element.TryGetProperty("TraceId", out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetUInt64(out var traceId)
            ? traceId
            : 0UL;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.TryGetDateTimeOffset(out var value)
            ? value
            : null;
    }

    private static MetadataBag ReadMetadata(JsonElement element)
    {
        if (!element.TryGetProperty("Metadata", out var metadataElement))
            return MetadataBag.Empty;

        var itemsElement = metadataElement.TryGetProperty("Items", out var items)
            ? items
            : metadataElement;

        if (itemsElement.ValueKind != JsonValueKind.Object)
            return MetadataBag.Empty;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in itemsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                values[property.Name] = property.Value.GetString()!;
        }

        return values.Count == 0 ? MetadataBag.Empty : MetadataBag.From(values);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
