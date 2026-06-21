using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file with periodic flushing.
/// Uses streaming write to avoid unbounded memory growth.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class JsonFileSink<T> : IPipelineSink<T>
{
    private readonly string _path;
    private readonly int _flushInterval;
    private readonly Func<List<T>, string> _serializeBatch;
    private readonly List<T> _buffer = [];
    private readonly Lock _bufferLock = new();
    private int _count;

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
        _serializeBatch = batch => JsonSerializer.Serialize(batch, options);
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
        _serializeBatch = batch => JsonSerializer.Serialize(batch, batchTypeInfo);
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload != null)
        {
            List<T>? batchToFlush = null;
            lock (_bufferLock)
            {
                _buffer.Add(envelope.Payload);
                if (++_count >= _flushInterval)
                {
                    batchToFlush = [.. _buffer];
                    _buffer.Clear();
                    _count = 0;
                }
            }
            if (batchToFlush != null)
                await FlushBatchAsync(batchToFlush, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<T> remaining;
        lock (_bufferLock)
        {
            remaining = [.. _buffer];
            _buffer.Clear();
        }
        if (remaining.Count > 0)
            await FlushBatchAsync(remaining, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushBatchAsync(List<T> batch, CancellationToken ct)
    {
        var json = _serializeBatch(batch);
        await File.AppendAllTextAsync(_path, json + Environment.NewLine, ct).ConfigureAwait(false);
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
