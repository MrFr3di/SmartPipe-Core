using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv;

internal sealed class StrictCsvFileSource<T> : IPipelineSource<T>
{
    private const string MappingCategory = "mapping";
    private readonly string _path;
    private readonly CsvSourceOptionsSnapshot _options;
    private readonly CsvMapRegistration<T> _map;
    private readonly ILogger<StrictCsvFileSource<T>>? _logger;
    private readonly CancellationToken _activationCancellationToken;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    private readonly Func<string, CsvSourceOptionsSnapshot, CancellationToken, ValueTask<TextReader>> _readerFactory;
    private readonly Func<CsvLogicalRecordFramer, CancellationToken, CsvBoundedRecordTextReader> _bridgeFactory;
    private readonly object _disposeSync = new();

    private TextReader? _reader;
    private CsvBoundedRecordTextReader? _bridge;
    private CsvReader? _csv;
    private bool _initialized;
    private bool _disposed;
    private Task? _disposeTask;

    public StrictCsvFileSource(
        string path,
        CsvSourceOptionsSnapshot options,
        CsvMapRegistration<T> map,
        ILogger<StrictCsvFileSource<T>>? logger,
        CancellationToken activationCancellationToken,
        Func<string, CsvSourceOptionsSnapshot, CancellationToken, ValueTask<TextReader>>? readerFactory = null,
        Func<CsvLogicalRecordFramer, CancellationToken, CsvBoundedRecordTextReader>? bridgeFactory = null)
    {
        _path = path;
        _options = options;
        _map = map;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
        _readerFactory = readerFactory ?? OpenReaderAsync;
        _bridgeFactory = bridgeFactory ?? (static (framer, cancellationToken) =>
            new CsvBoundedRecordTextReader(framer, cancellationToken));
    }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _initializeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;

            using var linked = CreateLinkedCancellation(ct, out var cancellationToken);
            _reader = await _readerFactory(_path, _options, cancellationToken).ConfigureAwait(false);
            _bridge = _bridgeFactory(
                new CsvLogicalRecordFramer(
                    _reader,
                    _options.Delimiter,
                    _options.Quote,
                    _options.MaxRecordSizeCharacters,
                    _options.MaxFieldSizeCharacters,
                    _options.MaxColumnCount,
                    cancellationToken),
                cancellationToken);
            _csv = new CsvReader(_bridge, CreateConfiguration(), leaveOpen: true);
            _map.Register(_csv.Context);
            _initialized = true;
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = await DisposeResourcesAsync().ConfigureAwait(false);
            ThrowFailures(primaryFailure, cleanupFailures);
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var linkedCancellation = CreateLinkedCancellation(ct, out var cancellationToken);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var csv = _csv
            ?? throw new InvalidOperationException("CSV source initialization did not create its CsvReader.");
        var recordIndex = 0L;
        Exception? primaryFailure = null;

        try
        {
            if (_options.HasHeaderRecord)
            {
                var header = await ReadHeaderOutcomeAsync(
                    csv,
                    cancellationToken,
                    recordIndex).ConfigureAwait(false);
                recordIndex = header.RecordIndex;
                primaryFailure = header.Failure;
                if (primaryFailure is null && !header.HasRecord)
                    yield break;
            }

            while (primaryFailure is null)
            {
                var record = await ReadRecordOutcomeAsync(
                    csv,
                    cancellationToken,
                    recordIndex).ConfigureAwait(false);
                recordIndex = record.RecordIndex;

                if (record.Failure is not null)
                {
                    primaryFailure = record.Failure;
                    break;
                }

                if (!record.HasRecord)
                    break;

                if (record.Envelope is { } envelope)
                    yield return envelope;
            }
        }
        finally
        {
            var cleanupFailures = await DisposeResourcesAsync().ConfigureAwait(false);
            ThrowFailures(primaryFailure, cleanupFailures);
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private async ValueTask<CsvHeaderReadOutcome> ReadHeaderOutcomeAsync(
        CsvReader csv,
        CancellationToken cancellationToken,
        long recordIndex)
    {
        try
        {
            var headerResult = await ReadNextAsync(
                csv,
                cancellationToken,
                recordIndex,
                isHeader: true).ConfigureAwait(false);
            if (!headerResult.HasRecord)
                return new(headerResult.RecordIndex, HasRecord: false, Failure: null);

            csv.ReadHeader();
            csv.ValidateHeader<T>();
            return new(headerResult.RecordIndex, HasRecord: true, Failure: null);
        }
        catch (OperationCanceledException exception)
        {
            return new(recordIndex, HasRecord: false, exception);
        }
        catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
        {
            return new(recordIndex, HasRecord: false, CreateHeaderException(exception));
        }
        catch (Exception exception)
        {
            return new(recordIndex, HasRecord: false, exception);
        }
    }

    private async ValueTask<CsvRecordReadOutcome> ReadRecordOutcomeAsync(
        CsvReader csv,
        CancellationToken cancellationToken,
        long recordIndex)
    {
        CsvReadResult readResult;
        try
        {
            readResult = await ReadNextAsync(
                csv,
                cancellationToken,
                recordIndex,
                isHeader: false).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new(recordIndex, HasRecord: false, Envelope: null, exception);
        }

        if (!readResult.HasRecord)
            return new(readResult.RecordIndex, HasRecord: false, Envelope: null, Failure: null);

        return MapRecord(csv, readResult.RecordIndex);
    }

    private CsvRecordReadOutcome MapRecord(CsvReader csv, long recordIndex)
    {
        try
        {
            var value = csv.GetRecord<T>();
            if (value is not null)
            {
                return new(
                    recordIndex,
                    HasRecord: true,
                    ProcessingEnvelope<T>.Create(value),
                    Failure: null);
            }

            var nullRecord = new InvalidDataException("CSV record mapped to null.");
            return HandleDataFailure(recordIndex, nullRecord, MappingCategory)
                ? new(recordIndex, HasRecord: true, Envelope: null, Failure: null)
                : new(
                    recordIndex,
                    HasRecord: true,
                    Envelope: null,
                    CreateDataException(recordIndex, nullRecord, MappingCategory));
        }
        catch (OperationCanceledException exception)
        {
            return new(recordIndex, HasRecord: true, Envelope: null, exception);
        }
        catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
        {
            return HandleDataFailure(recordIndex, exception, MappingCategory)
                ? new(recordIndex, HasRecord: true, Envelope: null, Failure: null)
                : new(
                    recordIndex,
                    HasRecord: true,
                    Envelope: null,
                    CreateDataException(recordIndex, exception, MappingCategory));
        }
        catch (Exception exception)
        {
            return new(recordIndex, HasRecord: true, Envelope: null, exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? starter = null;
        Task task;
        lock (_disposeSync)
        {
            if (_disposeTask is null)
            {
                starter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = starter.Task;
            }

            task = _disposeTask;
        }

        if (starter is not null)
            _ = RunDisposeAsync(starter);

        return new ValueTask(task);
    }

    private async Task RunDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _initializeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            var cleanupFailures = await DisposeResourcesAsync().ConfigureAwait(false);
            ThrowFailures(null, cleanupFailures);
        }
        finally
        {
            _initializeGate.Release();
            _initializeGate.Dispose();
        }
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        var bridge = _bridge
            ?? throw new InvalidOperationException("CSV source initialization did not create its record bridge.");
        bridge.SetDefaultCancellationToken(cancellationToken);
    }

    private async ValueTask<CsvReadResult> ReadNextAsync(
        CsvReader csv,
        CancellationToken cancellationToken,
        long recordIndex,
        bool isHeader)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var hasRecord = await csv.ReadAsync().ConfigureAwait(false);
                if (!hasRecord)
                    return new CsvReadResult(false, recordIndex);
                recordIndex++;
                return new CsvReadResult(true, recordIndex);
            }
            catch (InvalidDataException exception)
            {
                recordIndex++;
                var category = IsUnrecoverable(exception) ? "framing" : "limit";
                if (isHeader || IsUnrecoverable(exception) || !HandleDataFailure(recordIndex, exception, category))
                    throw CreateHeaderOrDataException(recordIndex, exception, isHeader, category);
            }
            catch (CsvHelperException exception)
            {
                recordIndex++;
                if (isHeader || !HandleDataFailure(recordIndex, exception, "reading"))
                    throw CreateHeaderOrDataException(recordIndex, exception, isHeader, "reading");
            }
        }
    }

    private static bool IsUnrecoverable(InvalidDataException exception) =>
        exception.Data[CsvLogicalRecordFramer.FailureKindDataKey] is string kind
            && kind == CsvLogicalRecordFramer.UnrecoverableFailureKind;

    private bool HandleDataFailure(long recordIndex, Exception exception, string category)
    {
        if (_options.InvalidRecordBehavior == CsvInvalidRecordBehavior.Throw)
            return false;

        _logger?.LogWarning(
            "Skipping CSV record {RecordIndex} in {Path}; failure category {FailureCategory}.",
            recordIndex,
            _path,
            category);
        return true;
    }

    private static InvalidDataException CreateHeaderException(Exception exception) =>
        new("CSV header validation failed.", exception);

    private static InvalidDataException CreateDataException(long recordIndex, Exception exception, string category) =>
        new($"CSV record {recordIndex} failed during {category}.", exception);

    private static InvalidDataException CreateHeaderOrDataException(
        long recordIndex,
        Exception exception,
        bool isHeader,
        string category) =>
        isHeader
            ? CreateHeaderException(exception)
            : CreateDataException(recordIndex, exception, category);

    private readonly record struct CsvHeaderReadOutcome(
        long RecordIndex,
        bool HasRecord,
        Exception? Failure);

    private readonly record struct CsvRecordReadOutcome(
        long RecordIndex,
        bool HasRecord,
        ProcessingEnvelope<T>? Envelope,
        Exception? Failure);

    private CsvConfiguration CreateConfiguration()
    {
        var configuration = new CsvConfiguration(_options.Culture)
        {
            Mode = CsvMode.RFC4180,
            Delimiter = _options.Delimiter.ToString(),
            Quote = _options.Quote,
            Escape = _options.Quote,
            DetectDelimiter = false,
            AllowComments = false,
            TrimOptions = TrimOptions.None,
            HasHeaderRecord = _options.HasHeaderRecord,
            IgnoreBlankLines = _options.IgnoreBlankLines,
            DetectColumnCountChanges = _options.DetectColumnCountChanges,
            MaxFieldSize = _options.MaxFieldSizeCharacters,
            BufferSize = _options.BufferSize,
            ExceptionMessagesContainRawData = false,
            BadDataFound = _ => throw new InvalidDataException("CSV bad data."),
            ReadingExceptionOccurred = _ => false,
        };

        if (_options.MissingFieldBehavior == CsvMissingFieldBehavior.UseDefault)
            configuration.MissingFieldFound = null;
        if (_options.HeaderValidationBehavior == CsvHeaderValidationBehavior.Ignore)
            configuration.HeaderValidated = null;

        return configuration;
    }

    private ValueTask<IReadOnlyList<Exception>> DisposeResourcesAsync()
    {
        var failures = new List<Exception>();
        var csv = Interlocked.Exchange(ref _csv, null);
        try
        {
            csv?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var bridge = Interlocked.Exchange(ref _bridge, null);
        try
        {
            bridge?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var reader = Interlocked.Exchange(ref _reader, null);
        try
        {
            reader?.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        _initialized = false;
        return ValueTask.FromResult<IReadOnlyList<Exception>>(failures);
    }

    private static async ValueTask<TextReader> OpenReaderAsync(
        string path,
        CsvSourceOptionsSnapshot options,
        CancellationToken cancellationToken) =>
        await CsvStrictEncoding.OpenReaderAsync(path, options, cancellationToken).ConfigureAwait(false);

    private static void ThrowFailures(Exception? primaryFailure, IReadOnlyList<Exception> cleanupFailures)
    {
        if (primaryFailure is not null)
        {
            if (cleanupFailures.Count != 0)
                throw new AggregateException(
                    "CSV operation failed and cleanup also failed.",
                    new[] { primaryFailure }.Concat(cleanupFailures));

            return;
        }

        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException("CSV cleanup failed.", cleanupFailures);
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

    private readonly record struct CsvReadResult(bool HasRecord, long RecordIndex);
}
