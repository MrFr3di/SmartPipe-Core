using System.Globalization;
using System.Text.Json.Nodes;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Tests.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.Commands;

public sealed class BaselineOrchestrationTests
{
    [Fact]
    public async Task CaptureThenOfflineVerify_Passes()
    {
        using var scenario = new BaselineScenario();

        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var result = await scenario.VerifyAsync();

        Assert.True(result.Success, result.Format());
        Assert.True(File.Exists(scenario.ManifestPath));
    }

    [Fact]
    public async Task Capture_WritesDeterministicBaselineReport()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var reportPath = Path.Combine(scenario.BaselinePath, "baseline-report.md");
        var first = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);

        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var second = await File.ReadAllTextAsync(reportPath, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        Assert.Contains("# SmartPipe 2.1.2 baseline report", first, StringComparison.Ordinal);
        Assert.Contains($"Capture commit: `{BaselineScenario.Sha}`", first, StringComparison.Ordinal);
        Assert.Contains("| Workflow | Run ID | Head SHA | URL |", first, StringComparison.Ordinal);
        Assert.Contains("| CI | 1 |", first, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_PersistsExactCaptureAndWorkflowCommitIdentity()
    {
        using var scenario = new BaselineScenario();

        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            scenario.ManifestPath, TestContext.Current.CancellationToken))!.AsObject();

        Assert.Equal(BaselineScenario.Sha, root["repository"]!["captureCommitSha"]!.GetValue<string>());
        Assert.All(root["repository"]!["requiredWorkflows"]!.AsArray(), workflow =>
            Assert.Equal(BaselineScenario.Sha, workflow!["headSha"]!.GetValue<string>()));
        Assert.Null(root["repository"]!["commitSha"]);
    }

    [Fact]
    public async Task DescendantGovernanceHead_VerifiesByCaptureCommitAncestry()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        scenario.ProcessRunner.CurrentHeadSha = new string('d', 40);
        scenario.ProcessRunner.MergeBaseExitCode = 0;
        scenario.ProcessRunner.Requests.Clear();

        var result = await scenario.VerifyAsync();

        Assert.True(result.Success, result.Format());
        Assert.Contains(scenario.ProcessRunner.Requests, request => request.FileName == "git"
            && request.Arguments.Skip(2).SequenceEqual(
                ["merge-base", "--is-ancestor", BaselineScenario.Sha, "HEAD"]));
        Assert.DoesNotContain(scenario.ProcessRunner.Requests, request => request.FileName == "git"
            && request.Arguments.Skip(2).SequenceEqual(["rev-parse", "HEAD"]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public async Task UnrelatedOrMissingCaptureCommit_FailsClosed(int mergeBaseExitCode)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        scenario.ProcessRunner.MergeBaseExitCode = mergeBaseExitCode;
        scenario.ProcessRunner.Requests.Clear();

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SPB003");
        Assert.Contains(scenario.ProcessRunner.Requests, request => request.FileName == "git"
            && request.Arguments.Skip(2).SequenceEqual(
                ["merge-base", "--is-ancestor", BaselineScenario.Sha, "HEAD"]));
    }

    [Fact]
    public async Task WellFormedMutatedCaptureCommit_FailsAncestryCheck()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var mutatedSha = new string('b', 40);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(
            scenario.ManifestPath, TestContext.Current.CancellationToken))!.AsObject();
        root["repository"]!["captureCommitSha"] = mutatedSha;
        foreach (var workflow in root["repository"]!["requiredWorkflows"]!.AsArray())
        {
            workflow!["headSha"] = mutatedSha;
        }

        await File.WriteAllTextAsync(
            scenario.ManifestPath, root.ToJsonString(), TestContext.Current.CancellationToken);
        scenario.ProcessRunner.MergeBaseExitCode = 128;

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SPB003");
    }

    [Fact]
    public async Task BaselineReportMutation_FailsVerification()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(scenario.BaselinePath, "baseline-report.md"),
            "mutated\n",
            TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB006"
            && item.Message.Contains("report", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingBaselineReport_FailsVerification()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(scenario.BaselinePath, "baseline-report.md"));

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB005"
            && item.Message == "Required baseline report is missing: baseline-report.md");
    }

    [Theory]
    [InlineData("mixed-sha")]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("extra-pending")]
    [InlineData("extra-failed")]
    public async Task Capture_RejectsInvalidRawGitHubWorkflowEvidence(string mutation)
    {
        using var scenario = new BaselineScenario();
        scenario.WriteWorkflowEvidence(mutation);

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => scenario.CaptureAsync(TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(scenario.BaselinePath));
    }

    [Fact]
    public async Task Capture_RejectsDuplicateSuccessfulWorkflowEvidenceAsAmbiguous()
    {
        using var scenario = new BaselineScenario();
        scenario.WriteWorkflowEvidence("duplicate-success");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => scenario.CaptureAsync(TestContext.Current.CancellationToken));

        Assert.Contains("exactly one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCapture_DoesNotReplaceExistingBaseline()
    {
        using var scenario = new BaselineScenario();
        Directory.CreateDirectory(scenario.BaselinePath);
        var marker = Path.Combine(scenario.BaselinePath, "keep.txt");
        await File.WriteAllTextAsync(marker, "old", TestContext.Current.CancellationToken);
        scenario.SignatureVerifier.Failure = new InvalidDataException("signature failure");

        await Assert.ThrowsAsync<InvalidDataException>(() => scenario.CaptureAsync(TestContext.Current.CancellationToken));

        Assert.Equal("old", await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(scenario.BaselinePath)!, ".2.1.2.capture-*"));
    }

    [Fact]
    public async Task SuccessfulCapture_DoesNotFailWhenBackupCleanupFails()
    {
        using var scenario = new BaselineScenario(failBackupCleanup: true);
        Directory.CreateDirectory(scenario.BaselinePath);
        await File.WriteAllTextAsync(Path.Combine(scenario.BaselinePath, "old.txt"), "old", TestContext.Current.CancellationToken);

        await scenario.CaptureAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(scenario.ManifestPath));
        Assert.False(File.Exists(Path.Combine(scenario.BaselinePath, "old.txt")));
    }

    [Fact]
    public async Task ManifestMutation_Fails()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(scenario.ManifestPath, TestContext.Current.CancellationToken))!.AsObject();
        json["targetRelease"] = "2.2.1";
        await File.WriteAllTextAsync(scenario.ManifestPath, json.ToJsonString(), TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB001");
    }

    [Fact]
    public async Task PackageByteMutation_FailsBeforeParsing()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(scenario.PackagesPath, "SmartPipe.Core.2.1.2.nupkg"), "mutated", TestContext.Current.CancellationToken);
        scenario.SignatureVerifier.VerifiedPaths.Clear();

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB007" && item.Message.Contains("SmartPipe.Core", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.SignatureVerifier.VerifiedPaths, path => Path.GetFileName(path) == "SmartPipe.Core.2.1.2.nupkg");
    }

    [Fact]
    public async Task PublicApiMutation_Fails()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(scenario.PublicApiPath, "\nNew.Api", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB014");
    }

    [Fact]
    public async Task DirectPackageReferenceMutation_Fails()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scenario.ProjectPath, "<Project><ItemGroup><PackageReference Include=\"Changed\" Version=\"2.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB015");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/codeql.yml")]
    [InlineData(".github/workflows/dependency-review.yml")]
    public async Task WorkflowReleaseBranchRemoval_Fails(string workflowRelativePath)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(scenario.Root, workflowRelativePath.Replace('/', Path.DirectorySeparatorChar)), "on:\n  push:\n    branches: [ main ]\n", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB016");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", "push")]
    [InlineData(".github/workflows/ci.yml", "pull_request")]
    [InlineData(".github/workflows/codeql.yml", "push")]
    [InlineData(".github/workflows/codeql.yml", "pull_request")]
    [InlineData(".github/workflows/dependency-review.yml", "pull_request")]
    public async Task RequiredWorkflowEventReleaseBranchMutation_Fails(
        string workflowRelativePath,
        string mutatedEvent)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var events = workflowRelativePath.EndsWith("dependency-review.yml", StringComparison.Ordinal)
            ? new[] { "pull_request" }
            : new[] { "push", "pull_request" };
        var yaml = "on:\n" + string.Concat(events.Select(eventName =>
            $"  {eventName}:\n    branches: [ main{(eventName == mutatedEvent ? string.Empty : ", release/2.2.0")} ]\n"));
        await File.WriteAllTextAsync(
            Path.Combine(scenario.Root, workflowRelativePath.Replace('/', Path.DirectorySeparatorChar)),
            yaml,
            TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB016"
            && item.Actual == workflowRelativePath);
    }

    [Fact]
    public async Task OfflineVerify_PerformsZeroPackageFetches()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var callsAfterCapture = scenario.Fetcher.FetchCount;

        var result = await scenario.VerifyAsync();

        Assert.True(result.Success, result.Format());
        Assert.Equal(callsAfterCapture, scenario.Fetcher.FetchCount);
    }

    [Fact]
    public async Task Diagnostics_AreExactStableAndCultureIndependent()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(scenario.BaselinePath, "baseline-report.md"));
        await File.WriteAllTextAsync(
            scenario.WorkflowPath,
            "on:\n  push:\n    branches: [ main ]\n  pull_request:\n    branches: [ main ]\n",
            TestContext.Current.CancellationToken);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = (await scenario.VerifyAsync()).Format();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var second = (await scenario.VerifyAsync()).Format();

            const string expected = """
                BASELINE VERIFICATION FAILED
                [SPB005] Required baseline report is missing: baseline-report.md
                [SPB016] Workflow release branch policy mismatch: CI
                  expected: release/2.2.0
                  actual:   .github/workflows/ci.yml
                """;
            Assert.Equal(expected, first);
            Assert.Equal(expected, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task UnknownSnapshotFile_IsIgnored()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(scenario.BaselinePath, "unknown.json"), "{\"changed\":true}", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.True(result.Success, result.Format());
    }

    [Fact]
    public async Task ManifestPathTraversal_IsRejectedImmediately()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var text = await File.ReadAllTextAsync(scenario.ManifestPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scenario.ManifestPath, text.Replace("eng/baselines/2.1.2/public-api.json", "../public-api.json", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        scenario.SignatureVerifier.VerifiedPaths.Clear();

        var result = await scenario.VerifyAsync();

        Assert.Collection(result.Diagnostics, item => Assert.Equal("SPB001", item.Code));
        Assert.Empty(scenario.SignatureVerifier.VerifiedPaths);
    }

    [Theory]
    [InlineData("defaultBranch", "wrong")]
    [InlineData("solutionPath", "Other.slnx")]
    [InlineData("duplicateSnapshots", "")]
    public async Task UnsafeManifestContract_StopsBeforeProcessesAndPackages(string mutation, string value)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(scenario.ManifestPath, TestContext.Current.CancellationToken))!.AsObject();
        if (mutation == "duplicateSnapshots")
        {
            root["packageAssets"]!["path"] = root["publicApi"]!["path"]!.GetValue<string>();
        }
        else
        {
            root["repository"]![mutation] = value;
        }

        await File.WriteAllTextAsync(scenario.ManifestPath, root.ToJsonString(), TestContext.Current.CancellationToken);
        scenario.ProcessRunner.Requests.Clear();
        scenario.SignatureVerifier.VerifiedPaths.Clear();

        var result = await scenario.VerifyAsync();

        Assert.Collection(result.Diagnostics, diagnostic => Assert.Equal("SPB001", diagnostic.Code));
        Assert.Empty(scenario.ProcessRunner.Requests);
        Assert.Empty(scenario.SignatureVerifier.VerifiedPaths);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/codeql.yml")]
    [InlineData(".github/workflows/dependency-review.yml")]
    public async Task WorkflowReleaseBranchInCommentOrEnvironment_DoesNotSatisfyPolicy(string workflowRelativePath)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        var path = Path.Combine(scenario.Root, workflowRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, "on:\n  pull_request:\n    branches: [ main ]\nenv:\n  NOTE: release/2.2.0 # unrelated\njobs:\n  check:\n    steps:\n      - run: echo release/2.2.0\n", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB016");
    }

    [Fact]
    public async Task GitFailure_IsDiagnosticAndIndependentChecksContinue()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        scenario.ProcessRunner.GitFailure = new ProcessRunnerException(ProcessFailureKind.StartFailure, "git unavailable");
        await File.AppendAllTextAsync(scenario.PublicApiPath, "Changed.Api\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scenario.WorkflowPath, "on:\n  push:\n    branches: [ main ]\n", TestContext.Current.CancellationToken);

        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB003");
        Assert.Contains(result.Diagnostics, item => item.Code == "SPB014");
        Assert.Contains(result.Diagnostics, item => item.Code == "SPB016");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public async Task InvalidGlobalJson_IsDiagnosticAndIndependentChecksContinue(string state)
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        if (state == "missing")
        {
            File.Delete(scenario.GlobalJsonPath);
        }
        else
        {
            await File.WriteAllTextAsync(scenario.GlobalJsonPath, "{", TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(scenario.WorkflowPath, "on:\n  push:\n    branches: [ main ]\n", TestContext.Current.CancellationToken);
        var result = await scenario.VerifyAsync();

        Assert.Contains(result.Diagnostics, item => item.Code == "SPB004");
        Assert.Contains(result.Diagnostics, item => item.Code == "SPB016");
    }

    [Fact]
    public async Task CanceledGitProcess_PropagatesCancellation()
    {
        using var scenario = new BaselineScenario();
        await scenario.CaptureAsync(TestContext.Current.CancellationToken);
        scenario.ProcessRunner.GitFailure = new ProcessRunnerException(ProcessFailureKind.Canceled, "git canceled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.VerifyAsync());
    }

    [Fact]
    public async Task CanceledGitProcessDuringCapture_PropagatesAndCleansTemporaryDirectory()
    {
        using var scenario = new BaselineScenario();
        scenario.ProcessRunner.GitFailure = new ProcessRunnerException(ProcessFailureKind.Canceled, "git canceled");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scenario.CaptureAsync(TestContext.Current.CancellationToken));

        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(scenario.BaselinePath)!, ".2.1.2.capture-*"));
        Assert.False(Directory.Exists(scenario.BaselinePath));
    }

    [Fact]
    public async Task Cancellation_CleansTemporaryCaptureDirectory()
    {
        using var scenario = new BaselineScenario();
        using var cancellation = new CancellationTokenSource();
        scenario.Fetcher.AfterCopy = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.CaptureAsync(cancellation.Token));

        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(scenario.BaselinePath)!, ".2.1.2.capture-*"));
        Assert.False(Directory.Exists(scenario.BaselinePath));
    }

    private sealed class BaselineScenario : IDisposable
    {
        internal const string Sha = "8e79902d22de714f493582946f7c260462b0895e";
        private readonly List<SyntheticNuGetPackage> _packages = [];
        private readonly ScenarioProcessRunner _processRunner;
        private readonly BaselineCaptureService _capture;
        private readonly BaselineVerificationService _verify;

        public BaselineScenario(bool failBackupCleanup = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "SmartPipe.BaselineScenario", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            PackagesPath = Path.Combine(Root, "packages");
            Directory.CreateDirectory(PackagesPath);
            BaselinePath = Path.Combine(Root, "eng", "baselines", "2.1.2");
            ManifestPath = Path.Combine(BaselinePath, "manifest.json");
            ProjectPath = Write("src/Fixture/Fixture.csproj", "<Project><ItemGroup><PackageReference Include=\"Example\" Version=\"1.0.0\" /></ItemGroup></Project>");
            PublicApiPath = Write("src/Fixture/PublicAPI.Shipped.txt", "Fixture.Api\n");
            WorkflowPath = Write(".github/workflows/ci.yml", "on:\n  push:\n    branches: [ main, release/2.2.0 ]\n  pull_request:\n    branches: [ main, release/2.2.0 ]\n");
            Write(".github/workflows/codeql.yml", "on:\n  push:\n    branches: [ main, release/2.2.0 ]\n  pull_request:\n    branches: [ main, release/2.2.0 ]\n");
            Write(".github/workflows/dependency-review.yml", "on:\n  pull_request:\n    branches: [ main, release/2.2.0 ]\n");
            Write("SmartPipe.Core.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/Fixture/Fixture.csproj\" /></Folder></Solution>");
            GlobalJsonPath = Write("global.json", "{\"sdk\":{\"version\":\"10.0.302\"}}");
            WorkflowEvidencePath = Path.Combine(Root, "workflow.json");
            WriteWorkflowEvidence("valid");

            var sources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in new[] { "SmartPipe.Core", "SmartPipe.Extensions", "SmartPipe.Extensions.Json" })
            {
                var package = SyntheticNuGetPackage.Create(id, entries: [("lib/net10.0/readme.txt", "fixture"u8.ToArray())]);
                _packages.Add(package);
                sources.Add(id, package.Path);
            }

            Fetcher = new CopyingFetcher(sources);
            SignatureVerifier = new RecordingSignatureVerifier();
            _processRunner = new ScenarioProcessRunner(Sha, ProjectPath);
            var repository = new BaselineRepositorySnapshotReader(_processRunner, "dotnet");
            _verify = new BaselineVerificationService(_processRunner, "git", SignatureVerifier, new NuGetPackageReader(), repository);
            _capture = new BaselineCaptureService(
                _processRunner, "git", "dotnet", Fetcher, SignatureVerifier, new NuGetPackageReader(), repository, _verify,
                failBackupCleanup ? _ => throw new IOException("backup cleanup failed") : null);
        }

        public string Root { get; }
        public string PackagesPath { get; }
        public string BaselinePath { get; }
        public string ManifestPath { get; }
        public string ProjectPath { get; }
        public string PublicApiPath { get; }
        public string WorkflowPath { get; }
        public string GlobalJsonPath { get; }
        public string WorkflowEvidencePath { get; }
        public CopyingFetcher Fetcher { get; }
        public RecordingSignatureVerifier SignatureVerifier { get; }
        public ScenarioProcessRunner ProcessRunner => _processRunner;

        public void WriteWorkflowEvidence(string mutation)
        {
            var ciSha = mutation == "mixed-sha" ? new string('a', 40) : Sha;
            var ciStatus = mutation == "pending" ? "in_progress" : "completed";
            var ciConclusion = mutation == "pending" ? string.Empty : mutation == "failed" ? "failure" : "success";
            var extra = mutation switch
            {
                "extra-pending" => $$"""
                    ,
                      {"databaseId":4,"workflowName":"Other","headSha":"{{Sha}}","status":"in_progress","conclusion":"","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/4","event":"push","createdAt":"2026-07-17T00:03:00Z"}
                    """,
                "extra-failed" => $$"""
                    ,
                      {"databaseId":4,"workflowName":"Other","headSha":"{{Sha}}","status":"completed","conclusion":"failure","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/4","event":"push","createdAt":"2026-07-17T00:03:00Z"}
                    """,
                "duplicate-success" => $$"""
                    ,
                      {"databaseId":4,"workflowName":"CI","headSha":"{{Sha}}","status":"completed","conclusion":"success","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/4","event":"pull_request","createdAt":"2026-07-17T00:03:00Z"}
                    """,
                _ => string.Empty,
            };
            var evidence = $$"""
                [
                  {"databaseId":1,"workflowName":"CI","headSha":"{{ciSha}}","status":"{{ciStatus}}","conclusion":"{{ciConclusion}}","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/1","event":"push","createdAt":"2026-07-17T00:00:00Z"},
                  {"databaseId":2,"workflowName":"CodeQL","headSha":"{{Sha}}","status":"completed","conclusion":"success","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/2","event":"push","createdAt":"2026-07-17T00:01:00Z"},
                  {"databaseId":3,"workflowName":"Dependency Review","headSha":"{{Sha}}","status":"completed","conclusion":"success","url":"https://github.com/MrFr3di/SmartPipe-Core/actions/runs/3","event":"pull_request","createdAt":"2026-07-17T00:02:00Z"}{{extra}}
                ]
                """;
            File.WriteAllText(WorkflowEvidencePath, evidence);
        }

        public Task CaptureAsync(CancellationToken cancellationToken) => _capture.CaptureAsync(new CaptureBaselineOptions(
            Root, "MrFr3di/SmartPipe-Core", Sha, "2.2.0", "2.1.2", PackagesPath,
            "eng/baselines/2.1.2", WorkflowEvidencePath), cancellationToken);

        public Task<BaselineVerificationResult> VerifyAsync() => _verify.VerifyAsync(new VerifyBaselineOptions(
            Root, ManifestPath, PackagesPath, Offline: true), TestContext.Current.CancellationToken);

        public void Dispose()
        {
            foreach (var package in _packages)
            {
                package.Dispose();
            }

            Directory.Delete(Root, recursive: true);
        }

        private string Write(string relative, string text)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return path;
        }
    }

    private sealed class CopyingFetcher(IReadOnlyDictionary<string, string> sources) : INuGetPackageFetcher
    {
        public Action? AfterCopy { get; set; }
        public int FetchCount { get; private set; }

        public Task<string> FetchAsync(string packageId, string version, string destinationDirectory, CancellationToken cancellationToken)
        {
            FetchCount++;
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, $"{packageId}.{version}.nupkg");
            File.Copy(sources[packageId], destination, overwrite: true);
            AfterCopy?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(destination);
        }
    }

    private sealed class RecordingSignatureVerifier : INuGetPackageSignatureVerifier
    {
        public Exception? Failure { get; set; }
        public List<string> VerifiedPaths { get; } = [];

        public Task VerifyAsync(string packagePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedPaths.Add(packagePath);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    internal sealed class ScenarioProcessRunner(string sha, string projectPath) : IProcessRunner
    {
        public Exception? GitFailure { get; set; }
        public string CurrentHeadSha { get; set; } = sha;
        public int MergeBaseExitCode { get; set; }
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (request.FileName == "git" && GitFailure is not null)
            {
                throw GitFailure;
            }
            if (request.FileName == "git" && request.Arguments.Count == 4
                && request.Arguments[0] == "-C" && request.Arguments.Skip(2).SequenceEqual(["status", "--porcelain"]))
            {
                return Success(string.Empty);
            }

            if (request.FileName == "git" && request.Arguments.Count == 4
                && request.Arguments[0] == "-C" && request.Arguments.Skip(2).SequenceEqual(["rev-parse", "HEAD"]))
            {
                return Success(CurrentHeadSha + "\n");
            }

            if (request.FileName == "git" && request.Arguments.Count == 6
                && request.Arguments[0] == "-C"
                && request.Arguments.Skip(2).SequenceEqual(
                    ["merge-base", "--is-ancestor", request.Arguments[4], "HEAD"]))
            {
                return Task.FromResult(new ProcessResult(
                    MergeBaseExitCode,
                    string.Empty,
                    MergeBaseExitCode == 0 ? string.Empty : "not an ancestor"));
            }

            if (request.FileName == "dotnet" && request.Arguments.SequenceEqual(["--version"]))
            {
                return Success("10.0.302\n");
            }

            if (request.Arguments.FirstOrDefault() == "msbuild")
            {
                return Success("{\"Properties\":{\"PackageId\":\"Fixture\",\"Version\":\"2.2.0\",\"TargetFramework\":\"net10.0\",\"IsPackable\":\"true\",\"AssemblyName\":\"Fixture\"}}");
            }

            if (request.Arguments.Take(2).SequenceEqual(["package", "list"]))
            {
                var escaped = JsonValue.Create(projectPath)!.ToJsonString();
                return Success($"{{\"version\":1,\"parameters\":\"--include-transitive\",\"projects\":[{{\"path\":{escaped},\"frameworks\":[{{\"framework\":\"net10.0\",\"topLevelPackages\":[{{\"id\":\"Example\",\"requestedVersion\":\"1.0.0\",\"resolvedVersion\":\"1.0.0\"}}]}}]}}]}}");
            }

            throw new InvalidOperationException($"Unexpected process: {request.FileName} {string.Join(' ', request.Arguments)}");
        }

        private static Task<ProcessResult> Success(string output) => Task.FromResult(new ProcessResult(0, output, string.Empty));
    }
}
