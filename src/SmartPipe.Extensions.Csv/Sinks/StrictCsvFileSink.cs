using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv;

internal sealed class StrictCsvFileSink<T> : IPipelineSink<T>
{
    private const int Active = 0;
    private const int Disposing = 1;
    private const int Disposed = 2;
    private const int Faulted = 3;

    private readonly string _path;
    private readonly CsvSinkOptionsSnapshot _options;
    private readonly CsvMapRegistration<T> _map;
    private readonly CancellationToken _activationCancellationToken;
    private readonly Func<FileMode, Stream>? _streamFactory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _disposeSync = new();

    private Stream? _stream;
    private CsvBoundedRecordTextWriter? _recordWriter;
    private CsvWriter? _csv;
    private bool _initialized;
    private bool _needsSeparator;
    private int _recordsSinceFlush;
    private int _state;
    private Task? _disposeTask;

    public StrictCsvFileSink(
        string path,
        CsvSinkOptionsSnapshot options,
        CsvMapRegistration<T> map,
        CancellationToken activationCancellationToken,
        Func<FileMode, Stream>? streamFactory = null)
    {
        _path = path;
        _options = options;
        _map = map;
        _activationCancellationToken = activationCancellationToken;
        _streamFactory = streamFactory;
    }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfNotActive();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            if (_initialized)
                return;

            using var linked = CreateLinkedCancellation(ct, out var cancellationToken);
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = await DisposeResourcesAsync().ConfigureAwait(false);
            if (cleanupFailures.Count != 0)
                throw new AggregateException(
                    "CSV operation failed and cleanup also failed.",
                    new[] { primaryFailure }.Concat(cleanupFailures));

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        ThrowIfNotActive();
        if (envelope.Payload is null)
            return;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfNotActive();
            if (!_initialized || _stream is null || _recordWriter is null || _csv is null)
                throw new InvalidOperationException("Sink is not initialized. Call InitializeAsync before writing.");

            using var linked = CreateLinkedCancellation(ct, out var cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _recordWriter.Reset();
            try
            {
                _csv.WriteRecord(envelope.Payload);
                await _csv.NextRecordAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var text = _recordWriter.ToStringAndReset();
                var bytes = EncodeRecord(text);
                try
                {
                    var separator = _needsSeparator ? EncodeText(_options.NewLine) : Array.Empty<byte>();
                    var shouldFlush = _recordsSinceFlush + 1 >= _options.FlushEveryRecords;
                    await CommitRecordAsync(separator, bytes, shouldFlush, cancellationToken).ConfigureAwait(false);
                    _needsSeparator = false;
                    _recordsSinceFlush = shouldFlush ? 0 : _recordsSinceFlush + 1;
                }
                finally
                {
                    ReturnBuffer(bytes);
                }
            }
            finally
            {
                _recordWriter.Reset();
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        Task task;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                Interlocked.CompareExchange(ref _state, Disposing, Active);
                _disposeTask = DisposeCoreAsync();
            }

            task = _disposeTask;
        }

        return new ValueTask(task);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        var mode = _options.OpenMode == CsvFileOpenMode.Create
            ? FileMode.Create
            : FileMode.OpenOrCreate;
        var stream = _streamFactory?.Invoke(mode) ?? new FileStream(
            _path,
            mode,
            FileAccess.ReadWrite,
            FileShare.Read,
            _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("CSV sink requires a readable, writable, seekable stream.");
        }
        _stream = stream;

        var probe = await CsvStrictEncoding.ProbeAsync(
            stream,
            _options.Encoding,
            detectEncodingFromByteOrderMarks: true,
            cancellationToken).ConfigureAwait(false);
        if (_options.OpenMode == CsvFileOpenMode.Append
            && stream.Length > probe.ContentOffset
            && probe.ContentOffset != 0
            && probe.Encoding.CodePage != _options.Encoding.CodePage)
        {
            throw new InvalidDataException("CSV append destination encoding does not match the configured encoding.");
        }

        _recordWriter = new CsvBoundedRecordTextWriter(
            _options.MaxRecordSizeCharacters,
            _options.Encoding,
            _options.NewLine);
        _csv = new CsvWriter(_recordWriter, CreateConfiguration(), leaveOpen: true);
        _map.Register(_csv.Context);

        var contentLength = stream.Length - probe.ContentOffset;
        if (_options.OpenMode == CsvFileOpenMode.Append && contentLength > 0)
        {
            if (_options.HasHeaderRecord && _options.ValidateExistingHeaderOnAppend)
            {
                var expectedHeader = await BuildHeaderAsync(cancellationToken).ConfigureAwait(false);
                await ValidateExistingHeaderAsync(probe, expectedHeader, cancellationToken).ConfigureAwait(false);
            }

            _needsSeparator = !await EndsWithRecordBoundaryAsync(stream, _options.Encoding, cancellationToken).ConfigureAwait(false);
            stream.Position = stream.Length;
            return;
        }

        stream.Position = probe.ContentOffset;
        var preamble = _options.EmitByteOrderMark && probe.ContentOffset == 0
            ? CsvStrictEncoding.GetPreamble(_options.Encoding)
            : Array.Empty<byte>();
        if (_options.HasHeaderRecord && _options.WriteHeaderWhenFileIsEmpty)
        {
            var header = await BuildHeaderAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await CommitRecordAsync(preamble, header, shouldFlush: true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ReturnBuffer(header);
            }
        }
        else if (preamble.Length != 0)
        {
            await CommitRecordAsync(Array.Empty<byte>(), preamble, shouldFlush: true, cancellationToken).ConfigureAwait(false);
        }

        stream.Position = stream.Length;
    }

    private async ValueTask<byte[]> BuildHeaderAsync(CancellationToken cancellationToken)
    {
        var writer = _recordWriter!;
        var csv = _csv!;
        writer.Reset();
        csv.WriteHeader<T>();
        await csv.NextRecordAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var text = writer.ToStringAndReset();
        return EncodeRecord(text);
    }

    private async ValueTask ValidateExistingHeaderAsync(
        CsvStrictEncoding.ProbeResult probe,
        byte[] expectedHeaderBytes,
        CancellationToken cancellationToken)
    {
        var expectedHeader = ParseHeader(expectedHeaderBytes);
        ReturnBuffer(expectedHeaderBytes);

        await using var existing = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var existingProbe = await CsvStrictEncoding.ProbeAsync(
            existing,
            _options.Encoding,
            detectEncodingFromByteOrderMarks: true,
            cancellationToken).ConfigureAwait(false);
        if (existingProbe.ContentOffset != probe.ContentOffset
            || existingProbe.Encoding.CodePage != probe.Encoding.CodePage)
        {
            throw new InvalidDataException("CSV append destination encoding does not match the configured encoding.");
        }

        existing.Position = existingProbe.ContentOffset;
        using var reader = new StreamReader(
            existing,
            existingProbe.Encoding,
            detectEncodingFromByteOrderMarks: false,
            _options.BufferSize,
            leaveOpen: true);
        var framer = new CsvLogicalRecordFramer(
            reader,
            _options.Delimiter,
            _options.Quote,
            _options.MaxRecordSizeCharacters,
            _options.MaxRecordSizeCharacters,
            Math.Max(1, expectedHeader.Length + 1),
            cancellationToken);
        CsvLogicalRecord? record;
        try
        {
            record = await framer.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or CsvHelperException)
        {
            throw new InvalidDataException("CSV append header preflight failed.", exception);
        }

        if (!record.HasValue)
            throw new InvalidDataException("CSV append destination has no header record.");

        try
        {
            using var headerReader = new StringReader(record.Value.Text);
            using var csv = new CsvReader(headerReader, CreateConfiguration(hasHeaderRecord: false), leaveOpen: false);
            if (!await csv.ReadAsync().ConfigureAwait(false))
                throw new InvalidDataException("CSV append destination has no header record.");

            var actualHeader = csv.Parser.Record;
            if (actualHeader is null || !actualHeader.SequenceEqual(expectedHeader, StringComparer.Ordinal))
                throw new InvalidDataException("CSV append destination header does not match the configured map.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (CsvHelperException exception)
        {
            throw new InvalidDataException("CSV append header preflight failed.", exception);
        }
    }

    private string[] ParseHeader(byte[] headerBytes)
    {
        try
        {
            var headerText = _options.Encoding.GetString(headerBytes);
            using var reader = new StringReader(headerText);
            using var csv = new CsvReader(reader, CreateConfiguration(hasHeaderRecord: false), leaveOpen: false);
            if (!csv.Read())
                throw new InvalidDataException("CSV header generation produced no fields.");
            return csv.Parser.Record?.ToArray()
                ?? throw new InvalidDataException("CSV header generation produced no fields.");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("CSV header encoding failed.", exception);
        }
        catch (CsvHelperException exception)
        {
            throw new InvalidDataException("CSV header generation failed.", exception);
        }
    }

    private CsvConfiguration CreateConfiguration(bool? hasHeaderRecord = null)
    {
        return new CsvConfiguration(_options.Culture)
        {
            Mode = CsvMode.RFC4180,
            Delimiter = _options.Delimiter.ToString(),
            Quote = _options.Quote,
            Escape = _options.Quote,
            DetectDelimiter = false,
            AllowComments = false,
            TrimOptions = TrimOptions.None,
            HasHeaderRecord = hasHeaderRecord ?? _options.HasHeaderRecord,
            NewLine = _options.NewLine,
            BufferSize = _options.BufferSize,
            ExceptionMessagesContainRawData = false,
            InjectionOptions = _options.FormulaInjectionMode switch
            {
                CsvFormulaInjectionMode.None => InjectionOptions.None,
                CsvFormulaInjectionMode.Escape => InjectionOptions.Escape,
                CsvFormulaInjectionMode.Strip => InjectionOptions.Strip,
                CsvFormulaInjectionMode.Throw => InjectionOptions.Exception,
                _ => throw new ArgumentOutOfRangeException(),
            },
            BadDataFound = _ => throw new InvalidDataException("CSV output contains invalid data."),
        };
    }

    private async ValueTask CommitRecordAsync(
        ReadOnlyMemory<byte> prefix,
        ReadOnlyMemory<byte> payload,
        bool shouldFlush,
        CancellationToken cancellationToken)
    {
        var stream = _stream!;
        var checkpointLength = stream.Length;
        var checkpointPosition = stream.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prefix.IsEmpty)
                await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            if (!payload.IsEmpty)
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldFlush)
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception primary)
        {
            try
            {
                stream.SetLength(checkpointLength);
                stream.Position = checkpointPosition;
            }
            catch (Exception rollback)
            {
                throw new AggregateException(
                    "CSV record write failed and rollback also failed; the destination may contain a partial record.",
                    primary,
                    rollback);
            }

            ExceptionDispatchInfo.Capture(primary).Throw();
            throw;
        }
    }

    private async ValueTask<bool> EndsWithRecordBoundaryAsync(
        Stream stream,
        Encoding encoding,
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            EncodeText(_options.NewLine),
            EncodeText("\r\n"),
            EncodeText("\n"),
            EncodeText("\r"),
        };
        var position = stream.Position;
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Length == 0 || candidate.Length > stream.Length)
                    continue;
                stream.Position = stream.Length - candidate.Length;
                var tail = new byte[candidate.Length];
                await stream.ReadExactlyAsync(tail.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (tail.AsSpan().SequenceEqual(candidate))
                    return true;
            }

            return false;
        }
        finally
        {
            stream.Position = position;
        }
    }

    private byte[] EncodeRecord(string text) => EncodeText(text);

    private byte[] EncodeText(string text)
    {
        try
        {
            var byteCount = _options.Encoding.GetByteCount(text);
            var bytes = new byte[byteCount];
            _ = _options.Encoding.GetBytes(text.AsSpan(), bytes.AsSpan());
            return bytes;
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("CSV output encoding failed.", exception);
        }
    }

    private static void ReturnBuffer(byte[] bytes)
    {
        // The character staging buffer is pooled; encoded records are already bounded by the
        // configured character limit and are kept as exact-length arrays for stream writes.
    }

    private async Task DisposeCoreAsync()
    {
        var acquired = false;
        Exception? finalizationFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            await _writeGate.WaitAsync().ConfigureAwait(false);
            acquired = true;
            try
            {
                if (_stream is not null)
                    await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                finalizationFailure = exception;
            }

            try
            {
                _csv?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }

            try
            {
                _recordWriter?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineCleanup(cleanupFailure, exception);
            }

            try
            {
                if (_stream is not null)
                    await _stream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = CombineCleanup(cleanupFailure, exception);
            }

            _csv = null;
            _recordWriter = null;
            _stream = null;
            _initialized = false;
            if (finalizationFailure is not null || cleanupFailure is not null)
            {
                Volatile.Write(ref _state, Faulted);
                ThrowFailures(finalizationFailure, cleanupFailure);
            }

            Volatile.Write(ref _state, Disposed);
        }
        finally
        {
            if (acquired)
                _writeGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> DisposeResourcesAsync()
    {
        var failures = new List<Exception>();
        var csv = _csv;
        _csv = null;
        try
        {
            csv?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var recordWriter = _recordWriter;
        _recordWriter = null;
        try
        {
            recordWriter?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var stream = _stream;
        _stream = null;
        try
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        _initialized = false;
        return failures;
    }

    private static Exception? CombineCleanup(Exception? existing, Exception next) =>
        existing is null ? next : new AggregateException(existing, next);

    private static void ThrowFailures(Exception? finalizationFailure, Exception? cleanupFailure)
    {
        if (finalizationFailure is not null && cleanupFailure is not null)
            throw new AggregateException(
                "CSV finalization failed and cleanup also failed.",
                finalizationFailure,
                cleanupFailure);
        if (finalizationFailure is not null)
            ExceptionDispatchInfo.Capture(finalizationFailure).Throw();
        if (cleanupFailure is not null)
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }

    private void ThrowIfNotActive()
    {
        if (Volatile.Read(ref _state) != Active)
            throw new ObjectDisposedException(nameof(StrictCsvFileSink<T>));
    }

    private CancellationTokenSource? CreateLinkedCancellation(
        CancellationToken requested,
        out CancellationToken effective)
    {
        if (!_activationCancellationToken.CanBeCanceled)
        {
            effective = requested;
            return null;
        }

        if (!requested.CanBeCanceled || requested == _activationCancellationToken)
        {
            effective = _activationCancellationToken;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _activationCancellationToken,
            requested);
        effective = linked.Token;
        return linked;
    }
}
