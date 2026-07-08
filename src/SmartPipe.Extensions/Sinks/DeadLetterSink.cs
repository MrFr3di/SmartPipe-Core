#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>
/// Sink that captures dead-letter envelopes for later analysis.
/// Saves each envelope to a file in JSON Lines format.
/// Uses StreamWriter for immediate writes with IOException retry logic.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class DeadLetterSink<T> : IPipelineSink<DeadLetterEnvelope<T>>
{
    /// <summary>Maximum write attempts, including the initial attempt.</summary>
    internal const int MaxAttempts = 4;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
    ];

    private readonly string _path;
    private readonly ILogger<DeadLetterSink<T>> _logger;
    private readonly Func<DeadLetterEnvelope<T>, string> _serialize;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IDeadLetterLineWriter? _lineWriter;
    private bool _disposed;

    /// <summary>Create dead letter sink with default file path.</summary>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink()
        : this(path: "dead_letter.json", logger: null, stream: null)
    {
    }

    /// <summary>Create dead letter sink with given file path.</summary>
    /// <param name="path">Output JSON file path.</param>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink(string path)
        : this(path, logger: null, stream: null)
    {
    }

    /// <summary>Create dead letter sink with given file path and logger.</summary>
    /// <param name="path">Output JSON file path.</param>
    /// <param name="logger">Logger instance via DI.</param>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink(string path, ILogger<DeadLetterSink<T>>? logger)
        : this(path, logger, stream: null)
    {
    }

    /// <summary>Create dead letter sink with given file path.</summary>
    /// <param name="path">Output JSON file path (default: "dead_letter.json").</param>
    /// <param name="logger">Logger instance via DI.</param>
    /// <param name="stream">Optional output stream. If provided, the sink writes to this stream instead of opening a file.</param>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use the JsonTypeInfo constructor for trimming and NativeAOT.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use the JsonTypeInfo constructor for NativeAOT.")]
#pragma warning disable RS0027 // Existing 1.x optional constructor preserved for source compatibility.
    public DeadLetterSink(
        string path = "dead_letter.json",
        ILogger<DeadLetterSink<T>>? logger = null,
        Stream? stream = null
    )
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _serialize = static result => JsonSerializer.Serialize(result);

        if (stream != null)
        {
            _lineWriter = new StreamDeadLetterLineWriter(stream, leaveOpen: true);
        }
    }
#pragma warning restore RS0027

    /// <summary>Create dead letter sink with source-generated JSON metadata.</summary>
    /// <param name="path">Output JSON file path.</param>
    /// <param name="resultTypeInfo">Source-generated type information for dead-letter envelopes.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSink(string path, JsonTypeInfo<DeadLetterEnvelope<T>> resultTypeInfo)
        : this(path, resultTypeInfo, logger: null, stream: null)
    {
    }

    /// <summary>Create dead letter sink with source-generated JSON metadata.</summary>
    /// <param name="path">Output JSON file path.</param>
    /// <param name="resultTypeInfo">Source-generated type information for dead-letter envelopes.</param>
    /// <param name="logger">Logger instance via DI.</param>
    /// <param name="stream">Optional output stream. If provided, the sink writes to this stream instead of opening a file.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSink(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> resultTypeInfo,
        ILogger<DeadLetterSink<T>>? logger,
        Stream? stream
    )
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        ArgumentNullException.ThrowIfNull(resultTypeInfo);
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _serialize = result => JsonSerializer.Serialize(result, resultTypeInfo);

        if (stream != null)
        {
            _lineWriter = new StreamDeadLetterLineWriter(stream, leaveOpen: true);
        }
    }

    internal DeadLetterSink(
        string path,
        ILogger<DeadLetterSink<T>>? logger,
        IDeadLetterLineWriter lineWriter
    )
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _serialize = static result => JsonSerializer.Serialize(result);
        _lineWriter = lineWriter ?? throw new ArgumentNullException(nameof(lineWriter));
    }

    /// <summary>Gets the write failure behavior. The default throws after retry attempts are exhausted.</summary>
    public DeadLetterWriteFailureMode FailureMode { get; init; } = DeadLetterWriteFailureMode.Throw;

    /// <summary>Gets a value indicating whether each successful write is flushed immediately.</summary>
    public bool FlushEachWrite { get; init; } = true;

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        if (_lineWriter == null)
        {
            var fileStream = new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            fileStream.Seek(0, SeekOrigin.End);
            _lineWriter = new StreamDeadLetterLineWriter(fileStream, leaveOpen: false);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<DeadLetterEnvelope<T>> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload is null)
            return;

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_lineWriter == null)
                throw new InvalidOperationException(
                    "Sink not initialized. Call InitializeAsync first."
                );

            var json = _serialize(envelope.Payload);
            await WriteWithRetryAsync(json, ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Write JSON line with IOException retry logic. Uses exponential backoff before
    /// attempts 2-4: 100ms, 200ms, 400ms.
    /// </summary>
    private async Task WriteWithRetryAsync(string json, CancellationToken ct)
    {
        IOException? lastException = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (_lineWriter == null)
                    return;

                await _lineWriter.WriteLineAsync(json, FlushEachWrite, ct);
                return; // Success
            }
            catch (IOException ex)
            {
                lastException = ex;

                if (attempt < MaxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "IOException on attempt {Attempt}/{MaxAttempts} writing to dead letter file {Path}. Retrying in {Delay}ms...",
                        attempt,
                        MaxAttempts,
                        _path,
                        RetryDelays[attempt - 1].TotalMilliseconds
                    );
                    await Task.Delay(RetryDelays[attempt - 1], ct);
                }
            }
        }

        // Final failure after all retries
        if (lastException != null)
        {
            _logger.LogError(
                lastException,
                "Failed to write to dead letter file {Path} after {MaxAttempts} attempts.",
                _path,
                MaxAttempts
            );

            if (FailureMode == DeadLetterWriteFailureMode.Throw)
                throw new DeadLetterWriteException(_path, MaxAttempts, lastException);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _semaphore.WaitAsync();
        try
        {
            if (_lineWriter != null)
            {
                await _lineWriter.DisposeAsync();
                _lineWriter = null;
            }
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}

internal interface IDeadLetterLineWriter : IAsyncDisposable
{
    ValueTask WriteLineAsync(string line, bool flushEachWrite, CancellationToken ct);
}

internal sealed class StreamDeadLetterLineWriter : IDeadLetterLineWriter
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();
    private readonly Stream _stream;

    public StreamDeadLetterLineWriter(Stream stream, bool leaveOpen)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        LeaveOpen = leaveOpen;
    }

    private bool LeaveOpen { get; }

    public async ValueTask WriteLineAsync(string line, bool flushEachWrite, CancellationToken ct)
    {
        var checkpoint = _stream.CanSeek ? _stream.Length : (long?)null;
        if (checkpoint is not null)
            _stream.Position = checkpoint.Value;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.WriteAsync(NewLine, ct).ConfigureAwait(false);

            if (flushEachWrite)
                await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (checkpoint is not null)
            {
                _stream.SetLength(checkpoint.Value);
                _stream.Position = checkpoint.Value;
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (LeaveOpen)
            return;

        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Behavior after dead-letter write attempts are exhausted.</summary>
public enum DeadLetterWriteFailureMode
{
    /// <summary>Throw <see cref="DeadLetterWriteException"/> after retry attempts are exhausted.</summary>
    Throw = 0,

    /// <summary>Log the exhausted write failure and drop the dead-letter record.</summary>
    LogAndDrop = 1,
}

/// <summary>Exception thrown when a dead-letter record cannot be written after retry attempts.</summary>
public sealed class DeadLetterWriteException : IOException
{
    /// <summary>Create a dead-letter write exception.</summary>
    /// <param name="path">Configured dead-letter path.</param>
    /// <param name="attempts">Number of write attempts made.</param>
    /// <param name="innerException">Last write exception.</param>
    public DeadLetterWriteException(string path, int attempts, Exception innerException)
        : base($"Failed to write dead-letter record to '{path}' after {attempts} attempts.", innerException)
    {
        Path = path;
        Attempts = attempts;
    }

    /// <summary>Configured dead-letter path.</summary>
    public string Path { get; }

    /// <summary>Number of write attempts made.</summary>
    public int Attempts { get; }
}
