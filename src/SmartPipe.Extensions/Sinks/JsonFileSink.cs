using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file with periodic flushing.
/// Uses streaming write to avoid unbounded memory growth.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class JsonFileSink<T> : IPipelineSink<T>
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly string _path;
    private readonly int _flushInterval;
    private readonly Func<List<T>, byte[]> _serializeBatch;
    private readonly List<T> _buffer = [];
    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private Stream? _stream;
    private bool _leaveOpen;
    private bool _disposed;

    /// <summary>Create JSON file sink for given path.</summary>
    /// <param name="path">Output file path.</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    [RequiresUnreferencedCode("JsonSerializerOptions-based JSON file writing may require reflection metadata. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("JsonSerializerOptions-based JSON file writing may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public JsonFileSink(string path)
        : this(path, flushInterval: 1000)
    {
    }

    /// <summary>Create JSON file sink for given path.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="flushInterval">Number of items to buffer before flushing to disk (default: 1000).</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when flush interval is less than one.</exception>
    [RequiresUnreferencedCode("JsonSerializerOptions-based JSON file writing may require reflection metadata. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("JsonSerializerOptions-based JSON file writing may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
#pragma warning disable RS0027 // Existing 1.x optional constructor preserved for source compatibility.
    public JsonFileSink(string path, int flushInterval = 1000)
    {
        _path = ValidatePath(path);
        _flushInterval = ValidateFlushInterval(flushInterval);
        var options = new JsonSerializerOptions { WriteIndented = false };
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, options);
    }
#pragma warning restore RS0027

    /// <summary>Create JSON file sink for given path using source-generated JSON metadata.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="batchTypeInfo">Source-generated type information for buffered batches.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    public JsonFileSink(string path, JsonTypeInfo<List<T>> batchTypeInfo)
        : this(path, batchTypeInfo, flushInterval: 1000)
    {
    }

    /// <summary>Create JSON file sink for given path using source-generated JSON metadata.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="batchTypeInfo">Source-generated type information for buffered batches.</param>
    /// <param name="flushInterval">Number of items to buffer before flushing to disk.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    /// <exception cref="ArgumentException">Thrown when path is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when flush interval is less than one.</exception>
    public JsonFileSink(string path, JsonTypeInfo<List<T>> batchTypeInfo, int flushInterval)
    {
        _path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(batchTypeInfo);
        _flushInterval = ValidateFlushInterval(flushInterval);
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, batchTypeInfo);
    }

    internal JsonFileSink(string path, Stream stream, int flushInterval = 1000)
    {
        _path = ValidatePath(path);
        ArgumentNullException.ThrowIfNull(stream);
        _flushInterval = ValidateFlushInterval(flushInterval);
        var options = new JsonSerializerOptions { WriteIndented = false };
        _serializeBatch = batch => JsonSerializer.SerializeToUtf8Bytes(batch, options);
        _stream = stream;
        _leaveOpen = true;
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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
        if (envelope.Payload != null)
        {
            var shouldFlush = false;
            lock (_bufferLock)
            {
                _buffer.Add(envelope.Payload);
                shouldFlush = _buffer.Count >= _flushInterval;
            }

            if (shouldFlush)
                await FlushBufferedAsync(force: false, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        await FlushBufferedAsync(force: true, CancellationToken.None).ConfigureAwait(false);

        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_stream != null && !_leaveOpen)
                await _stream.DisposeAsync().ConfigureAwait(false);

            _stream = null;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private async Task FlushBufferedAsync(bool force, CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<T> batch;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0 || (!force && _buffer.Count < _flushInterval))
                    return;

                batch = [.. _buffer];
            }

            var bytes = _serializeBatch(batch);
            var stream = EnsureStream();
            await WriteBatchAsync(stream, bytes, ct).ConfigureAwait(false);

            lock (_bufferLock)
            {
                _buffer.RemoveRange(0, Math.Min(batch.Count, _buffer.Count));
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private Stream EnsureStream()
    {
        if (_stream != null)
            return _stream;

        var fileStream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        fileStream.Seek(0, SeekOrigin.End);
        _stream = fileStream;
        _leaveOpen = false;
        return _stream;
    }

    private static async Task WriteBatchAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        long? checkpointLength = null;
        long? checkpointPosition = null;
        if (stream.CanSeek)
        {
            stream.Seek(0, SeekOrigin.End);
            checkpointLength = stream.Length;
            checkpointPosition = stream.Position;
        }

        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(NewLine, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (checkpointLength is not null && checkpointPosition is not null)
            {
                stream.SetLength(checkpointLength.Value);
                stream.Position = checkpointPosition.Value;
            }

            throw;
        }
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));

        return path;
    }

    private static int ValidateFlushInterval(int flushInterval)
    {
        if (flushInterval <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(flushInterval),
                flushInterval,
                "Flush interval must be greater than zero.");

        return flushInterval;
    }
}
