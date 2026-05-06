using System.Globalization;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams CSV files as pipeline source using CsvHelper.</summary>
/// <typeparam name="T">Record type to map.</typeparam>
public class CsvFileSource<T> : ISource<T>
{
    private readonly string _path;
    private readonly CsvConfiguration _config;

    /// <summary>Create CSV source for given file.</summary>
    /// <param name="path">Path to CSV file.</param>
    /// <param name="delimiter">Column delimiter (default: ",").</param>
    /// <param name="culture">Culture for parsing (default: InvariantCulture).</param>
    public CsvFileSource(string path, string delimiter = ",", CultureInfo? culture = null)
    {
        _path = path;
        _config = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        };
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(_path);
        using var csv = new CsvReader(reader, _config);
        await foreach (var record in csv.GetRecordsAsync<T>(ct).ConfigureAwait(false))
            yield return new ProcessingContext<T>(record);
    }

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;
}
