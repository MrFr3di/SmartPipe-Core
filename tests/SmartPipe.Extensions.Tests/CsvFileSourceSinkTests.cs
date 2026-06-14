using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Extensions.Sinks;

namespace SmartPipe.Extensions.Tests;

public class CsvFileSourceSinkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CsvFileSource_Constructor_ShouldRejectInvalidPath(string? path)
    {
        var act = () => new CsvFileSource<TestRecord>(path!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CsvFileSource_Constructor_ShouldRejectInvalidDelimiter(string? delimiter)
    {
        var act = () => new CsvFileSource<TestRecord>("records.csv", delimiter!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task CsvFileSource_ShouldReadRecords()
    {
        var path = "test_csv_source.csv";
        await File.WriteAllTextAsync(path, "Name,Age\nAlice,30\nBob,25\n");

        var source = new CsvFileSource<TestRecord>(path);
        var results = new List<ProcessingEnvelope<TestRecord>>();
        await foreach (var ctx in source.ReadEnvelopesAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
        results[0].Payload.Name.Should().Be("Alice");
        File.Delete(path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CsvFileSink_Constructor_ShouldRejectInvalidPath(string? path)
    {
        var act = () => new CsvFileSink<TestRecord>(path!);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CsvFileSink_Constructor_ShouldRejectInvalidDelimiter(string? delimiter)
    {
        var act = () => new CsvFileSink<TestRecord>("records.csv", delimiter!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task CsvFileSink_WriteAsync_ShouldThrow_WhenNotInitialized()
    {
        var sink = new CsvFileSink<TestRecord>(
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csv"));

        var act = async () => await sink.WriteAsync(
            ProcessingEnvelope<TestRecord>.Create(new TestRecord { Name = "Alice", Age = 30 }));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*InitializeAsync*");
    }

    [Fact]
    public async Task CsvFileSink_ShouldWriteRecords()
    {
        var path = "test_csv_sink.csv";
        var sink = new CsvFileSink<TestRecord>(path);
        await sink.InitializeAsync();
        await sink.WriteAsync(ProcessingEnvelope<TestRecord>.Create(new TestRecord { Name = "Alice", Age = 30 }));
        await sink.WriteAsync(ProcessingEnvelope<TestRecord>.Create(new TestRecord { Name = "Bob", Age = 25 }));
        await sink.DisposeAsync();

        var content = await File.ReadAllTextAsync(path);
        content.Should().Contain("Alice");
        content.Should().Contain("Bob");
        File.Delete(path);
    }

    public class TestRecord { public string Name { get; set; } = ""; public int Age { get; set; } }
}
