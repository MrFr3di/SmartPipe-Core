using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public class CsvFileSourceSinkTests
{
    [Fact]
    public async Task CsvFileSource_ShouldReadRecords()
    {
        var path = "test_csv_source.csv";
        await File.WriteAllTextAsync(path, "Name,Age\nAlice,30\nBob,25\n");

        var source = new CsvFileSource<TestRecord>(path);
        var results = new List<ProcessingContext<TestRecord>>();
        await foreach (var ctx in source.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
        results[0].Payload.Name.Should().Be("Alice");
        File.Delete(path);
    }

    [Fact]
    public async Task CsvFileSink_ShouldWriteRecords()
    {
        var path = "test_csv_sink.csv";
        var sink = new CsvFileSink<TestRecord>(path);
        await sink.InitializeAsync();
        await sink.WriteAsync(ProcessingResult<TestRecord>.Success(new TestRecord { Name = "Alice", Age = 30 }, 1));
        await sink.WriteAsync(ProcessingResult<TestRecord>.Success(new TestRecord { Name = "Bob", Age = 25 }, 2));
        await sink.DisposeAsync();

        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("Alice");
        content.Should().Contain("Bob");
        File.Delete(path);
    }

    public class TestRecord { public string Name { get; set; } = ""; public int Age { get; set; } }
}
