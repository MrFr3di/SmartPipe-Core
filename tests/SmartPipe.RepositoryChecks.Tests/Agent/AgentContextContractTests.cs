using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Agent;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Profiles;
using SmartPipe.RepositoryChecks.Reporting;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Agent;

public sealed class AgentContextContractTests
{
    [Fact]
    public async Task PlanLoader_RejectsMissingActiveDirectoryAsPlanFailure()
    {
        using var fixture = new RepositoryTestDirectory();

        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLoader_RejectsMissingMarkers()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePlan(fixture, markerBody: "{}");

        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLoader_RejectsDuplicateMarkersAndMalformedJson()
    {
        using var duplicate = new RepositoryTestDirectory();
        WritePlan(duplicate, markerBody: "<!-- smartpipe-agent-context:v1:start -->\n```json\n{}\n```\n<!-- smartpipe-agent-context:v1:start -->\n<!-- smartpipe-agent-context:v1:end -->");
        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            duplicate.Path, TestContext.Current.CancellationToken));

        using var malformed = new RepositoryTestDirectory();
        WritePlan(malformed, markerBody: "<!-- smartpipe-agent-context:v1:start -->\n```json\n{\n```\n<!-- smartpipe-agent-context:v1:end -->");
        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            malformed.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLoader_RejectsMultipleActivePlans()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        fixture.Write(".agent/exec-plans/active/second.md", "duplicate");

        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLoader_RejectsUnsafeScopeAndTrackedPlanEscape()
    {
        using var fixture = new RepositoryTestDirectory();
        var document = ValidPlan() with
        {
            TrackedPlan = new AgentTrackedPlanDefinition("../outside.md", "EPIC SP220-05"),
            Tasks = [ValidTask() with { AllowedPaths = ["eng/../outside/**"] }],
        };
        WritePlan(fixture, JsonSerializer.Serialize(document, AgentJsonContext.Default.AgentPlanDocument));

        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PlanLoader_RejectsUnboundedTaskPayload()
    {
        using var fixture = new RepositoryTestDirectory();
        var document = ValidPlan() with
        {
            Tasks = [ValidTask() with { Title = new string('x', 513) }],
        };
        WritePlan(fixture, JsonSerializer.Serialize(document, AgentJsonContext.Default.AgentPlanDocument));

        await Assert.ThrowsAsync<AgentPlanException>(() => new ActiveExecPlanLoader().LoadAsync(
            fixture.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContextBuilder_EmitsCompactJsonForMaximumTaskPayload()
    {
        using var fixture = new RepositoryTestDirectory();
        var task = ValidTask() with
        {
            Title = new string('t', 256),
            AllowedPaths = Enumerable.Range(0, 32)
                .Select(index => $"eng/SmartPipe.RepositoryChecks/Agent/p{index:D2}/**")
                .ToArray(),
            Contracts = Enumerable.Range(0, 16)
                .Select(index => $"c{index:D2}" + new string('c', 253))
                .ToArray(),
        };
        WritePlan(fixture, JsonSerializer.Serialize(
            ValidPlan() with { Tasks = [task] },
            AgentJsonContext.Default.AgentPlanDocument));
        var sha = new string('a', 40);
        var runner = new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, string.Empty, string.Empty));

        var snapshot = await new AgentContextBuilder(runner).BuildAsync(
            fixture.Path, "SP220-05", "T25", TestContext.Current.CancellationToken);
        var json = AgentJsonSerializer.Serialize(snapshot.Context);

        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, 32 * 1024);
    }

    [Fact]
    public async Task ContextBuilder_ProducesCanonicalJsonWithoutAbsoluteRoot()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("docs/plans/2.2.0-extension-architecture.md", "## EPIC SP220-05 — Health Checks\n");
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        var sha = new string('a', 40);
        var runner = new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, string.Empty, string.Empty));

        var snapshot = await new AgentContextBuilder(runner).BuildAsync(
            fixture.Path, "SP220-05", "T25", TestContext.Current.CancellationToken);
        var json = AgentJsonSerializer.Serialize(snapshot.Context);

        Assert.DoesNotContain(fixture.Path, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\r", json);
        Assert.EndsWith("\n", json);
        Assert.Equal(1, json.Count(character => character == '\n'));
        Assert.Equal("docs/plans/2.2.0-extension-architecture.md", snapshot.Context.Read.Path);
        Assert.Equal("EPIC SP220-05", snapshot.Context.Read.Section);
        Assert.Equal(sha, snapshot.Context.Head);
        Assert.Equal("main", snapshot.Context.Branch);
    }

    [Theory]
    [InlineData("UNKNOWN", "T25")]
    [InlineData("SP220-05", "t25")]
    [InlineData("SP220-05", "T 25")]
    public void Parser_RejectsUnknownOrNonCanonicalIdentity(string epic, string task)
    {
        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["agent-context", "--epic", epic, "--task", task, "--format", "json"]));

        Assert.Contains("canonical", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parser_RejectsDuplicateAndUnknownAgentOptions()
    {
        Assert.Equal("Duplicate option '--task'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["verify-task", "--epic", "SP220-05", "--task", "T25", "--task", "T25"])).Message);
        Assert.Equal("Unknown option '--network'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["evidence", "--epic", "SP220-05", "--network", "true"])).Message);
    }

    [Fact]
    public void Parser_DefaultsAgentRootAndFormats()
    {
        var context = Assert.IsType<AgentContextOptions>(CommandLineParser.Parse(
            ["agent-context", "--epic", "SP220-05", "--task", "T25", "--format", "json"]));
        var verify = Assert.IsType<VerifyTaskOptions>(CommandLineParser.Parse(
            ["verify-task", "--epic", "SP220-05", "--task", "T25"]));
        var evidence = Assert.IsType<AgentEvidenceOptions>(CommandLineParser.Parse(
            ["evidence", "--epic", "SP220-05", "--format", "json"]));

        Assert.Equal(Path.GetFullPath(Directory.GetCurrentDirectory()), context.RepositoryRoot);
        Assert.Equal(ProfileOutputFormat.Jsonl, context.Format);
        Assert.Equal(ProfileOutputFormat.Text, verify.Format);
        Assert.Equal(ProfileOutputFormat.Jsonl, evidence.Format);
    }

    [Fact]
    public async Task RepositoryStateReader_PreservesSpacesSortsPathsAndFingerprintsDeterministically()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("src/with space.txt", "changed");
        fixture.Write("untracked file.txt", "new");
        var sha = new string('b', 40);
        const string status = " M src/with space.txt\0?? untracked file.txt\0 D deleted.txt\0";
        var first = await new AgentRepositoryStateReader(new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, status, string.Empty))).CaptureAsync(
                fixture.Path, TestContext.Current.CancellationToken);
        var second = await new AgentRepositoryStateReader(new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, status, string.Empty))).CaptureAsync(
                fixture.Path, TestContext.Current.CancellationToken);

        Assert.Equal(["deleted.txt", "src/with space.txt", "untracked file.txt"], first.ChangedPaths);
        Assert.False(first.Clean);
        Assert.Equal(first.TreeFingerprint, second.TreeFingerprint);
    }

    [Fact]
    public async Task RepositoryStateReader_ChangesFingerprintWhenContentChanges()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("changed.txt", "before");
        var sha = new string('b', 40);
        var first = await new AgentRepositoryStateReader(new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, " M changed.txt\0", string.Empty))).CaptureAsync(
                fixture.Path, TestContext.Current.CancellationToken);

        fixture.Write("changed.txt", "after");
        var second = await new AgentRepositoryStateReader(new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, " M changed.txt\0", string.Empty))).CaptureAsync(
                fixture.Path, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.TreeFingerprint, second.TreeFingerprint);
    }

    [Fact]
    public async Task RepositoryStateReader_RejectsRenameStatus()
    {
        using var fixture = new RepositoryTestDirectory();
        var runner = new FakeProcessRunner(
            new ProcessResult(0, new string('f', 40) + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, "R  renamed.txt\0old name.txt\0", string.Empty));

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(() => new AgentRepositoryStateReader(runner).CaptureAsync(
            fixture.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
    }

    [Fact]
    public async Task RepositoryStateReader_RejectsCopyStatus()
    {
        using var fixture = new RepositoryTestDirectory();
        var runner = new FakeProcessRunner(
            new ProcessResult(0, new string('f', 40) + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, "C  copied.txt\0source.txt\0", string.Empty));

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(() => new AgentRepositoryStateReader(runner).CaptureAsync(
            fixture.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
    }

    [Fact]
    public async Task ContextBuilder_RejectsDirtyPathsOutsideEpicUnion()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        var runner = new FakeProcessRunner(
            new ProcessResult(0, new string('a', 40) + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, " M README.md\0", string.Empty));

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(() => new AgentContextBuilder(runner).BuildAsync(
            fixture.Path, "SP220-05", "T25", TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.RepositorySnapshotMismatch, exception.ExitCode);
    }

    [Theory]
    [InlineData(0, "satisfied")]
    [InlineData(1, "unsatisfied")]
    public async Task PrerequisiteReader_MapsAncestorExitCodes(int exitCode, string status)
    {
        using var fixture = new RepositoryTestDirectory();
        var runner = new FakeProcessRunner(new ProcessResult(exitCode, string.Empty, string.Empty));
        var result = await new AgentPrerequisiteReader(runner).ReadAsync(
            fixture.Path, new AgentPrerequisiteDefinition("SP220-04", new string('c', 40)),
            TestContext.Current.CancellationToken);

        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task TaskVerifier_AppendsBoundedStaleFailureWhenProfileMutatesState()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("docs/plans/2.2.0-extension-architecture.md", "## EPIC SP220-05 — Health Checks\n");
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        fixture.Write("eng/verification-profiles.json", ProfileJson());
        var sha = new string('d', 40);
        var runner = new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, " M eng/SmartPipe.RepositoryChecks/Agent/state.txt\0", string.Empty),
            new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, string.Empty, string.Empty));
        var verifier = new AgentTaskVerifier(
            new AgentContextBuilder(runner),
            (_, _, _) =>
            {
                fixture.Write("eng/SmartPipe.RepositoryChecks/Agent/state.txt", "mutated");
                return Task.FromResult(new VerificationProfileRunResult(
                    [new CheckRun("verify-lock-files", "sp220-05", true, 0, [])], 0));
            });

        var result = await verifier.VerifyAsync(
            fixture.Path, "SP220-05", "T25", ProfileOutputFormat.Jsonl, false,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.RepositorySnapshotMismatch, result.ExitCode);
        var stale = Assert.Single(result.CheckRuns, run => !run.Success);
        Assert.Equal("SPAGENT001", Assert.Single(stale.Diagnostics).Code);
    }

    [Fact]
    public async Task TaskVerifier_PassesDeclaredProfileOrderWhenStateIsStable()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        fixture.Write("eng/verification-profiles.json", VerificationProfileManifestLoader.Serialize(new VerificationProfileManifest
        {
            SchemaVersion = 1,
            Profiles = [new VerificationProfile("sp220-05", ["verify-package-projects", "verify-lock-files"])],
        }));
        var sha = new string('a', 40);
        var runner = new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty), new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty), new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty));
        IReadOnlyList<string>? declaredOrder = null;
        var verifier = new AgentTaskVerifier(new AgentContextBuilder(runner), (_, profile, _) =>
        {
            declaredOrder = profile.Checks;
            return Task.FromResult(new VerificationProfileRunResult(
                [new CheckRun("verify-package-projects", profile.Name, true, 0, []), new CheckRun("verify-lock-files", profile.Name, true, 0, [])], 0));
        });

        var result = await verifier.VerifyAsync(
            fixture.Path, "SP220-05", "T25", ProfileOutputFormat.Text, failuresOnly: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Equal(["verify-package-projects", "verify-lock-files"], declaredOrder);
        Assert.Equal(2, result.CheckRuns.Count);
    }

    [Fact]
    public async Task ContextBuilder_RejectsBaseRefWhenResolvedCommitDiffers()
    {
        using var fixture = new RepositoryTestDirectory();
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        var runner = new FakeProcessRunner(
            new ProcessResult(0, new string('a', 40) + "\n", string.Empty),
            new ProcessResult(0, "main\n", string.Empty),
            new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, new string('b', 40) + "\n", string.Empty));

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(() => new AgentContextBuilder(runner).BuildAsync(
            fixture.Path, "SP220-05", "T25", TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.RepositorySnapshotMismatch, exception.ExitCode);
    }

    [Fact]
    public async Task Evidence_ContainsOnlyLocalFactsAndPerCheckCodes()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("docs/plans/2.2.0-extension-architecture.md", "## EPIC SP220-05 — Health Checks\n");
        WritePlan(fixture, JsonSerializer.Serialize(ValidPlan(), AgentJsonContext.Default.AgentPlanDocument));
        fixture.Write("eng/verification-profiles.json", ProfileJson());
        var sha = new string('e', 40);
        var runner = new FakeProcessRunner(
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty), new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty),
            new ProcessResult(0, sha + "\n", string.Empty), new ProcessResult(0, "main\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty), new ProcessResult(0, new string('a', 40) + "\n", string.Empty), new ProcessResult(0, string.Empty, string.Empty));
        var service = new AgentEvidenceService(
            new AgentContextBuilder(runner),
            (_, _, _) => Task.FromResult(new VerificationProfileRunResult(
                [new CheckRun("check", "sp220-05", false, 23, [new CheckDiagnostic("E1", "bad")], new Dictionary<string, int> { ["violations"] = 1 })], 23)));

        var result = await service.CollectAsync(fixture.Path, "SP220-05", TestContext.Current.CancellationToken);
        var json = AgentJsonSerializer.Serialize(result.Evidence);

        Assert.Equal("failed", result.Evidence.Status);
        Assert.Equal(23, result.Evidence.ExitCode);
        Assert.Contains("\"check\":\"check\"", json, StringComparison.Ordinal);
        Assert.Contains("\"counters\":{\"violations\":1}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ci", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pull", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("merge", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("release", json, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentPlanDocument ValidPlan() => new()
    {
        SchemaVersion = 1,
        Epic = "SP220-05",
        BaseRef = "origin/sp220/checkpoint-c",
        BaseCommit = new string('a', 40),
        Prerequisites = [new AgentPrerequisiteDefinition("SP220-04", new string('a', 40))],
        TrackedPlan = new AgentTrackedPlanDefinition("docs/plans/2.2.0-extension-architecture.md", "EPIC SP220-05"),
        Tasks = [ValidTask()],
    };

    private static AgentTaskDefinition ValidTask() => new()
    {
        Id = "T25",
        Title = "Agent context task verification and evidence",
        AllowedPaths = ["eng/SmartPipe.RepositoryChecks/Agent/**"],
        Contracts = ["active ExecPlan is the only task context source"],
        VerificationProfile = "sp220-05",
    };

    private static string ProfileJson() => VerificationProfileManifestLoader.Serialize(new VerificationProfileManifest
    {
        SchemaVersion = 1,
        Profiles = [new VerificationProfile("sp220-05", ["verify-lock-files"])],
    });

    private static void WritePlan(RepositoryTestDirectory fixture, string markerBody)
    {
        fixture.Write("docs/plans/2.2.0-extension-architecture.md", "## EPIC SP220-05 — Health Checks\n");
        if (!markerBody.Contains("```json", StringComparison.Ordinal))
        {
            markerBody = "```json\n" + markerBody + "\n```";
        }

        fixture.Write(
            ".agent/exec-plans/active/plan.md",
            "# active\n<!-- smartpipe-agent-context:v1:start -->\n" + markerBody + "\n<!-- smartpipe-agent-context:v1:end -->\n");
    }
}
