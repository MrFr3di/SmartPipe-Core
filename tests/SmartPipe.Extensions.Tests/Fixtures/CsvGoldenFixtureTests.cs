using CsvHelper.Configuration.Attributes;
using FluentAssertions;
using SmartPipe.Extensions.Selectors;
using SmartPipe.Testing.Fixtures;

namespace SmartPipe.Extensions.Tests.Fixtures;

public class CsvGoldenFixtureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smartpipe-csv-golden-" + Guid.NewGuid().ToString("N"));

    public CsvGoldenFixtureTests()
    {
        GeneratedFixtureData.WriteAllTo(_root);
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
    [Trait("Category", "Golden")]
    public async Task CsvGolden_BomAndNoBom_ParseCorrectly()
    {
        var noBom = await ReadAllAsync<CsvAmountRecord>("csv/basic.csv");
        var bom = await ReadAllAsync<CsvAmountRecord>("csv/bom.csv");

        noBom.Select(r => r.Name).Should().Equal("alpha", "beta");
        bom.Should().ContainSingle().Which.Name.Should().Be("alpha");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_CrlfAndLf_ParseCorrectly()
    {
        var crlf = await ReadAllAsync<CsvAmountRecord>("csv/crlf.csv");
        var lf = await ReadAllAsync<CsvAmountRecord>("csv/lf.csv");

        crlf.Should().ContainSingle().Which.Amount.Should().Be(1);
        lf.Should().ContainSingle().Which.Amount.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_QuotedCommas_ParseCorrectly()
    {
        var rows = await ReadAllAsync<CsvAmountRecord>("csv/quoted-commas.csv");

        rows.Should().ContainSingle().Which.Name.Should().Be("alpha, beta");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_MultilineQuotedField_ParseCorrectly()
    {
        var rows = await ReadAllAsync<CsvNoteRecord>("csv/multiline.csv");

        rows.Should().ContainSingle().Which.Note.Should().Be("line 1\nline 2");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_EmptyAndNullLikeFields_AreHandled()
    {
        var rows = await ReadAllAsync<CsvNullableRecord>("csv/empty-fields.csv");

        rows.Should().ContainSingle();
        rows[0].Amount.Should().BeNull();
        rows[0].Note.Should().Be("");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_DuplicateHeaders_UseConfiguredPolicy()
    {
        var path = Path.Combine(_root, "csv", "duplicate-headers.csv");
        await File.WriteAllTextAsync(path, "Name,Name\nfirst,second\n");

        var rows = await ReadAllAsync<CsvDuplicateHeaderRecord>("csv/duplicate-headers.csv");

        rows.Should().ContainSingle().Which.Name.Should().Be("first");
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_MalformedRows_GoToDeadLetterOrFailurePolicy()
    {
        var act = async () => await ReadAllAsync<CsvAmountRecord>("csv/malformed.csv");

        await act.Should().ThrowAsync<Exception>()
            .Where(ex => ex.GetType().FullName != null
                && ex.GetType().FullName!.Contains("CsvHelper", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_LongFields_DoNotBreakPipeline()
    {
        var longValue = new string('x', 128 * 1024);
        var path = Path.Combine(_root, "csv", "long-field.csv");
        await File.WriteAllTextAsync(path, $"Name,Note\nalpha,{longValue}\n");

        var rows = await ReadAllAsync<CsvNoteRecord>("csv/long-field.csv");

        rows.Should().ContainSingle();
        rows[0].Note.Should().HaveLength(longValue.Length);
    }

    [Fact]
    [Trait("Category", "Golden")]
    public async Task CsvGolden_UnicodeHeadersAndValues_ParseCorrectly()
    {
        var path = Path.Combine(_root, "csv", "unicode-header.csv");
        await File.WriteAllTextAsync(path, "Имя,Сумма\nПример,42\n");

        var rows = await ReadAllAsync<CsvUnicodeRecord>("csv/unicode-header.csv");

        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Пример");
        rows[0].Amount.Should().Be(42);
    }

    private async Task<List<T>> ReadAllAsync<T>(string relativePath)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = new CsvFileSource<T>(path);
        var rows = new List<T>();

        await foreach (var envelope in source.ReadEnvelopesAsync(TestContext.Current.CancellationToken))
            rows.Add(envelope.Payload);

        return rows;
    }

    private sealed class CsvAmountRecord
    {
        public string Name { get; set; } = "";
        public decimal Amount { get; set; }
    }

    private sealed class CsvNullableRecord
    {
        public string Name { get; set; } = "";
        public decimal? Amount { get; set; }
        public string? Note { get; set; }
    }

    private sealed class CsvNoteRecord
    {
        public string Name { get; set; } = "";
        public string Note { get; set; } = "";
    }

    private sealed class CsvDuplicateHeaderRecord
    {
        public string Name { get; set; } = "";
    }

    private sealed class CsvUnicodeRecord
    {
        [Name("Имя")]
        public string Name { get; set; } = "";

        [Name("Сумма")]
        public decimal Amount { get; set; }
    }
}
