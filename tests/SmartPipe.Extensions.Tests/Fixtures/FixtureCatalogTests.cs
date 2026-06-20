using System.Text;
using System.Text.Json;
using FluentAssertions;
using SmartPipe.Testing.Fixtures;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class FixtureCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-fixtures-" + Guid.NewGuid().ToString("N"));

    public FixtureCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

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
    public void FixtureEnvironment_IsEnabled_AcceptsExplicitTruthyValues()
    {
        const string variableName = "SMARTPIPE_TEST_ENABLE_FIXTURE_CATALOG";
        var original = Environment.GetEnvironmentVariable(variableName);

        try
        {
            foreach (var value in new[] { "1", "true", "TRUE", "yes" })
            {
                Environment.SetEnvironmentVariable(variableName, value);

                FixtureEnvironment.IsEnabled(variableName).Should().BeTrue();
            }

            Environment.SetEnvironmentVariable(variableName, "0");
            FixtureEnvironment.IsEnabled(variableName).Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, original);
        }
    }

    [Fact]
    public void FixtureEnvironment_RealAndLargeFixtures_RequireExplicitOptIn()
    {
        var originalReal = Environment.GetEnvironmentVariable(FixtureEnvironment.EnableRealFixtures);
        var originalLarge = Environment.GetEnvironmentVariable(FixtureEnvironment.EnableLargeFixtures);

        try
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableRealFixtures, null);
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableLargeFixtures, null);

            FixtureEnvironment.RealFixturesEnabled.Should().BeFalse();
            FixtureEnvironment.LargeFixturesEnabled.Should().BeFalse();
            FixtureSkip.RealFixturesDisabled.Should().Contain(FixtureEnvironment.EnableRealFixtures);
            FixtureSkip.LargeFixturesDisabled.Should().Contain(FixtureEnvironment.EnableLargeFixtures);

            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableRealFixtures, "1");
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableLargeFixtures, "1");

            FixtureEnvironment.RealFixturesEnabled.Should().BeTrue();
            FixtureEnvironment.LargeFixturesEnabled.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableRealFixtures, originalReal);
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableLargeFixtures, originalLarge);
        }
    }

    [Fact]
    public void FixtureEnvironment_StressAndSlowTests_AreOptIn()
    {
        var originalStress = Environment.GetEnvironmentVariable(FixtureEnvironment.EnableStressTests);
        var originalSlow = Environment.GetEnvironmentVariable(FixtureEnvironment.EnableSlowTests);

        try
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableStressTests, null);
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableSlowTests, null);

            FixtureEnvironment.StressTestsEnabled.Should().BeFalse();
            FixtureEnvironment.SlowTestsEnabled.Should().BeFalse();
            FixtureSkip.StressTestsDisabled.Should().Contain(FixtureEnvironment.EnableStressTests);
            FixtureSkip.SlowTestsDisabled.Should().Contain(FixtureEnvironment.EnableSlowTests);

            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableStressTests, "1");
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableSlowTests, "1");

            FixtureEnvironment.StressTestsEnabled.Should().BeTrue();
            FixtureEnvironment.SlowTestsEnabled.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableStressTests, originalStress);
            Environment.SetEnvironmentVariable(FixtureEnvironment.EnableSlowTests, originalSlow);
        }
    }

    [Fact]
    public void FixtureCategories_DefinePlanTraits()
    {
        new[]
        {
            FixtureCategories.Golden,
            FixtureCategories.RealFixture,
            FixtureCategories.LargeFixture,
            FixtureCategories.HugeFixture,
            FixtureCategories.Stress,
            FixtureCategories.Aot,
            FixtureCategories.Slow,
        }.Should().BeEquivalentTo(
            "Golden",
            "RealFixture",
            "LargeFixture",
            "HugeFixture",
            "Stress",
            "Aot",
            "Slow");
    }

    [Fact]
    public void FixtureCatalog_Classify_UsesPlanThresholds()
    {
        FixtureCatalog.Classify(1).Should().Be(FixtureSizeClass.Small);
        FixtureCatalog.Classify(FixtureCatalog.SmallMaxBytes).Should().Be(FixtureSizeClass.Small);
        FixtureCatalog.Classify(FixtureCatalog.SmallMaxBytes + 1).Should().Be(FixtureSizeClass.Medium);
        FixtureCatalog.Classify(FixtureCatalog.MediumMaxBytes).Should().Be(FixtureSizeClass.Medium);
        FixtureCatalog.Classify(FixtureCatalog.MediumMaxBytes + 1).Should().Be(FixtureSizeClass.Large);
        FixtureCatalog.Classify(FixtureCatalog.LargeMaxBytes).Should().Be(FixtureSizeClass.Large);
        FixtureCatalog.Classify(FixtureCatalog.LargeMaxBytes + 1).Should().Be(FixtureSizeClass.Huge);
    }

    [Fact]
    public async Task FixtureCatalog_Discover_EnumeratesSupportedFilesAndMetadataOnly()
    {
        var csvPath = Path.Combine(_root, "bom-crlf.csv");
        await File.WriteAllBytesAsync(
            csvPath,
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Name,Value\r\nalpha,1\r\n")]);
        await File.WriteAllTextAsync(Path.Combine(_root, "items.ndjson"), "{\"id\":1}\n{\"id\":2}\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.md"), "# ignored\n");

        var fixtures = FixtureCatalog.Discover(_root);

        fixtures.Select(f => f.RelativePath).Should().BeEquivalentTo("bom-crlf.csv", "items.ndjson");
        fixtures.Should().OnlyContain(f => f.SizeClass == FixtureSizeClass.Small);
        fixtures.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Sha256));
        fixtures.Single(f => f.RelativePath == "bom-crlf.csv").Bom.Should().Be("utf-8");
        fixtures.Single(f => f.RelativePath == "bom-crlf.csv").NewlineStyle.Should().Be(FixtureNewlineStyle.Crlf);
        fixtures.Single(f => f.RelativePath == "items.ndjson").NewlineStyle.Should().Be(FixtureNewlineStyle.Lf);
    }

    [Fact]
    public void FixtureCatalog_Discover_ReturnsEmpty_WhenRootMissing()
    {
        var missingRoot = Path.Combine(_root, "missing");

        FixtureCatalog.Discover(missingRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task FixtureManifest_UsesRelativeMetadataWithoutEmbeddedContent()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "fixture-manifest.json");

        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var fixtures = root.GetProperty("fixtures").EnumerateArray().ToArray();

        root.GetProperty("version").GetInt32().Should().Be(1);
        fixtures.Should().NotBeEmpty();

        foreach (var fixture in fixtures)
        {
            var relativePath = fixture.GetProperty("relativePath").GetString();
            relativePath.Should().NotBeNullOrWhiteSpace();
            Path.IsPathRooted(relativePath!).Should().BeFalse();
            fixture.TryGetProperty("content", out _).Should().BeFalse();
            fixture.TryGetProperty("data", out _).Should().BeFalse();
        }

        var huge = fixtures.Single(f => f.GetProperty("id").GetString() == "soc-pokec-relationships");
        huge.GetProperty("sizeClass").GetString().Should().Be("huge");
        huge.TryGetProperty("sha256", out _).Should().BeFalse();
        huge.GetProperty("expectedRows").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
