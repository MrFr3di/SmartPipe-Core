using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Sinks;

/// <summary>Writes pipeline output to a CSV file.</summary>
/// <typeparam name="T">Record type.</typeparam>
public class CsvFileSink<T> : ISink<T>
{
    private readonly string _path;
    private readonly CsvConfiguration _config;
    private StreamWriter? _writer;
    private CsvWriter? _csv;

    /// <summary>Create CSV file sink.</summary>
    /// <param name="path">Output file path.</param>
    /// <param name="delimiter">Column delimiter (default: ",").</param>
    /// <param name="culture">Culture for parsing (default: InvariantCulture).</param>
    public CsvFileSink(string path, string delimiter = ",", CultureInfo? culture = null)
    {
        _path = path;
        _config = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture) { Delimiter = delimiter, HasHeaderRecord = true };
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default)
    {
        _writer = new StreamWriter(_path);
        _csv = new CsvWriter(_writer, _config);
        _csv.WriteHeader<T>();
        _csv.NextRecord();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ProcessingResult<T> result, CancellationToken ct = default)
    {
        if (result.IsSuccess && result.Value != null)
        {
            _csv?.WriteRecord(result.Value);
            await (_csv?.NextRecordAsync() ?? Task.CompletedTask);
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync()
    {
        _csv?.Dispose();
        _writer?.Dispose();
        return Task.CompletedTask;
    }
}
