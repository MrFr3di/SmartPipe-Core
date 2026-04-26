using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// CSV serialization/deserialization transformer using CsvHelper.
/// Supports custom delimiter, culture, and mapping configuration.
/// </summary>
/// <typeparam name="TInput">Input type.</typeparam>
/// <typeparam name="TOutput">Output type.</typeparam>
public class CsvTransform<TInput, TOutput> : ITransformer<TInput, TOutput>
{
    private readonly CsvConfiguration _readConfig;
    private readonly CsvConfiguration _writeConfig;

    public CsvTransform(
        string delimiter = ",",
        CultureInfo? culture = null,
        Action<CsvConfiguration>? configure = null)
    {
        var baseConfig = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null
        };

        if (configure != null) configure(baseConfig);

        _readConfig = baseConfig;
        _writeConfig = new CsvConfiguration(culture ?? CultureInfo.InvariantCulture)
        {
            Delimiter = delimiter,
            HasHeaderRecord = true
        };
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask<ProcessingResult<TOutput>> TransformAsync(ProcessingContext<TInput> ctx, CancellationToken ct = default)
    {
        try
        {
            using var writer = new StringWriter();
            using var csvWriter = new CsvWriter(writer, _writeConfig);
            csvWriter.WriteRecords(new[] { ctx.Payload });
            var csv = writer.ToString();

            using var reader = new StringReader(csv);
            using var csvReader = new CsvReader(reader, _readConfig);
            var records = csvReader.GetRecords<TOutput>().ToList();

            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Success(records.First(), ctx.TraceId));
        }
        catch (Exception ex)
        {
            return ValueTask.FromResult(
                ProcessingResult<TOutput>.Failure(
                    new SmartPipeError($"CSV transform failed: {ex.Message}", ErrorType.Permanent, "Serialization", ex),
                    ctx.TraceId));
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
