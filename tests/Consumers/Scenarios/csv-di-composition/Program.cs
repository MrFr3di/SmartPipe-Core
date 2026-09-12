using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.Csv;
using SmartPipe.Extensions.DependencyInjection;

var input = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-di-{Guid.NewGuid():N}.csv");
try
{
    await File.WriteAllTextAsync(input, "Name,Age\r\nAda,42\r\n");
    var key = new PipelineKey("csv-di-composition");
    var definition = CsvPipelineDefinitionBuilder.FromCsvFile<Person>(key, input, new CsvSourceOptions()).Build();
    var services = new ServiceCollection();
    services.AddSmartPipe().AddPipeline(definition);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    var factory = provider.GetRequiredService<ISmartPipeFactoryProvider>().GetFactory<Person, Person>(key);
    await using var run = await factory.StartAsync();
    var result = await run.Outputs.ReadAsync();
    await run.Completion;
    if (!result.Result.IsSuccess || result.Result.Value?.Name != "Ada") return 1;
}
finally
{
    File.Delete(input);
}

Console.WriteLine("CONSUMER_OK csv-di-composition");
return 0;

internal sealed record Person(string Name, int Age);
