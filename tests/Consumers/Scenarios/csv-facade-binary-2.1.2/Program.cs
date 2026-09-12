using SmartPipe.Extensions.Selectors;

var input = Path.Combine(Path.GetTempPath(), $"smartpipe-csv-facade-binary-{Guid.NewGuid():N}.csv");
try
{
    await File.WriteAllTextAsync(input, "Name,Age\r\nAda,42\r\n");
    await using var source = new CsvFileSource<Person>(input);
    await source.InitializeAsync();
    await foreach (var record in source.ReadEnvelopesAsync())
        if (record.Payload.Name == "Ada")
        {
            Console.WriteLine("CONSUMER_OK csv-facade-binary-2.1.2");
            return 0;
        }
    return 1;
}
finally
{
    File.Delete(input);
}

internal sealed record Person(string Name, int Age);
