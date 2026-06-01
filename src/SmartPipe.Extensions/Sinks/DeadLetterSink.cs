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
/// Sink that captures failed items for later analysis.
/// Saves only errors (IsSuccess = false) to a file in JSON format (one JSON object per line).
/// Uses StreamWriter for immediate writes with IOException retry logic.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public class DeadLetterSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly ILogger<DeadLetterSink<T>> _logger;
    private readonly Func<ProcessingResult<T>, string> _serialize;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Stream? _testStream;
    private StreamWriter? _writer;
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
    /// <param name="stream">Optional test stream. If provided, writer is created immediately.</param>
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
        _testStream = stream;
        _serialize = static result => JsonSerializer.Serialize(result);

        if (_testStream != null)
        {
            _writer = new StreamWriter(_testStream, Encoding.UTF8, 1024, leaveOpen: true);
        }
    }
#pragma warning restore RS0027

    /// <summary>Create dead letter sink with source-generated JSON metadata.</summary>
    /// <param name="path">Output JSON file path.</param>
    /// <param name="resultTypeInfo">Source-generated type information for legacy processing results.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSink(string path, JsonTypeInfo<ProcessingResult<T>> resultTypeInfo)
        : this(path, resultTypeInfo, logger: null, stream: null)
    {
    }

    /// <summary>Create dead letter sink with source-generated JSON metadata.</summary>
    /// <param name="path">Output JSON file path.</param>
    /// <param name="resultTypeInfo">Source-generated type information for legacy processing results.</param>
    /// <param name="logger">Logger instance via DI.</param>
    /// <param name="stream">Optional test stream. If provided, writer is created immediately.</param>
    /// <exception cref="ArgumentNullException">Thrown when path or type information is null.</exception>
    public DeadLetterSink(
        string path,
        JsonTypeInfo<ProcessingResult<T>> resultTypeInfo,
        ILogger<DeadLetterSink<T>>? logger,
        Stream? stream
    )
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        ArgumentNullException.ThrowIfNull(resultTypeInfo);
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _testStream = stream;
        _serialize = result => JsonSerializer.Serialize(result, resultTypeInfo);

        if (_testStream != null)
        {
            _writer = new StreamWriter(_testStream, Encoding.UTF8, 1024, leaveOpen: true);
        }
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        if (_writer == null)
        {
            // Open file in create mode - overwrite existing file on initialization
            var fileStream = new FileStream(
                _path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
            _writer = new StreamWriter(fileStream);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess)
            return;

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_writer == null)
                throw new InvalidOperationException(
                    "Sink not initialized. Call InitializeAsync first."
                );

            var json = _serialize(result);
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
                if (_writer == null)
                    return;

                // For testing: check if we should throw IOException
                if (
                    _testException != null
                    && attempt < _testException.Length
                    && _testException[attempt]
                )
                {
                    throw new IOException($"Simulated IOException (attempt {attempt + 1})");
                }

                await _writer.WriteLineAsync(json.AsMemory(), ct);
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

    /// <summary>
    /// For testing purposes: set which attempts should throw IOException.
    /// </summary>
    internal void SetTestExceptionForTesting(bool[] throwOnAttempt)
    {
        _testException = throwOnAttempt;
    }

    private bool[]? _testException;

    /// <summary>
    /// For testing purposes: set the writer directly.
    /// </summary>
    internal void SetWriterForTesting(StreamWriter writer)
    {
        _writer = writer;
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _semaphore.WaitAsync();
        try
        {
            if (_writer != null)
            {
                await _writer.DisposeAsync();
                _writer = null;
            }
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
