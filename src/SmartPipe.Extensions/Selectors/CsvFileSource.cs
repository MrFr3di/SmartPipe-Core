using System.Globalization;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams CSV files as pipeline source using CsvHelper.</summary>
public class CsvFileSource<T> : ISource<T>
{
    private readonly string _path;
    private readonly CsvConfiguration _config;

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

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(_path);
        using var csv = new CsvReader(reader, _config);
        await foreach (var record in csv.GetRecordsAsync<T>(ct).ConfigureAwait(false))
            yield return new ProcessingContext<T>(record);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
