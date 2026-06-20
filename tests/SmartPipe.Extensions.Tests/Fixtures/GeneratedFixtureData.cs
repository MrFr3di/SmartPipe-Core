using System.Text;

namespace SmartPipe.Extensions.Tests.Fixtures;

public sealed record GeneratedTextFixture(string Id, string RelativePath, byte[] Content);

public static class GeneratedFixtureData
{
    public static IReadOnlyList<GeneratedTextFixture> CsvFixtures { get; } =
    [
        Csv("csv-basic", "csv/basic.csv", "Name,Amount\nalpha,1\nbeta,2\n"),
        new("csv-bom", "csv/bom.csv", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Name,Amount\nalpha,1\n")]),
        Csv("csv-crlf", "csv/crlf.csv", "Name,Amount\r\nalpha,1\r\n"),
        Csv("csv-lf", "csv/lf.csv", "Name,Amount\nalpha,1\n"),
        Csv("csv-quoted-commas", "csv/quoted-commas.csv", "Name,Amount\n\"alpha, beta\",1\n"),
        Csv("csv-escaped-quotes", "csv/escaped-quotes.csv", "Name,Note\nalpha,\"said \"\"hello\"\"\"\n"),
        Csv("csv-multiline-quoted-field", "csv/multiline.csv", "Name,Note\nalpha,\"line 1\nline 2\"\n"),
        Csv("csv-empty-fields", "csv/empty-fields.csv", "Name,Amount,Note\nalpha,,\n"),
        Csv("csv-malformed-row", "csv/malformed.csv", "Name,Amount\nalpha,not-a-number\n"),
        Csv("csv-unicode", "csv/unicode.csv", "Name,Amount\nПример,42\n"),
    ];

    public static IReadOnlyList<GeneratedTextFixture> JsonFixtures { get; } =
    [
        Json("json-root-array", "json/root-array.json", """[{"Name":"alpha","Amount":1},{"Name":"beta","Amount":2}]"""),
        Json("json-top-level-values", "json/top-level-values.ndjson", """{"Name":"alpha","Amount":1}""" + "\n" + """{"Name":"beta","Amount":2}""" + "\n"),
        Json("json-ndjson", "json/items.ndjson", """{"Name":"alpha","Amount":1}""" + "\n" + """{"Name":"beta","Amount":2}""" + "\n"),
        Json("json-nulls", "json/nulls.json", """[{"Name":"alpha","Amount":null},{"Name":null,"Amount":2}]"""),
        Json("json-missing-fields", "json/missing-fields.json", """[{"Name":"alpha"},{"Amount":2}]"""),
        Json("json-malformed", "json/malformed.json", """[{"Name":"alpha"}"""),
        Json("json-empty-array", "json/empty-array.json", "[]"),
        Json("json-empty-file", "json/empty-file.json", ""),
    ];

    public static string WriteAllTo(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Directory.CreateDirectory(root);

        foreach (var fixture in CsvFixtures.Concat(JsonFixtures))
        {
            var path = Path.Combine(root, fixture.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, fixture.Content);
        }

        return root;
    }

    private static GeneratedTextFixture Csv(string id, string relativePath, string content) =>
        new(id, relativePath, Encoding.UTF8.GetBytes(content));

    private static GeneratedTextFixture Json(string id, string relativePath, string content) =>
        new(id, relativePath, Encoding.UTF8.GetBytes(content));
}
