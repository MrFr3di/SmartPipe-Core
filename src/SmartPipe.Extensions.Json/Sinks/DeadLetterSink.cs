#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes dead-letter envelopes through an <see cref="IDeadLetterSerializer{T}"/>.</summary>
/// <typeparam name="T">Original payload type.</typeparam>
public partial class DeadLetterSink<T> : IPipelineSink<DeadLetterEnvelope<T>>
{
    /// <summary>Default maximum attempts, including the initial write.</summary>
    internal const int MaxAttempts = 4;

    private enum SinkLifecycleState
    {
        Active,
        Disposing,
        Disposed,
        Faulted,
    }

    private readonly string _path;
    private readonly ILogger<DeadLetterSink<T>> _logger;
    private readonly IDeadLetterSerializer<T> _serializer;
    private readonly TimeSpan[] _retryDelays;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SharedAsyncDisposeState _dispose = new();
    private IDeadLetterLineWriter? _lineWriter;
    private int _state;

    /// <summary>Create a sink with the default path and reflection-based STJ serializer.</summary>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink()
        : this("dead_letter.json", logger: null, stream: null)
    {
    }

    /// <summary>Create a sink with a reflection-based STJ serializer.</summary>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink(string path)
        : this(path, logger: null, stream: null)
    {
    }

    /// <summary>Create a sink with a reflection-based STJ serializer and logger.</summary>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
    public DeadLetterSink(string path, ILogger<DeadLetterSink<T>>? logger)
        : this(path, logger, stream: null)
    {
    }

    /// <summary>Create a sink with a reflection-based STJ serializer.</summary>
    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe. Use a JsonTypeInfo constructor.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation. Use a JsonTypeInfo constructor for NativeAOT.")]
#pragma warning disable RS0027 // Existing optional constructor preserved for source compatibility.
    public DeadLetterSink(
        string path = "dead_letter.json",
        ILogger<DeadLetterSink<T>>? logger = null,
        Stream? stream = null)
        : this(
            path,
            new JsonLinesDeadLetterSerializer<T>(),
            new DeadLetterSinkOptions(),
            logger,
            stream)
    {
    }
#pragma warning restore RS0027

    /// <summary>Create a sink using source-generated STJ metadata.</summary>
    public DeadLetterSink(string path, JsonTypeInfo<DeadLetterEnvelope<T>> resultTypeInfo)
        : this(path, resultTypeInfo, logger: null, stream: null)
    {
    }

    /// <summary>Create a sink using source-generated STJ metadata.</summary>
    public DeadLetterSink(
        string path,
        JsonTypeInfo<DeadLetterEnvelope<T>> resultTypeInfo,
        ILogger<DeadLetterSink<T>>? logger,
        Stream? stream)
        : this(
            path,
            new JsonLinesDeadLetterSerializer<T>(resultTypeInfo),
            new DeadLetterSinkOptions(),
            logger,
            stream)
    {
    }

    /// <summary>Create a sink using an explicit serializer and retry options.</summary>
    public DeadLetterSink(
        string path,
        IDeadLetterSerializer<T> serializer,
        DeadLetterSinkOptions options,
        ILogger<DeadLetterSink<T>>? logger,
        Stream? stream)
    {
        _path = ValidatePath(path);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        var validated = ValidateOptions(options);
        FailureMode = validated.FailureMode;
        FlushEachWrite = validated.FlushEachWrite;
        _retryDelays = validated.RetryDelays.ToArray();
        _timeProvider = validated.TimeProvider;
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        if (stream != null)
            _lineWriter = new StreamDeadLetterLineWriter(stream, leaveOpen: true);
    }

    [RequiresUnreferencedCode("Reflection-based dead-letter JSON serialization is not trimming-safe.")]
    [RequiresDynamicCode("Reflection-based dead-letter JSON serialization may require runtime code generation.")]
    internal DeadLetterSink(
        string path,
        ILogger<DeadLetterSink<T>>? logger,
        IDeadLetterLineWriter lineWriter)
        : this(
            path,
            new JsonLinesDeadLetterSerializer<T>(),
            new DeadLetterSinkOptions(),
            logger,
            lineWriter)
    {
    }

    internal DeadLetterSink(
        string path,
        IDeadLetterSerializer<T> serializer,
        DeadLetterSinkOptions options,
        ILogger<DeadLetterSink<T>>? logger,
        IDeadLetterLineWriter lineWriter)
    {
        _path = ValidatePath(path);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        var validated = ValidateOptions(options);
        FailureMode = validated.FailureMode;
        FlushEachWrite = validated.FlushEachWrite;
        _retryDelays = validated.RetryDelays.ToArray();
        _timeProvider = validated.TimeProvider;
        _logger = logger ?? NullLogger<DeadLetterSink<T>>.Instance;
        _lineWriter = lineWriter ?? throw new ArgumentNullException(nameof(lineWriter));
    }

    /// <summary>Gets the behavior after write attempts are exhausted.</summary>
    public DeadLetterWriteFailureMode FailureMode { get; init; } = DeadLetterWriteFailureMode.Throw;

    /// <summary>Gets whether each successful record is flushed immediately.</summary>
    public bool FlushEachWrite { get; init; } = true;

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfNotActive();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            var writer = EnsureWriter();
            if (writer is StreamDeadLetterLineWriter streamWriter)
                streamWriter.EnsureAppendCapabilities();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        ProcessingEnvelope<DeadLetterEnvelope<T>> envelope,
        CancellationToken ct = default)
    {
        ThrowIfNotActive();
        if (envelope.Payload is null)
            return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            var writer = EnsureWriter();
            var record = await SerializeOnceAsync(envelope.Payload, ct).ConfigureAwait(false);
            await WriteWithRetryAsync(writer, record, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
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
            await _gate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            var writer = _lineWriter;
            _lineWriter = null;
            if (writer != null)
                await writer.DisposeAsync().ConfigureAwait(false);
            succeeded = true;
        }
        finally
        {
            Volatile.Write(
                ref _state,
                (int)(succeeded ? SinkLifecycleState.Disposed : SinkLifecycleState.Faulted));
            if (acquired)
                _gate.Release();
        }
    }

    private async ValueTask<byte[]> SerializeOnceAsync(
        DeadLetterEnvelope<T> envelope,
        CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await _serializer.WriteAsync(envelope, buffer, ct).ConfigureAwait(false);
        var record = buffer.ToArray();
        if (record.Length == 0 || record[^1] != (byte)'\n')
            return [.. record, (byte)'\n'];
        return record;
    }

    private async Task WriteWithRetryAsync(
        IDeadLetterLineWriter writer,
        byte[] record,
        CancellationToken ct)
    {
        Exception? lastException = null;
        var mayContainPartialRecord = false;
        var attempts = _retryDelays.Length + 1;
        var attemptsMade = 0;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            attemptsMade = attempt;
            try
            {
                await writer.WriteRecordAsync(record, FlushEachWrite, ct).ConfigureAwait(false);
                return;
            }
            catch (DeadLetterRecordWriteException ex)
            {
                lastException = ex.OriginalException;
                mayContainPartialRecord = ex.MayContainPartialRecord;
            }
            catch (IOException ex)
            {
                lastException = ex;
                mayContainPartialRecord = false;
            }

            if (mayContainPartialRecord || lastException is not IOException || attempt >= attempts)
                break;

            var delay = _retryDelays[attempt - 1];
            LogRetry(_logger, lastException!, attempt, attempts, _path, delay.TotalMilliseconds);
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }

        if (lastException == null)
            return;

        LogFailure(_logger, lastException, _path, attemptsMade);
        if (FailureMode == DeadLetterWriteFailureMode.Throw)
            throw new DeadLetterWriteException(
                _path,
                attemptsMade,
                lastException,
                mayContainPartialRecord);
    }

    private IDeadLetterLineWriter EnsureWriter()
    {
        if (_lineWriter != null)
            return _lineWriter;

        var stream = new FileStream(
            _path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(0, SeekOrigin.End);
        _lineWriter = new StreamDeadLetterLineWriter(stream, leaveOpen: false);
        return _lineWriter;
    }

    private void ThrowIfNotActive()
    {
        if (Volatile.Read(ref _state) != (int)SinkLifecycleState.Active)
            throw new ObjectDisposedException(nameof(DeadLetterSink<T>));
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));
        return path;
    }

    private static DeadLetterSinkOptions ValidateOptions(DeadLetterSinkOptions? options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.RetryDelays);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        if (options.RetryDelays.Any(static delay => delay < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delays cannot be negative.");
        return options with { RetryDelays = options.RetryDelays.ToArray() };
    }

    [LoggerMessage(1, LogLevel.Warning, "IOException on attempt {Attempt}/{MaxAttempts} writing to dead letter file {Path}. Retrying in {DelayMilliseconds}ms.")]
    private static partial void LogRetry(
        ILogger logger,
        Exception exception,
        int attempt,
        int maxAttempts,
        string path,
        double delayMilliseconds);

    [LoggerMessage(2, LogLevel.Error, "Failed to write to dead letter file {Path} after {Attempts} attempts.")]
    private static partial void LogFailure(
        ILogger logger,
        Exception exception,
        string path,
        int attempts);
}

internal interface IDeadLetterLineWriter : IAsyncDisposable
{
    ValueTask WriteRecordAsync(ReadOnlyMemory<byte> record, bool flushEachWrite, CancellationToken ct);
}

internal sealed class StreamDeadLetterLineWriter : IDeadLetterLineWriter
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _appendBoundaryChecked;

    public StreamDeadLetterLineWriter(Stream stream, bool leaveOpen)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public async ValueTask WriteRecordAsync(
        ReadOnlyMemory<byte> record,
        bool flushEachWrite,
        CancellationToken ct)
    {
        EnsureAppendCapabilities();
        var prefixLineFeed = !_appendBoundaryChecked
            && await AppendFraming.RequiresLineSeparatorAsync(_stream, ct).ConfigureAwait(false);
        var checkpoint = _stream.Length;
        _stream.Position = checkpoint;
        try
        {
            if (prefixLineFeed)
                await _stream.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
            await _stream.WriteAsync(record, ct).ConfigureAwait(false);
            if (flushEachWrite)
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            _appendBoundaryChecked = true;
        }
        catch (Exception original)
        {
            try
            {
                _stream.SetLength(checkpoint);
                _stream.Position = checkpoint;
            }
            catch (Exception rollback)
            {
                throw new DeadLetterRecordWriteException(
                    new AggregateException("Dead-letter append failed and rollback also failed.", original, rollback),
                    mayContainPartialRecord: true);
            }

            if (original is OperationCanceledException)
                throw;
            throw new DeadLetterRecordWriteException(original, mayContainPartialRecord: false);
        }
    }

    internal void EnsureAppendCapabilities() => AppendFraming.EnsureReadableAndSeekable(_stream);

    public async ValueTask DisposeAsync()
    {
        Exception? flushFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            flushFailure = exception;
        }

        if (!_leaveOpen)
        {
            try
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        SharedAsyncDisposeState.ThrowIfFailed(flushFailure, cleanupFailure);
    }
}

internal sealed class DeadLetterRecordWriteException : IOException
{
    public DeadLetterRecordWriteException(Exception originalException, bool mayContainPartialRecord)
        : base(originalException.Message, originalException)
    {
        OriginalException = originalException;
        MayContainPartialRecord = mayContainPartialRecord;
    }

    public Exception OriginalException { get; }
    public bool MayContainPartialRecord { get; }
}

/// <summary>Behavior after dead-letter write attempts are exhausted.</summary>
public enum DeadLetterWriteFailureMode
{
    /// <summary>Throw after attempts are exhausted.</summary>
    Throw = 0,
    /// <summary>Log and drop the dead-letter record.</summary>
    LogAndDrop = 1,
}

/// <summary>Exception thrown when a dead-letter record cannot be written.</summary>
public sealed class DeadLetterWriteException : IOException
{
    /// <summary>Create an exception for a safely failed write.</summary>
    public DeadLetterWriteException(string path, int attempts, Exception innerException)
        : this(path, attempts, innerException, mayContainPartialRecord: false)
    {
    }

    /// <summary>Create an exception and describe whether the destination may contain a partial record.</summary>
    public DeadLetterWriteException(
        string path,
        int attempts,
        Exception innerException,
        bool mayContainPartialRecord)
        : base($"Failed to write dead-letter record to '{path}' after {attempts} attempts.", innerException)
    {
        Path = path;
        Attempts = attempts;
        MayContainPartialRecord = mayContainPartialRecord;
    }

    /// <summary>Configured destination path.</summary>
    public string Path { get; }
    /// <summary>Number of attempts made.</summary>
    public int Attempts { get; }
    /// <summary>Whether a non-seekable destination may contain a partial record.</summary>
    public bool MayContainPartialRecord { get; }
}
