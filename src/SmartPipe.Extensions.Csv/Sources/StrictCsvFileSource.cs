using System.Runtime.CompilerServices;
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

    private StreamReader? _reader;
    private CsvBoundedRecordTextReader? _bridge;
    private CsvReader? _csv;
    private bool _initialized;
    private bool _disposed;

    public StrictCsvFileSource(
        string path,
        CsvSourceOptionsSnapshot options,
        CsvMapRegistration<T> map,
        ILogger<StrictCsvFileSource<T>>? logger,
        CancellationToken activationCancellationToken)
    {
        _path = path;
        _options = options;
        _map = map;
        _logger = logger;
        _activationCancellationToken = activationCancellationToken;
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
            _reader = await CsvStrictEncoding.OpenReaderAsync(_path, _options, cancellationToken).ConfigureAwait(false);
            _bridge = new CsvBoundedRecordTextReader(
                new CsvLogicalRecordFramer(
                    _reader,
                    _options.Delimiter,
                    _options.Quote,
                    _options.MaxRecordSizeCharacters,
                    _options.MaxFieldSizeCharacters,
                    _options.MaxColumnCount,
                    cancellationToken),
                cancellationToken);
            _csv = new CsvReader(_bridge, CreateConfiguration(), leaveOpen: false);
            _map.Register(_csv.Context);
            _initialized = true;
        }
        catch
        {
            await DisposeResourcesAsync().ConfigureAwait(false);
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

        try
        {
            if (_options.HasHeaderRecord)
            {
                var headerResult = await ReadNextAsync(csv, cancellationToken, recordIndex, isHeader: true).ConfigureAwait(false);
                recordIndex = headerResult.RecordIndex;
                if (!headerResult.HasRecord)
                    yield break;

                try
                {
                    csv.ReadHeader();
                    csv.ValidateHeader<T>();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
                {
                    throw CreateHeaderException(exception);
                }
            }

            while (true)
            {
                var readResult = await ReadNextAsync(csv, cancellationToken, recordIndex, isHeader: false).ConfigureAwait(false);
                recordIndex = readResult.RecordIndex;
                if (!readResult.HasRecord)
                    break;

                T value;
                try
                {
                    value = csv.GetRecord<T>();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is CsvHelperException or InvalidDataException)
                {
                    if (!HandleDataFailure(recordIndex, exception, "mapping"))
                        throw CreateDataException(recordIndex, exception, "mapping");
                    continue;
                }

                if (value is null)
                {
                    if (!HandleDataFailure(recordIndex, new InvalidDataException("CSV record mapped to null."), "mapping"))
                        throw CreateDataException(recordIndex, new InvalidDataException("CSV record mapped to null."), "mapping");
                    continue;
                }

                yield return ProcessingEnvelope<T>.Create(value);
            }
        }
        finally
        {
            await DisposeResourcesAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _initializeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            await DisposeResourcesAsync().ConfigureAwait(false);
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

    private async ValueTask DisposeResourcesAsync()
    {
        _csv?.Dispose();
        _csv = null;
        _bridge?.Dispose();
        _bridge = null;
        if (_reader is not null)
        {
            _reader.Dispose();
            _reader = null;
        }
        _initialized = false;
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
