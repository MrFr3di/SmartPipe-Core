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
        _path = path;
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
        _path = path;
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _serialize = static result => JsonSerializer.Serialize(result);
        _lineWriter = lineWriter ?? throw new ArgumentNullException(nameof(lineWriter));
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        if (_lineWriter == null)
        {
            // Open file in create mode - overwrite existing file on initialization
            var fileStream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
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
    /// Write JSON line with IOException retry logic.
    /// Uses exponential backoff: 100ms, 200ms, 400ms.
    /// </summary>
    private async Task WriteWithRetryAsync(string json, CancellationToken ct)
    {
        var delays = new[] { 100, 200, 400 };
        IOException? lastException = null;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (_lineWriter == null)
                    return;

                await _lineWriter.WriteLineAsync(json, ct);
                return; // Success
            }
            catch (IOException ex)
            {
                lastException = ex;

                if (attempt < 2)
                {
                    _logger.LogWarning(
                        ex,
                        "IOException on attempt {Attempt}/3 writing to dead letter file {Path}. Retrying in {Delay}ms...",
                        attempt + 1,
                        _path,
                        delays[attempt]
                    );
                    await Task.Delay(delays[attempt], ct);
                }
            }
        }

        // Final failure after all retries
        if (lastException != null)
        {
            _logger.LogError(
                lastException,
                "Failed to write to dead letter file {Path} after 3 retries. Skipping item.",
                _path
            );
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
    ValueTask WriteLineAsync(string line, CancellationToken ct);
}

internal sealed class StreamDeadLetterLineWriter : IDeadLetterLineWriter
{
    private readonly StreamWriter _writer;

    public StreamDeadLetterLineWriter(Stream stream, bool leaveOpen)
    {
        _writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen);
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken ct)
    {
        await _writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
