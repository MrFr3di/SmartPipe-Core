using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a CSV file.</summary>
/// <typeparam name="T">Record type.</typeparam>
public class CsvFileSink<T> : IPipelineSink<T>
{
    private readonly string _path;
    private readonly CsvConfiguration _config;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private StreamWriter? _writer;
    private CsvWriter? _csv;
    private bool _disposed;

    /// <summary>Create CSV file sink.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="delimiter">Column delimiter (default: ",").</param>
    /// <param name="culture">Culture for parsing (default: InvariantCulture).</param>
    public CsvFileSink(string path, string delimiter = ",", CultureInfo? culture = null)
    {
        _path = ValidatePath(path);
        _config = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = ValidateDelimiter(delimiter),
            HasHeaderRecord = true,
        };
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer = new StreamWriter(_path);
            _csv = new CsvWriter(_writer, _config);
            _csv.WriteHeader<T>();
            _csv.NextRecord();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        if (envelope.Payload == null)
            return;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_csv == null)
                throw new InvalidOperationException("Sink is not initialized. Call InitializeAsync before writing.");

            _csv.WriteRecord(envelope.Payload);
            await _csv.NextRecordAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;

            _disposed = true;
            _csv?.Dispose();
            _writer?.Dispose();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string ValidatePath(string? path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path cannot be empty or whitespace.", nameof(path));

        return path;
    }

    private static string ValidateDelimiter(string? delimiter)
    {
        ArgumentNullException.ThrowIfNull(delimiter);
        if (delimiter.Length == 0)
            throw new ArgumentException("Delimiter cannot be empty.", nameof(delimiter));

        return delimiter;
    }
}
