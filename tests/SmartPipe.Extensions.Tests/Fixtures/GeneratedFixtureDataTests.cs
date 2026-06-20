using FluentAssertions;
using SmartPipe.Testing.Fixtures;

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

        var fixtures = FixtureCatalog.Discover(_root);

        fixtures.Should().HaveCount(
            GeneratedFixtureData.CsvFixtures.Count + GeneratedFixtureData.JsonFixtures.Count);
        fixtures.Should().OnlyContain(f => f.SizeClass == FixtureSizeClass.Small);
        fixtures.Should().OnlyContain(f => !Path.IsPathRooted(f.RelativePath));
        fixtures.Single(f => f.RelativePath == "csv/bom.csv").Bom.Should().Be("utf-8");
        fixtures.Single(f => f.RelativePath == "csv/crlf.csv").NewlineStyle.Should().Be(FixtureNewlineStyle.Crlf);
        fixtures.Single(f => f.RelativePath == "csv/lf.csv").NewlineStyle.Should().Be(FixtureNewlineStyle.Lf);
    }
}
