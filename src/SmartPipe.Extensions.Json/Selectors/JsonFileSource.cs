using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams JSON arrays, NDJSON, or batch JSON Lines from a file.</summary>
/// <typeparam name="T">Item type to deserialize.</typeparam>
public class JsonFileSource<T> : IPipelineSource<T>
{
    private readonly string _path;
    private readonly JsonFileSourceOptions _options;
    private readonly ILogger<JsonFileSource<T>>? _logger;
    private readonly Func<Stream> _openStream;
    private readonly Func<Stream, bool, CancellationToken, IAsyncEnumerable<T?>> _deserializeItems;
    private readonly Func<Stream, CancellationToken, IAsyncEnumerable<List<T>?>> _deserializeBatches;
    private readonly Func<byte[], T?> _deserializeItemRecord;
    private readonly Func<byte[], List<T>?> _deserializeBatchRecord;

    /// <summary>Create an auto-detecting source.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file reading is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file reading may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSource(string path)
        : this(path, new JsonFileSourceOptions())
    {
    }

    /// <summary>Create a source with explicit framing options.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file reading is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file reading may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSource(
        string path,
        JsonFileSourceOptions options)
        : this(path, options, serializerOptions: null, logger: null)
    {
    }

    /// <summary>Create a source with explicit framing options and logging.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file reading is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file reading may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSource(
        string path,
        JsonFileSourceOptions options,
        ILogger<JsonFileSource<T>> logger)
        : this(path, options, serializerOptions: null, logger)
    {
    }

    /// <summary>Create a source with explicit framing and serializer options.</summary>
    [RequiresUnreferencedCode("JsonSerializerOptions-based JSON reading may require reflection metadata.")]
    [RequiresDynamicCode("JsonSerializerOptions-based JSON reading may require runtime code generation.")]
    public JsonFileSource(
        string path,
        JsonFileSourceOptions options,
        JsonSerializerOptions? serializerOptions,
        ILogger<JsonFileSource<T>>? logger)
    {
        _path = ValidatePath(path);
        _openStream = () => OpenFile(_path);
        _options = JsonInputOptionsValidator.Validate(options, logger);
        _logger = logger;
        var frozenOptions = FreezeOptions(serializerOptions, _options.MaxDepth);
        _deserializeItems = (stream, topLevelValues, token) =>
            WrapJsonErrors(
                JsonSerializer.DeserializeAsyncEnumerable<T>(stream, topLevelValues, frozenOptions, token),
                _path,
                "document");
        _deserializeBatches = (stream, token) =>
            WrapJsonErrors(
                JsonSerializer.DeserializeAsyncEnumerable<List<T>>(stream, topLevelValues: true, frozenOptions, token),
                _path,
                "document");
        _deserializeItemRecord = bytes => JsonSerializer.Deserialize<T>(bytes, frozenOptions);
        _deserializeBatchRecord = bytes => JsonSerializer.Deserialize<List<T>>(bytes, frozenOptions);
    }

    /// <summary>Create an auto-detecting source using source-generated metadata.</summary>
    public JsonFileSource(
        string path,
        JsonTypeInfo<List<T>> listTypeInfo,
        JsonTypeInfo<T> itemTypeInfo)
        : this(path, itemTypeInfo, listTypeInfo, new JsonFileSourceOptions())
    {
    }

    /// <summary>Create a source using source-generated metadata and explicit framing.</summary>
    public JsonFileSource(
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> listTypeInfo,
        JsonFileSourceOptions options)
        : this(path, itemTypeInfo, listTypeInfo, options, logger: null)
    {
    }

    /// <summary>Create a source using source-generated metadata, explicit framing, and logging.</summary>
    public JsonFileSource(
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> listTypeInfo,
        JsonFileSourceOptions options,
        ILogger<JsonFileSource<T>>? logger)
    {
        _path = ValidatePath(path);
        _openStream = () => OpenFile(_path);
        ArgumentNullException.ThrowIfNull(itemTypeInfo);
        ArgumentNullException.ThrowIfNull(listTypeInfo);
        _options = JsonInputOptionsValidator.Validate(options, logger);
        _logger = logger;
        var frozenTypeInfo = FreezeSourceGeneratedOptions(itemTypeInfo, listTypeInfo, _options.MaxDepth);
        _deserializeItems = (stream, topLevelValues, token) =>
            WrapJsonErrors(
                JsonSerializer.DeserializeAsyncEnumerable(stream, frozenTypeInfo.Item, topLevelValues, token),
                _path,
                "document");
        _deserializeBatches = (stream, token) =>
            WrapJsonErrors(
                JsonSerializer.DeserializeAsyncEnumerable(stream, frozenTypeInfo.List, topLevelValues: true, token),
                _path,
                "document");
        _deserializeItemRecord = bytes => JsonSerializer.Deserialize(bytes, frozenTypeInfo.Item);
        _deserializeBatchRecord = bytes => JsonSerializer.Deserialize(bytes, frozenTypeInfo.List);
    }

    [RequiresUnreferencedCode("Reflection-based JSON file reading is not trimming-safe.")]
    [RequiresDynamicCode("Reflection-based JSON file reading may require runtime code generation.")]
    internal JsonFileSource(string path, Stream stream, JsonFileSourceOptions options)
        : this(path, options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _openStream = () => stream;
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = _openStream();

        var format = _options.Format;
        var autoDetected = format == JsonFileFormat.Auto;
        var autoDetectedArray = false;
        if (format == JsonFileFormat.Auto)
        {
            var firstByte = await ReadFirstJsonByteAsync(stream, ct).ConfigureAwait(false);
            if (firstByte == null)
                yield break;

            if (firstByte == (byte)'[')
            {
                if (IsAmbiguousCollectionType())
                    throw new JsonException("Auto format is ambiguous for collection-valued T. Specify Array, Ndjson, or BatchJsonLines explicitly.");
                format = JsonFileFormat.BatchJsonLines;
                autoDetectedArray = true;
            }
            else
            {
                format = JsonFileFormat.Ndjson;
            }
        }

        if (format == JsonFileFormat.BatchJsonLines)
        {
            if (!autoDetectedArray)
            {
                await foreach (var batch in ReadFramedRecordsAsync(_deserializeBatchRecord, stream, ct).ConfigureAwait(false))
                {
                    foreach (var item in batch)
                    {
                        if (item is null)
                        {
                            HandleInvalidNull(0);
                            continue;
                        }
                        yield return ProcessingEnvelope<T>.Create(item);
                    }
                }
                yield break;
            }

            using var limitedStream = new JsonUnframedInputLimitStream(
                stream,
                _options.MaxUnframedInputSizeBytes,
                _path);
            var recordIndex = 0L;
            await foreach (var batch in _deserializeBatches(limitedStream, ct).ConfigureAwait(false))
            {
                recordIndex++;
                if (batch == null)
                {
                    HandleInvalidNull(recordIndex);
                    continue;
                }

                foreach (var item in batch)
                {
                    if (item is null)
                    {
                        HandleInvalidNull(recordIndex);
                        continue;
                    }
                    yield return ProcessingEnvelope<T>.Create(item);
                }
            }
            yield break;
        }

        var topLevelValues = format == JsonFileFormat.Ndjson;
        if (format == JsonFileFormat.Ndjson && !autoDetected)
        {
            await foreach (var item in ReadFramedRecordsAsync(_deserializeItemRecord, stream, ct).ConfigureAwait(false))
            {
                if (item is null)
                {
                    HandleInvalidNull(0);
                    continue;
                }
                yield return ProcessingEnvelope<T>.Create(item);
            }
            yield break;
        }

        using var limitedItemStream = new JsonUnframedInputLimitStream(stream, _options.MaxUnframedInputSizeBytes, _path);
        Stream itemStream = limitedItemStream;
        var itemIndex = 0L;
        await foreach (var item in _deserializeItems(itemStream, topLevelValues, ct).ConfigureAwait(false))
        {
            itemIndex++;
            if (item is null)
            {
                HandleInvalidNull(itemIndex);
                continue;
            }
            yield return ProcessingEnvelope<T>.Create(item);
        }
    }

    private async IAsyncEnumerable<TRecord> ReadFramedRecordsAsync<TRecord>(
        Func<byte[], TRecord?> deserialize,
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var recordIndex = 0L;
        await foreach (var record in Utf8LineRecordReader.ReadAsync(
            stream,
            _options.MaxRecordSizeBytes,
            ct).ConfigureAwait(false))
        {
            recordIndex++;
            if (record.TooLarge)
            {
                var exception = new JsonException(
                    $"JSON record {recordIndex} in '{_path}' exceeds the {_options.MaxRecordSizeBytes}-byte limit.");
                if (!HandleInvalidRecord(recordIndex, exception))
                    throw exception;
                continue;
            }

            TRecord? value;
            try
            {
                JsonRecordValidator.Validate(record.Bytes, _options.MaxDepth, _path, recordIndex);
                value = deserialize(record.Bytes);
            }
            catch (JsonException ex)
            {
                var contextual = ex.Message.Contains(_path, StringComparison.Ordinal)
                    ? ex
                    : new JsonException($"Invalid JSON record {recordIndex} in '{_path}': {ex.Message}", ex);
                if (!HandleInvalidRecord(recordIndex, contextual))
                    throw contextual;
                continue;
            }

            if (value is null)
            {
                HandleInvalidNull(recordIndex);
                continue;
            }
            yield return value;
        }
    }

    private bool HandleInvalidRecord(long recordIndex, JsonException exception)
    {
        if (_options.InvalidRecordBehavior == InvalidJsonRecordBehavior.Throw)
            return false;
        _logger!.LogWarning(
            exception,
            "Skipping invalid JSON record {RecordIndex} in {Path}.",
            recordIndex,
            _path);
        return true;
    }

    private static FileStream OpenFile(string path) => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void HandleInvalidNull(long recordIndex)
    {
        if (recordIndex == 0)
            recordIndex = 1;
        if (_options.InvalidRecordBehavior == InvalidJsonRecordBehavior.Throw)
            throw new JsonException($"JSON record {recordIndex} in '{_path}' deserialized to null.");
        _logger!.LogWarning("Skipping null JSON record {RecordIndex} in {Path}.", recordIndex, _path);
    }

    private static async ValueTask<byte?> ReadFirstJsonByteAsync(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[3];
        var read = 0;
        while (read < prefix.Length)
        {
            var count = await stream.ReadAsync(prefix.AsMemory(read), ct).ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }
        stream.Position = 0;
        var offset = read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF ? 3L : 0L;
        stream.Position = offset;
        var buffer = new byte[1];
        while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) == 1)
        {
            if (buffer[0] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                stream.Position = offset;
                return buffer[0];
            }
        }
        stream.Position = offset;
        return null;
    }

    private static bool IsAmbiguousCollectionType()
    {
        var type = typeof(T);
        return type != typeof(string) && type != typeof(byte[]) && typeof(IEnumerable).IsAssignableFrom(type);
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        return path;
    }

    [RequiresUnreferencedCode("JsonSerializerOptions reflection metadata is not trimming-safe.")]
    [RequiresDynamicCode("JsonSerializerOptions reflection metadata may require runtime code generation.")]
    private static JsonSerializerOptions FreezeOptions(JsonSerializerOptions? options, int maxDepth)
    {
        var clone = options == null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        clone.MaxDepth = maxDepth;
        clone.MakeReadOnly(populateMissingResolver: true);
        return clone;
    }

    private static (JsonTypeInfo<T> Item, JsonTypeInfo<List<T>> List) FreezeSourceGeneratedOptions(
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> listTypeInfo,
        int maxDepth)
    {
        if (itemTypeInfo.Type != typeof(T) || listTypeInfo.Type != typeof(List<T>))
            throw new ArgumentException("JSON type metadata does not match the source item and batch types.");
        if (!ReferenceEquals(itemTypeInfo.Options, listTypeInfo.Options))
            throw new ArgumentException("Item and list JSON type metadata must come from the same serializer context.");
        if (itemTypeInfo.Options.TypeInfoResolver == null)
            throw new ArgumentException("Source-generated JSON metadata must provide a type-info resolver.");

        var clone = new JsonSerializerOptions(itemTypeInfo.Options)
        {
            MaxDepth = maxDepth,
            TypeInfoResolver = itemTypeInfo.Options.TypeInfoResolver,
        };
        if (clone.GetTypeInfo(typeof(T)) is not JsonTypeInfo<T> frozenItem
            || clone.GetTypeInfo(typeof(List<T>)) is not JsonTypeInfo<List<T>> frozenList)
            throw new ArgumentException("The JSON metadata resolver cannot resolve both item and batch types.");
        clone.MakeReadOnly();
        return (frozenItem, frozenList);
    }

    private static async IAsyncEnumerable<TValue?> WrapJsonErrors<TValue>(
        IAsyncEnumerable<TValue?> values,
        string path,
        string scope,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var enumerator = values.GetAsyncEnumerator(ct);
        while (true)
        {
            bool hasValue;
            try
            {
                hasValue = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                throw new JsonException($"Invalid JSON {scope} in '{path}': {exception.Message}", exception);
            }
            if (!hasValue)
                yield break;
            yield return enumerator.Current;
        }
    }
}
