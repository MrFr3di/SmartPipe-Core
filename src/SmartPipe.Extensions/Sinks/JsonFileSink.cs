using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a JSON file with periodic flushing.
/// Uses streaming write to avoid unbounded memory growth.</summary>
/// <typeparam name="T">Item type.</typeparam>
public class JsonFileSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly int _flushInterval;
    private readonly List<T> _buffer = [];
    private readonly Lock _bufferLock = new();
    private int _count;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    /// <summary>Create JSON file sink for given path.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="flushInterval">Number of items to buffer before flushing to disk (default: 1000).</param>
    /// <exception cref="ArgumentNullException">Thrown when path is null.</exception>
    public JsonFileSink(string path, int flushInterval = 1000)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _flushInterval = flushInterval;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
        {
            List<T>? batchToFlush = null;
            lock (_bufferLock)
            {
                _buffer.Add(result.Value);
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
    public async Task DisposeAsync()
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
        var json = JsonSerializer.Serialize(batch, _jsonOptions);
        await File.AppendAllTextAsync(_path, json + Environment.NewLine, ct).ConfigureAwait(false);
    }
}