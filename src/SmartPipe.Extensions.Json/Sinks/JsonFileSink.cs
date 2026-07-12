using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file using an explicit streaming layout.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class JsonFileSink<T> : IPipelineSink<T>
{
    private enum SinkLifecycleState
    {
        Active,
        Disposing,
        Disposed,
        Faulted,
    }

    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly string _path;
    private readonly JsonFileSinkOptions _options;
    private readonly Func<List<T>, byte[]> _serializeBatch;
    private readonly Func<T, byte[]>? _serializeItem;
    private readonly List<T> _buffer = [];
    private readonly SharedAsyncDisposeState _dispose = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Stream? _stream;
    private bool _leaveOpen;
    private bool _arrayStarted;
    private bool _arrayHasItems;
    private bool _appendBoundaryChecked;
    private int _state;

    /// <summary>Create a legacy batch-JSON-lines sink.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file writing is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file writing may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSink(string path)
        : this(path, new JsonFileSinkOptions())
    {
    }

    /// <summary>Create a legacy batch-JSON-lines sink.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file writing is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file writing may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
#pragma warning disable RS0027 // Existing optional constructor preserved for source compatibility.
    public JsonFileSink(string path, int flushInterval = 1000)
        : this(path, new JsonFileSinkOptions { FlushInterval = flushInterval })
    {
    }
#pragma warning restore RS0027

    /// <summary>Create a sink with an explicit output layout.</summary>
    [RequiresUnreferencedCode("Reflection-based JSON file writing is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based JSON file writing may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSink(string path, JsonFileSinkOptions options)
        : this(path, options, serializerOptions: null)
    {
    }

    /// <summary>Create a sink with explicit file and serializer options.</summary>
    [RequiresUnreferencedCode("JsonSerializerOptions-based JSON file writing may require reflection metadata.")]
    [RequiresDynamicCode("JsonSerializerOptions-based JSON file writing may require runtime code generation.")]
    public JsonFileSink(
        string path,
        JsonFileSinkOptions options,
        JsonSerializerOptions? serializerOptions)
    {
        _path = ValidatePath(path);
        _options = ValidateOptions(options);
        var frozenOptions = FreezeOptions(serializerOptions);
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, frozenOptions);
        _serializeItem = item => JsonSerializer.SerializeToUtf8Bytes(item, frozenOptions);
    }

    /// <summary>Create a legacy batch sink using source-generated metadata.</summary>
    public JsonFileSink(string path, JsonTypeInfo<List<T>> batchTypeInfo)
        : this(path, batchTypeInfo, flushInterval: 1000)
    {
    }

    /// <summary>Create a legacy batch sink using source-generated metadata.</summary>
    public JsonFileSink(string path, JsonTypeInfo<List<T>> batchTypeInfo, int flushInterval)
    {
        _path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(batchTypeInfo);
        _options = ValidateOptions(new JsonFileSinkOptions { FlushInterval = flushInterval });
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, batchTypeInfo);
    }

    /// <summary>Create a sink using source-generated item and batch metadata.</summary>
    public JsonFileSink(
        string path,
        JsonTypeInfo<T> itemTypeInfo,
        JsonTypeInfo<List<T>> batchTypeInfo,
        JsonFileSinkOptions options)
    {
        _path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(itemTypeInfo);
        ArgumentNullException.ThrowIfNull(batchTypeInfo);
        _options = ValidateOptions(options);
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, batchTypeInfo);
        _serializeItem = item => JsonSerializer.SerializeToUtf8Bytes(item, itemTypeInfo);
    }

    [RequiresUnreferencedCode("Reflection-based JSON file writing is not trimming-safe.")]
    [RequiresDynamicCode("Reflection-based JSON file writing may require runtime code generation.")]
    internal JsonFileSink(string path, Stream stream, int flushInterval = 1000)
        : this(path, stream, flushInterval, leaveOpen: true)
    {
    }

    [RequiresUnreferencedCode("Reflection-based JSON file writing is not trimming-safe.")]
    [RequiresDynamicCode("Reflection-based JSON file writing may require runtime code generation.")]
    internal JsonFileSink(string path, Stream stream, int flushInterval, bool leaveOpen)
    {
        _path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(stream);
        _options = ValidateOptions(new JsonFileSinkOptions { FlushInterval = flushInterval });
        var frozenOptions = FreezeOptions(null);
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, frozenOptions);
        _serializeItem = item => JsonSerializer.SerializeToUtf8Bytes(item, frozenOptions);
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfNotActive();
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            EnsureStream();
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        ThrowIfNotActive();
        if (envelope.Payload is null)
            return;

        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            _buffer.Add(envelope.Payload);
            if (_buffer.Count >= _options.FlushInterval)
                await FlushBufferedCoreAsync(force: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(
            ref _state,
            (int)SinkLifecycleState.Disposing,
            (int)SinkLifecycleState.Active);
        return new ValueTask(_dispose.GetOrStart(DisposeCoreAsync));
    }

    private async Task DisposeCoreAsync()
    {
        var acquired = false;
        var succeeded = false;
        try
        {
            await _flushGate.WaitAsync().ConfigureAwait(false);
            acquired = true;

            await FlushBufferedCoreAsync(force: true, CancellationToken.None).ConfigureAwait(false);
            if (_options.Format == JsonFileFormat.Array)
                await CompleteArrayAsync(CancellationToken.None).ConfigureAwait(false);

            if (_stream != null)
            {
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                if (!_leaveOpen)
                    await _stream.DisposeAsync().ConfigureAwait(false);
            }

            _stream = null;
            succeeded = true;
        }
        finally
        {
            Volatile.Write(
                ref _state,
                (int)(succeeded ? SinkLifecycleState.Disposed : SinkLifecycleState.Faulted));
            if (acquired)
                _flushGate.Release();
        }
    }

    private async Task FlushBufferedCoreAsync(bool force, CancellationToken ct)
    {
        if (_buffer.Count == 0 || (!force && _buffer.Count < _options.FlushInterval))
            return;

        var batch = _buffer.ToList();
        var bytes = BuildRecord(batch);
        var stream = EnsureStream();
        var prefixLineFeed = await NeedsLeadingLineFeedAsync(stream, ct).ConfigureAwait(false);
        await WriteTransactionalAsync(stream, bytes, prefixLineFeed, ct).ConfigureAwait(false);
        _appendBoundaryChecked = true;
        if (_options.Format == JsonFileFormat.Array)
        {
            _arrayStarted = true;
            _arrayHasItems = true;
        }

        _buffer.RemoveRange(0, batch.Count);
    }

    private byte[] BuildRecord(List<T> batch)
    {
        if (_options.Format == JsonFileFormat.BatchJsonLines)
            return Combine([_serializeBatch(batch), NewLine]);

        if (_serializeItem == null)
            throw new InvalidOperationException("The selected format requires JsonTypeInfo<T> item metadata.");

        using var output = new MemoryStream();
        if (_options.Format == JsonFileFormat.Array && !_arrayStarted)
            output.WriteByte((byte)'[');

        foreach (var item in batch)
        {
            if (_options.Format == JsonFileFormat.Array)
            {
                if (_arrayHasItems || output.Length > 1)
                    output.WriteByte((byte)',');
            }

            var itemBytes = _serializeItem(item);
            output.Write(itemBytes);
            if (_options.Format == JsonFileFormat.Ndjson)
                output.Write(NewLine);
        }

        return output.ToArray();
    }

    private async Task CompleteArrayAsync(CancellationToken ct)
    {
        var stream = EnsureStream();
        var closing = _arrayStarted ? "]"u8.ToArray() : "[]"u8.ToArray();
        await WriteTransactionalAsync(stream, closing, prefixLineFeed: false, ct).ConfigureAwait(false);
        _arrayStarted = true;
    }

    private Stream EnsureStream()
    {
        if (_stream != null)
        {
            if (_options.OpenMode == JsonFileOpenMode.Append && !_appendBoundaryChecked)
                AppendFraming.EnsureReadableAndSeekable(_stream);
            return _stream;
        }

        var mode = _options.OpenMode == JsonFileOpenMode.Create ? FileMode.Create : FileMode.OpenOrCreate;
        var stream = new FileStream(
            _path,
            mode,
            _options.OpenMode == JsonFileOpenMode.Append ? FileAccess.ReadWrite : FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (_options.OpenMode == JsonFileOpenMode.Append)
            stream.Seek(0, SeekOrigin.End);
        _stream = stream;
        return stream;
    }

    private async ValueTask<bool> NeedsLeadingLineFeedAsync(Stream stream, CancellationToken ct)
    {
        if (_options.OpenMode != JsonFileOpenMode.Append || _appendBoundaryChecked)
            return false;

        return await AppendFraming.RequiresLineSeparatorAsync(stream, ct).ConfigureAwait(false);
    }

    private void ThrowIfNotActive()
    {
        if (Volatile.Read(ref _state) != (int)SinkLifecycleState.Active)
            throw new ObjectDisposedException(nameof(JsonFileSink<T>));
    }

    private static async Task WriteTransactionalAsync(Stream stream, byte[] bytes, bool prefixLineFeed, CancellationToken ct)
    {
        long? checkpoint = stream.CanSeek ? stream.Length : null;
        if (checkpoint.HasValue)
            stream.Position = checkpoint.Value;

        try
        {
            if (prefixLineFeed)
                await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            if (checkpoint.HasValue)
            {
                try
                {
                    stream.SetLength(checkpoint.Value);
                    stream.Position = checkpoint.Value;
                }
                catch (Exception rollback)
                {
                    throw new AggregateException("Append write failed and rollback also failed; the destination may contain a partial record.", original, rollback);
                }
            }
            throw;
        }
    }

    private static JsonFileSinkOptions ValidateOptions(JsonFileSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Format == JsonFileFormat.Auto)
            throw new ArgumentException("Auto format is valid only for JSON sources.", nameof(options));
        if (options.Format == JsonFileFormat.Array && options.OpenMode == JsonFileOpenMode.Append)
            throw new ArgumentException("A root JSON array cannot be appended safely.", nameof(options));
        if (options.FlushInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.FlushInterval, "Flush interval must be greater than zero.");
        return options with { };
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
    private static JsonSerializerOptions FreezeOptions(JsonSerializerOptions? options)
    {
        var clone = options == null
            ? new JsonSerializerOptions()
            : new JsonSerializerOptions(options);
        clone.MakeReadOnly(populateMissingResolver: true);
        return clone;
    }

    private static byte[] Combine(IReadOnlyList<byte[]> parts)
    {
        var length = parts.Sum(static part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
