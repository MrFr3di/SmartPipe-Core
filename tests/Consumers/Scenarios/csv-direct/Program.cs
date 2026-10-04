using SmartPipe.ConsumerScenarios;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv;

var input = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-direct-{Guid.NewGuid():N}-in.csv");
var output = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-direct-{Guid.NewGuid():N}-out.csv");
try
{
    await File.WriteAllTextAsync(input, "Name,Age\r\nAda,42\r\n");
    var definition = CsvPipelineDefinitionBuilder
        .FromCsvFile<Person>(new PipelineKey("csv-direct"), input, new CsvSourceOptions())
        .ToCsvFile(output, new CsvSinkOptions());
    await using var run = await definition.StartAsync();
    await run.Completion;
    if (!(await File.ReadAllTextAsync(output)).Contains("Ada,42", StringComparison.Ordinal)) return 1;
}
finally
{
    File.Delete(input);
    File.Delete(output);
}

await Console.Out.WriteLineAsync("CONSUMER_OK csv-direct");
return 0;

namespace SmartPipe.ConsumerScenarios
{
    internal sealed record Person(string Name, int Age);
}
