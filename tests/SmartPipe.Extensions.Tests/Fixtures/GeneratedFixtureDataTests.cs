using FluentAssertions;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class GeneratedFixtureDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-generated-fixtures-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp files created by the test.
        }
    }

    [Fact]
    public void GeneratedSmallFixtures_CoverPlanCsvCases()
    {
        GeneratedFixtureData.CsvFixtures.Select(f => f.Id).Should().BeEquivalentTo(
            "csv-basic",
            "csv-bom",
            "csv-crlf",
            "csv-lf",
            "csv-quoted-commas",
            "csv-escaped-quotes",
            "csv-multiline-quoted-field",
            "csv-empty-fields",
            "csv-malformed-row",
            "csv-unicode");
    }

    [Fact]
    public void GeneratedSmallFixtures_CoverPlanJsonCases()
    {
        GeneratedFixtureData.JsonFixtures.Select(f => f.Id).Should().BeEquivalentTo(
            "json-root-array",
            "json-top-level-values",
            "json-ndjson",
            "json-nulls",
            "json-missing-fields",
            "json-malformed",
            "json-empty-array",
            "json-empty-file");
    }

    [Fact]
    public void GeneratedSmallFixtures_AreDiscoverableAndSmall()
    {
        GeneratedFixtureData.WriteAllTo(_root);

        var files = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        files.Should().BeEquivalentTo(
            GeneratedFixtureData.CsvFixtures.Concat(GeneratedFixtureData.JsonFixtures).Select(f => f.RelativePath));
        files.Should().OnlyContain(path => !Path.IsPathRooted(path));
        new FileInfo(Path.Combine(_root, "csv", "bom.csv")).Length.Should().BeLessThan(1024);
        File.ReadAllBytes(Path.Combine(_root, "csv", "bom.csv"))[..3].Should().Equal(0xEF, 0xBB, 0xBF);
        File.ReadAllText(Path.Combine(_root, "csv", "crlf.csv")).Should().Contain("\r\n");
        File.ReadAllText(Path.Combine(_root, "csv", "lf.csv")).Should().NotContain("\r\n");
    }
}
