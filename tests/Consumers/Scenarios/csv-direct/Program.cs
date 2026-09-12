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
    if (!File.ReadAllText(output).Contains("Ada,42", StringComparison.Ordinal)) return 1;
}
finally
{
    File.Delete(input);
    File.Delete(output);
}

Console.WriteLine("CONSUMER_OK csv-direct");
return 0;

internal sealed record Person(string Name, int Age);
