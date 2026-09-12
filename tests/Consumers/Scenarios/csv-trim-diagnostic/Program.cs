using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv;

internal static class Program
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "This diagnostic consumer documents the CsvHelper reflection boundary.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "This diagnostic consumer documents the CsvHelper dynamic-code boundary.")]
    private static void Main()
    {
        _ = CsvPipelineDefinitionBuilder.FromCsvFile<Person>(
            new PipelineKey("csv-trim-diagnostic"),
            "not-opened-during-build.csv",
            new CsvSourceOptions());
        Console.WriteLine("CONSUMER_OK csv-trim-diagnostic");
    }

    private sealed record Person(string Name, int Age);
}
