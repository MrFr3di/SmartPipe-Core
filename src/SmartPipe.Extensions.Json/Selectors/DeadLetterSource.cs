using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Shared.JsonFraming;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Reads failed items from DeadLetterSink JSON for reprocessing.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class DeadLetterSource<T> : IPipelineSource<T>
{
    private readonly string _path;
    private readonly Func<JsonElement, T?>? _deserializeValue;
    private readonly IDeadLetterSerializer<T>? _serializer;
    private readonly DeadLetterSourceOptions _sourceOptions = new();
    private readonly ILogger<DeadLetterSource<T>>? _logger;

    [RequiresUnreferencedCode("Reflection-based dead-letter JSON replay is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON replay may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    private static class ReflectionJsonOptions
    {
        internal static JsonSerializerOptions Instance { get; } = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
    }
    /// <summary>Create source for given dead letter JSON file.</summary>
    /// <param name="path">Path to dead letter JSON file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON replay is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON replay may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSource(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _serializer = new JsonLinesDeadLetterSerializer<T>(ReflectionJsonOptions.Instance);
    }

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

    /// <summary>Create a source using source-generated JSON metadata with explicit options.</summary>
    /// <param name="path">Path to dead letter JSON file.</param>
    /// <param name="valueTypeInfo">Source-generated type information for replayed values.</param>
    /// <param name="options">Input layout, depth, and size limits.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSource(string path, JsonTypeInfo<T> valueTypeInfo, DeadLetterSourceOptions options)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        ArgumentNullException.ThrowIfNull(valueTypeInfo);
        _deserializeValue = element => element.Deserialize(valueTypeInfo);
        _sourceOptions = JsonInputOptionsValidator.Validate(options, logger: null);
    }


    /// <summary>Create an AOT-safe streaming source using envelope metadata.</summary>
    public DeadLetterSource(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> envelopeTypeInfo,
        DeadLetterSourceOptions options)
        : this(
            path,
            new JsonLinesDeadLetterSerializer<T>(envelopeTypeInfo),
            options,
            logger: null)
    {
    }

    /// <summary>Create an AOT-safe streaming source using envelope metadata and logging.</summary>
    public DeadLetterSource(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> envelopeTypeInfo,
        DeadLetterSourceOptions options,
        ILogger<DeadLetterSource<T>> logger)
        : this(
            path,
            new JsonLinesDeadLetterSerializer<T>(envelopeTypeInfo),
            options,
            logger)
    {
    }

    /// <summary>Create a streaming source using an explicit dead-letter serializer.</summary>
    public DeadLetterSource(
        string path,
        IDeadLetterSerializer<T> serializer,
        DeadLetterSourceOptions options)
        : this(path, serializer, options, logger: null)
    {
    }

    /// <summary>Create a streaming source using an explicit serializer and logger.</summary>
    public DeadLetterSource(
        string path,
        IDeadLetterSerializer<T> serializer,
        DeadLetterSourceOptions options,
        ILogger<DeadLetterSource<T>>? logger)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _sourceOptions = JsonInputOptionsValidator.Validate(options, logger);
        _logger = logger;
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
        if (_serializer != null)
        {
            await foreach (var envelope in ReadCustomSerializerPathAsync(ct).ConfigureAwait(false))
                yield return envelope;
            yield break;
        }

        await foreach (var envelope in ReadLegacyPathAsync(ct).ConfigureAwait(false))
            yield return envelope;
    }

    private async IAsyncEnumerable<ProcessingEnvelope<T>> ReadCustomSerializerPathAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var serializerProbe = await JsonStreamProbe.ProbeAsync(stream, ct).ConfigureAwait(false);
        stream.Position = serializerProbe.ContentStartOffset;
        if (serializerProbe.FirstSignificantByte is null)
            yield break;
        var array = _sourceOptions.Format == JsonFileFormat.Array
            || (_sourceOptions.Format == JsonFileFormat.Auto && serializerProbe.FirstSignificantByte == (byte)'[');
        if (array && _sourceOptions.InvalidRecordBehavior == InvalidJsonRecordBehavior.SkipAndLog)
            throw new ArgumentException("SkipAndLog is supported only for independently framed JSON records.");
        var envelopes = array
            ? ReadDocumentEnvelopesAsync(stream, ct)
            : new DeadLetterRecordReader<T>().ReadFramedAsync(
                stream, _serializer!, _sourceOptions, _logger, _path, ct);
        await foreach (var envelope in envelopes.ConfigureAwait(false))
        {
            if (envelope.OriginalPayload is null)
                throw new JsonException($"Dead-letter record in '{_path}' has a null OriginalPayload.");

            yield return ProcessingEnvelope<T>.Create(
                envelope.OriginalPayload,
                envelope.PipelineId,
                envelope.RunId,
                envelope.TraceId,
                envelope.Metadata,
                envelope.FailedAtUtc);
        }
    }

    private async IAsyncEnumerable<ProcessingEnvelope<T>> ReadLegacyPathAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var legacyStream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var legacyProbe = await JsonStreamProbe.ProbeAsync(legacyStream, ct).ConfigureAwait(false);
        legacyStream.Position = legacyProbe.ContentStartOffset;
        if (legacyProbe.FirstSignificantByte == null)
            yield break;

        var legacyOptions = new JsonSerializerOptions { MaxDepth = _sourceOptions.MaxDepth };
        var legacyContext = new JsonInfrastructureContext(legacyOptions);
        if (_sourceOptions.Format == JsonFileFormat.Ndjson)
        {
            long recordIndex = 0;
            await foreach (var record in Utf8LineRecordReader.ReadAsync(
                legacyStream,
                _sourceOptions.MaxRecordSizeBytes,
                ct).ConfigureAwait(false))
            {
                recordIndex++;
                if (record.TooLarge)
                {
                    throw new JsonException(
                        $"JSON record {recordIndex} in '{_path}' exceeds MaxRecordSizeBytes ({_sourceOptions.MaxRecordSizeBytes}).");
                }

                JsonRecordValidator.Validate(
                    record.Bytes,
                    _sourceOptions.MaxDepth,
                    _path,
                    recordIndex);
                var element = JsonSerializer.Deserialize(record.Bytes, legacyContext.JsonElement);
                var context = ProcessElement(element);
                if (context != null)
                    yield return context;
            }

            yield break;
        }

        if (_sourceOptions.Format == JsonFileFormat.Array
            && legacyProbe.FirstSignificantByte != (byte)'[')
        {
            throw new JsonException($"Expected a root JSON array in '{_path}'.");
        }

        var topLevelValues = legacyProbe.FirstSignificantByte != (byte)'[';
        using var limitedLegacyStream = new JsonUnframedInputLimitStream(
            legacyStream,
            _sourceOptions.MaxUnframedInputSizeBytes,
            _path);
        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable(
            limitedLegacyStream,
            legacyContext.JsonElement,
            topLevelValues,
            ct).ConfigureAwait(false))
        {
            var context = ProcessElement(element);
            if (context != null)
                yield return context;
        }
    }

    private async IAsyncEnumerable<DeadLetterEnvelope<T>> ReadDocumentEnvelopesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var start = stream.Position;
        using (var validationStream = new JsonUnframedInputLimitStream(
            stream, _sourceOptions.MaxUnframedInputSizeBytes, _path))
            await JsonDocumentValidator.ValidateAsync(
                validationStream, _sourceOptions.MaxDepth, _path, ct);
        stream.Position = start;
        using var limitedStream = new JsonUnframedInputLimitStream(
            stream,
            _sourceOptions.MaxUnframedInputSizeBytes,
            _path);
        await foreach (var envelope in _serializer!.ReadAsync(limitedStream, ct).ConfigureAwait(false))
            yield return envelope;
    }

    private ProcessingEnvelope<T>? ProcessElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException($"Unexpected JSON root element type: {element.ValueKind}");
        if (!element.TryGetProperty("OriginalPayload", out var payloadProp))
            throw new JsonException($"Dead-letter record in '{_path}' is missing OriginalPayload.");

        var value = _deserializeValue!(payloadProp);
        if (value is null)
            throw new JsonException($"Dead-letter record in '{_path}' has a null OriginalPayload.");

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
