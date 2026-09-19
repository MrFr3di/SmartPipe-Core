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
        var csv = _csv!;
        var recordIndex = 0L;
        Exception? primaryFailure = null;

        try
        {
            if (_options.HasHeaderRecord)
            {
                try
                {
                    var headerResult = await ReadNextAsync(csv, cancellationToken, recordIndex, isHeader: true).ConfigureAwait(false);
                    recordIndex = headerResult.RecordIndex;
                    if (!headerResult.HasRecord)
                        yield break;

                    csv.ReadHeader();
                    csv.ValidateHeader<T>();
                }
                catch (OperationCanceledException exception)
                {
                    primaryFailure = exception;
                }
                catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
                {
                    primaryFailure = CreateHeaderException(exception);
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }
            }

            while (primaryFailure is null)
            {
                CsvReadResult readResult;
                try
                {
                    readResult = await ReadNextAsync(csv, cancellationToken, recordIndex, isHeader: false).ConfigureAwait(false);
                    recordIndex = readResult.RecordIndex;
                }
                catch (OperationCanceledException exception)
                {
                    primaryFailure = exception;
                    break;
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                    break;
                }

                if (!readResult.HasRecord)
                    break;

                T value = default!;
                try
                {
                    value = csv.GetRecord<T>();
                }
                catch (OperationCanceledException exception)
                {
                    primaryFailure = exception;
                }
                catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
                {
                    if (!HandleDataFailure(recordIndex, exception, "mapping"))
                        primaryFailure = CreateDataException(recordIndex, exception, "mapping");
                    else
                        continue;
                }
                catch (Exception exception)
                {
                    primaryFailure = exception;
                }

                if (primaryFailure is not null)
                    break;

                if (value is null)
                {
                    var exception = new InvalidDataException("CSV record mapped to null.");
                    if (!HandleDataFailure(recordIndex, exception, "mapping"))
                    {
                        primaryFailure = CreateDataException(recordIndex, exception, "mapping");
                        break;
                    }

                    continue;
                }

                yield return ProcessingEnvelope<T>.Create(value);
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
        await _initializeGate.WaitAsync().ConfigureAwait(false);
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
        if (_initialized)
        {
            _bridge!.SetDefaultCancellationToken(cancellationToken);
            return;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        _bridge!.SetDefaultCancellationToken(cancellationToken);
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidDataException exception)
            {
                recordIndex++;
                var category = IsUnrecoverable(exception) ? "framing" : "limit";
                if (isHeader || IsUnrecoverable(exception) || !HandleDataFailure(recordIndex, exception, category))
                    throw CreateHeaderOrDataException(recordIndex, exception, isHeader, category);
            }
            catch (IOException)
            {
                throw;
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
        new("CSV header validation failed.");

    private static InvalidDataException CreateDataException(long recordIndex, Exception exception, string category) =>
        new($"CSV record {recordIndex} failed during {category}.");

    private static InvalidDataException CreateHeaderOrDataException(
        long recordIndex,
        Exception exception,
        bool isHeader,
        string category) =>
        isHeader
            ? CreateHeaderException(exception)
            : CreateDataException(recordIndex, exception, category);

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
