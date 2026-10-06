using System.Text.Json;
using SmartPipe.RepositoryChecks.Reporting;

namespace SmartPipe.RepositoryChecks.Tests.Reporting;

public sealed class CheckRunReportingTests
{
    [Fact]
    public void Normalize_SortsDiagnosticsAndJsonlIsCompactAndLfTerminated()
    {
        var run = new CheckRun(
            "verify-package-graph",
            "fast",
            false,
            17,
            [
                new CheckDiagnostic("Z002", "last", "src/z.cs", 4),
                new CheckDiagnostic("A001", "second", "src/a.cs", 9),
                new CheckDiagnostic("A001", "first", "src/a.cs", 2),
            ]);

        var normalized = CheckRunNormalizer.Normalize(run);
        var jsonl = CheckRunJsonlRenderer.Render(normalized);

        Assert.Equal(["A001:first", "A001:second", "Z002:last"],
            normalized.Diagnostics.Select(diagnostic => $"{diagnostic.Code}:{diagnostic.Summary}"));
        Assert.EndsWith("\n", jsonl);
        Assert.DoesNotContain("\r", jsonl);
        Assert.DoesNotContain("\n", jsonl.TrimEnd('\n'));
        Assert.DoesNotContain("\n  ", jsonl);
        Assert.Equal(1, jsonl.Count(character => character == '\n'));
        Assert.Equal(1, JsonDocument.Parse(jsonl).RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Normalize_IsDeterministicAcrossInputPermutations()
    {
        var first = new CheckRun(
            "check",
            "profile",
            false,
            3,
            [
                new CheckDiagnostic("ERR", "same", "src/file.cs", 7, "artifacts/z.log"),
                new CheckDiagnostic("ERR", "same", "src/file.cs", 7, "artifacts/a.log"),
            ],
            new Dictionary<string, int> { ["z-count"] = 2, ["a-count"] = 1 });
        var second = first with
        {
            Diagnostics = first.Diagnostics.Reverse().ToArray(),
            Counters = new Dictionary<string, int> { ["a-count"] = 1, ["z-count"] = 2 },
        };

        Assert.Equal(CheckRunJsonlRenderer.Render(first), CheckRunJsonlRenderer.Render(second));
    }

    [Theory]
    [InlineData("C:\\repo\\file.txt")]
    [InlineData("\\\\server\\file.txt")]
    [InlineData("/repo/file.txt")]
    [InlineData("C:relative.txt")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    public void Normalize_RejectsAbsoluteAndEscapingPaths(string path)
    {
        var run = new CheckRun(
            "check",
            null,
            false,
            1,
            [new CheckDiagnostic("ERR", "failure", path)]);

        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(run));

        var evidenceRun = run with
        {
            Diagnostics = [new CheckDiagnostic("ERR", "failure", evidencePath: path)],
        };
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(evidenceRun));
    }

    [Fact]
    public void Normalize_RejectsEscapingEvidencePath()
    {
        var run = new CheckRun(
            "check",
            null,
            false,
            1,
            [new CheckDiagnostic("ERR", "failure", evidencePath: "artifacts/../../outside.log")]);

        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(run));
    }

    [Fact]
    public void Normalize_RejectsMultilineSummary()
    {
        var run = new CheckRun(
            "check",
            null,
            false,
            1,
            [new CheckDiagnostic("ERR", "first\nsecond")]);

        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(run));
    }

    [Fact]
    public void Normalize_RejectsMultilineIdentityAndDiagnosticFields()
    {
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check\n::error", null, true, 0, [])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", "profile\r", true, 0, [])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR\n::error", "failure")])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR", "failure", "src/file\n.cs")])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR", "failure", evidencePath: "artifacts/log\r.txt")])));
    }

    [Fact]
    public void Normalize_RejectsUnboundedFieldsAndInvalidCounters()
    {
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun(new string('c', 257), null, true, 0, [])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", new string('p', 257), true, 0, [])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic(new string('e', 129), "ok")])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR", new string('s', 1_025))])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR", "ok", new string('p', 513))])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, false, 0, [new CheckDiagnostic("ERR", "ok", evidencePath: new string('e', 513))])));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, true, 0, [], new Dictionary<string, int> { [new string('n', 257)] = 1 })));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, true, 0, [], new Dictionary<string, int> { ["negative"] = -1 })));
        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(new CheckRun("check", null, true, 0, [], Enumerable.Range(0, 101).ToDictionary(index => $"counter-{index}", _ => 1))));
    }

    [Fact]
    public void Normalize_RejectsUnsupportedSchemaVersion()
    {
        var run = new CheckRun("check", null, true, 0, []) { SchemaVersion = 2 };

        Assert.Throws<ArgumentException>(() => CheckRunNormalizer.Normalize(run));
    }

    [Fact]
    public void Renderers_EmitOneSummaryAndRetainOriginalExitCode()
    {
        var run = new CheckRun(
            "check",
            "profile",
            false,
            23,
            [new CheckDiagnostic("ERR", "failure")]);

        var text = CheckRunTextRenderer.Render(run, failuresOnly: true);
        var github = CheckRunGitHubRenderer.Render(run, failuresOnly: true);
        var jsonl = CheckRunJsonlRenderer.Render(run, failuresOnly: true);

        Assert.Equal(1, text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("summary:", StringComparison.Ordinal)));
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(2, github.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("exit code 23", text);
        Assert.Contains("exit code 23", github);
        Assert.Equal(23, JsonDocument.Parse(jsonl).RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void TextRenderer_BoundsFailureDiagnosticsAndKeepsSummary()
    {
        var diagnostics = Enumerable.Range(0, CheckRunNormalizer.MaxDiagnostics + 20)
            .Select(index => new CheckDiagnostic($"E{index:000}", "failure"))
            .ToArray();
        var run = new CheckRun("check", null, false, 9, diagnostics);

        var output = CheckRunTextRenderer.Render(run, failuresOnly: true);

        Assert.Equal(CheckRunNormalizer.MaxDiagnostics + 1, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("summary: check failed (exit code 9)", output);
        Assert.DoesNotContain("E120", output);
    }

    [Fact]
    public void GitHubRenderer_EscapesCommandData()
    {
        var escaped = CheckRunGitHubRenderer.Escape("100% done\r\nfield:value,ok");

        Assert.Equal("100%25 done%0D%0Afield%3Avalue%2Cok", escaped);
    }

    [Fact]
    public void GitHubRenderer_EmitsRelativeEvidenceReference()
    {
        var run = new CheckRun(
            "check",
            null,
            false,
            1,
            [new CheckDiagnostic("ERR", "failure", "src/file.cs", 7, "artifacts/log.txt")]);

        var output = CheckRunGitHubRenderer.Render(run);

        Assert.Contains("file=src/file.cs,line=7", output);
        Assert.Contains("[evidence%3A artifacts/log.txt]", output);
    }

    [Fact]
    public void Success_EmitsOneSummary()
    {
        var run = new CheckRun("check", "fast", true, 0, []);

        var text = CheckRunTextRenderer.Render(run);
        var github = CheckRunGitHubRenderer.Render(run);

        Assert.Equal("summary: check [fast] succeeded (exit code 0)\n", text);
        Assert.Equal("::notice title=check::check [fast] succeeded (exit code 0)\n", github);
    }
}
