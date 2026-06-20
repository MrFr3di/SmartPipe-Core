using System.Text.Json.Serialization;
using FluentAssertions;
using SmartPipe.Extensions.Selectors;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class CapitalPlanParityFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-capital-plan-" + Guid.NewGuid().ToString("N"));

    public CapitalPlanParityFixtureTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "csv"));
        Directory.CreateDirectory(Path.Combine(_root, "json"));
        File.WriteAllText(
            CsvPath,
            """
            ProjectId,Category,Budget2025,Notes
            C-001,Transit,123.45,
            C-002,Parks,,deferred
            C-003,Housing,9876543210.99,priority

            """);
        File.WriteAllText(
            JsonPath,
            """
            [
              { "ProjectId": "C-001", "Category": "Transit", "Budget2025": 123.45, "Notes": null },
              { "ProjectId": "C-002", "Category": "Parks", "Budget2025": null, "Notes": "deferred" },
              { "ProjectId": "C-003", "Category": "Housing", "Budget2025": 9876543210.99, "Notes": "priority" }
            ]
            """);
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

    private string CsvPath => Path.Combine(_root, "csv", "capital-plan.csv");

    private string JsonPath => Path.Combine(_root, "json", "capital-plan.json");

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CapitalPlan_CsvAndJson_ProduceSameLogicalItemCount()
    {
        var csv = await ReadCsvAsync();
        var json = await ReadJsonAsync();

        csv.Should().HaveSameCount(json);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CapitalPlan_CsvAndJson_HaveCompatibleSchema()
    {
        var csv = await ReadCsvAsync();
        var json = await ReadJsonAsync();

        csv.Select(record => record.Schema).Should().OnlyContain(schema => schema.SetEquals(CapitalPlanRecord.SchemaFields));
        json.Select(record => record.Schema).Should().OnlyContain(schema => schema.SetEquals(CapitalPlanRecord.SchemaFields));
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CapitalPlan_CsvAndJson_NumericFieldsMatchWithinTolerance()
    {
        var csv = await ReadCsvAsync();
        var json = await ReadJsonAsync();

        foreach (var pair in PairByProject(csv, json))
            pair.Csv.Budget2025.Should().Be(pair.Json.Budget2025);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CapitalPlan_CsvAndJson_NullFieldsMatchConfiguredPolicy()
    {
        var csv = await ReadCsvAsync();
        var json = await ReadJsonAsync();

        foreach (var pair in PairByProject(csv, json))
        {
            pair.Csv.Budget2025.HasValue.Should().Be(pair.Json.Budget2025.HasValue);
            pair.Csv.Notes.Should().Be(pair.Json.Notes);
        }
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CapitalPlan_CsvAndJson_CategoryFieldsMatch()
    {
        var csv = await ReadCsvAsync();
        var json = await ReadJsonAsync();

        foreach (var pair in PairByProject(csv, json))
            pair.Csv.Category.Should().Be(pair.Json.Category);
    }

    private async Task<List<CapitalPlanRecord>> ReadCsvAsync()
    {
        var source = new CsvFileSource<CapitalPlanCsvRecord>(CsvPath);
        var records = new List<CapitalPlanRecord>();

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
            records.Add(envelope.Payload.Normalize());

        return records;
    }

    private async Task<List<CapitalPlanRecord>> ReadJsonAsync()
    {
        var source = new JsonFileSource<CapitalPlanJsonRecord>(
            JsonPath,
            CapitalPlanParityJsonContext.Default.ListCapitalPlanJsonRecord,
            CapitalPlanParityJsonContext.Default.CapitalPlanJsonRecord);
        var records = new List<CapitalPlanRecord>();

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
            records.Add(envelope.Payload.Normalize());

        return records;
    }

    private static IEnumerable<(CapitalPlanRecord Csv, CapitalPlanRecord Json)> PairByProject(
        IReadOnlyCollection<CapitalPlanRecord> csv,
        IReadOnlyCollection<CapitalPlanRecord> json)
    {
        var jsonByProject = json.ToDictionary(record => record.ProjectId, StringComparer.Ordinal);
        foreach (var csvRecord in csv.OrderBy(record => record.ProjectId, StringComparer.Ordinal))
            yield return (csvRecord, jsonByProject[csvRecord.ProjectId]);
    }

    public sealed class CapitalPlanCsvRecord
    {
        public string ProjectId { get; set; } = "";
        public string Category { get; set; } = "";
        public decimal? Budget2025 { get; set; }
        public string? Notes { get; set; }

        public CapitalPlanRecord Normalize() =>
            new(ProjectId, Category, Budget2025, NormalizeText(Notes));
    }

    public sealed record CapitalPlanJsonRecord(
        string ProjectId,
        string Category,
        decimal? Budget2025,
        string? Notes)
    {
        public CapitalPlanRecord Normalize() =>
            new(ProjectId, Category, Budget2025, NormalizeText(Notes));
    }

    public sealed record CapitalPlanRecord(
        string ProjectId,
        string Category,
        decimal? Budget2025,
        string? Notes)
    {
        public static ISet<string> SchemaFields { get; } =
            new HashSet<string>(StringComparer.Ordinal)
            {
                nameof(ProjectId),
                nameof(Category),
                nameof(Budget2025),
                nameof(Notes),
            };

        public ISet<string> Schema => SchemaFields;
    }

    private static string? NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

[JsonSerializable(typeof(CapitalPlanParityFixtureTests.CapitalPlanJsonRecord))]
[JsonSerializable(typeof(List<CapitalPlanParityFixtureTests.CapitalPlanJsonRecord>))]
internal sealed partial class CapitalPlanParityJsonContext : JsonSerializerContext;
