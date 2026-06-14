using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// CSV serialization/deserialization transformer using CsvHelper.
/// Converts <typeparamref name="TInput"/> to CSV string, then deserializes to <typeparamref name="TOutput"/>.
/// Supports custom delimiter, culture, and mapping configuration via <see cref="CsvConfiguration"/>.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="TInput">The input type to serialize to CSV.</typeparam>
/// <typeparam name="TOutput">The output type to deserialize from CSV.</typeparam>
public class CsvTransform<TInput, TOutput> : IPipelineTransformer<TInput, TOutput>
{
    private readonly CsvConfiguration _readConfig;
    private readonly CsvConfiguration _writeConfig;

    /// <summary>
    /// Initializes a new instance of <see cref="CsvTransform{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="delimiter">The CSV delimiter character. Defaults to ",".</param>
    /// <param name="culture">The culture for parsing. Defaults to <see cref="CultureInfo.InvariantCulture"/>.</param>
    /// <param name="configure">Optional action to configure CSV reading behavior.</param>
    public CsvTransform(
        string delimiter = ",",
        CultureInfo? culture = null,
        Action<CsvConfiguration>? configure = null
    )
    {
        var baseConfig = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
        };

        if (configure != null)
            configure(baseConfig);

        _readConfig = baseConfig;
        _writeConfig = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
        };
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask<StageResult<TOutput>> TransformAsync(
        ProcessingEnvelope<TInput> envelope,
        CancellationToken ct = default
    )
    {
        try
        {
            using var writer = new StringWriter();
            using var csvWriter = new CsvWriter(writer, _writeConfig);
            csvWriter.WriteRecords(new[] { envelope.Payload });
            var csv = writer.ToString();

            using var reader = new StringReader(csv);
            using var csvReader = new CsvReader(reader, _readConfig);
            var firstRecord = csvReader.GetRecords<TOutput>().First();

            return ValueTask.FromResult(
                StageResult<TOutput>.Success(firstRecord)
            );
        }
        catch (CsvHelperException ex)
        {
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError(
                        $"CSV parsing error: {ex.Message}",
                        ErrorType.Permanent,
                        "Serialization",
                        ex
                    )
                )
            );
        }
        catch (IOException ex)
        {
            return ValueTask.FromResult(
                StageResult<TOutput>.Failure(
                    new SmartPipeError(
                        $"CSV IO error: {ex.Message}",
                        ErrorType.Transient,
                        "Serialization",
                        ex
                    )
                )
            );
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
