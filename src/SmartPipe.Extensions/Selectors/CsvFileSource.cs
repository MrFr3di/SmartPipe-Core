using System.Globalization;
using System.Runtime.CompilerServices;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>Streams CSV files as pipeline source using CsvHelper.</summary>
/// <typeparam name="T">Record type to map.</typeparam>
public class CsvFileSource<T> : IPipelineSource<T>
{
    private readonly string _path;
    private readonly CsvConfiguration _config;

    /// <summary>Create CSV source for given file.</summary>
    /// <param name="path">Path to CSV file.</param>
    /// <param name="delimiter">Column delimiter (default: ",").</param>
    /// <param name="culture">Culture for parsing (default: InvariantCulture).</param>
    public CsvFileSource(string path, string delimiter = ",", CultureInfo? culture = null)
    {
        _path = ValidatePath(path);
        _config = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = ValidateDelimiter(delimiter),
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };
    }

    /// <inheritdoc />
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        using var reader = new StreamReader(_path);
        using var csv = new CsvReader(reader, _config);
        await foreach (var record in csv.GetRecordsAsync<T>(ct).ConfigureAwait(false))
            yield return ProcessingEnvelope<T>.Create(record);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
