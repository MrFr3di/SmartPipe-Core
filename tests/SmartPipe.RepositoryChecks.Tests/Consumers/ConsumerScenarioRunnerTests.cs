using System.Text.Json;
using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Serialization;
using SmartPipe.RepositoryChecks.Tests.Repository;
using SmartPipe.RepositoryChecks.Tests.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

[Trait("Category", "PackageInfrastructure")]
[Collection(ExternalProcessCollection.Name)]
public sealed class ConsumerScenarioRunnerTests
{
    [Fact]
    public void ProcessFailure_IsBoundedSingleLineAndPointsToRelativeRetainedEvidence()
    {
        using var fixture = new RepositoryTestDirectory();
        var relativeLog = "artifacts/consumers/failure/run/logs/stderr.log";
        var logPath = fixture.Write(relativeLog, "secret=do-not-print\n" + new string('x', 32));
        var result = new DotNetProcessResult(
            17,
            "",
            "secret=do-not-print\r\n" + new string('x', 100_000),
            Path.Combine(fixture.Path, "artifacts/consumers/failure/run/logs/stdout.log"),
            logPath,
            Path.Combine(fixture.Path, "dotnet") + " restore --configfile <argument>",
            DateTimeOffset.UnixEpoch,
            12);

        var error = ConsumerScenarioRunner.BuildProcessFailure(result, fixture.Path);

        Assert.Equal("SPCONS014", error.Code);
        Assert.InRange(error.Message.Length, 1, 1024);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
        Assert.DoesNotContain("do-not-print", error.Message, StringComparison.Ordinal);
        Assert.Contains("Consumer command failed (17)", error.Message, StringComparison.Ordinal);
        Assert.Contains(relativeLog, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Path, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void ProcessFailure_RejectsNewlineAndOversizedEvidencePaths()
    {
        using var fixture = new RepositoryTestDirectory();
        var hostilePath = Path.Combine(
            fixture.Path,
            "artifacts",
            "consumers",
            "failure",
            "run",
            "logs",
            new string('x', 2_000) + "\nsecret.log");
        var result = new DotNetProcessResult(
            9,
            "",
            "stderr",
            hostilePath,
            hostilePath,
            "dotnet",
            DateTimeOffset.UnixEpoch,
            1);

        var error = Assert.Throws<ConsumerScenarioException>(() =>
            ConsumerScenarioRunner.BuildProcessFailure(result, fixture.Path));

        Assert.Equal("SPCONS009", error.Code);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
        Assert.InRange(error.Message.Length, 1, 1024);
        Assert.DoesNotContain("secret.log", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessFailure_RejectsEvidenceOutsideRepositoryRoot()
    {
        using var fixture = new RepositoryTestDirectory();
        var outsideLog = Path.Combine(fixture.Path, "..", "consumer.stderr.log");
        var result = new DotNetProcessResult(
            3,
            "",
            "stderr",
            outsideLog,
            outsideLog,
            "dotnet",
            DateTimeOffset.UnixEpoch,
            1);

        var error = Assert.Throws<ConsumerScenarioException>(() =>
            ConsumerScenarioRunner.BuildProcessFailure(result, fixture.Path));

        Assert.Equal("SPCONS009", error.Code);
        Assert.DoesNotContain("consumer.stderr.log", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', error.Message);
        Assert.DoesNotContain('\n', error.Message);
    }

    [Fact]
    public void ExpectedPublishDiagnostic_AppendsDeclaredPropertiesAndWarningsAsErrors()
    {
        var expectation = new ExpectedPublishDiagnostic
        {
            Code = "IL2026",
            SourcePath = "Program.cs",
            Line = 9,
            MsBuildProperties = ["EnableTrimAnalyzer=true", "InvokeReflectionValidation=true"],
        };

        var arguments = ConsumerScenarioRunner.BuildExpectedDiagnosticPublishArguments(
            ["publish", "Consumer.csproj", "--no-restore"],
            expectation);

        Assert.Equal(
            ["publish", "Consumer.csproj", "--no-restore", "-warnaserror", "-p:EnableTrimAnalyzer=true", "-p:InvokeReflectionValidation=true"],
            arguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExpectedPublishDiagnostic_AcceptsExactConsumerCallSiteFromEitherLog(bool useStandardOutput)
    {
        using var fixture = new RepositoryTestDirectory();
        var source = fixture.Write("source/Program.cs", new string('\n', 8) + "CallRuc();\n");
        var diagnostic = $"{source}(9,1): Trim analysis error IL2026: Using member requires unreferenced code.\n";
        var stdout = useStandardOutput ? diagnostic : string.Empty;
        var stderr = useStandardOutput ? string.Empty : diagnostic;
        var result = DiagnosticResult(fixture, 1, stdout, stderr);

        await ConsumerScenarioRunner.ValidateExpectedPublishDiagnosticAsync(
            result,
            DiagnosticExpectation(),
            source,
            fixture.Path,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExpectedPublishDiagnostic_AcceptsExactConsumerCallSiteWithRedactedHomePath()
    {
        using var fixture = new RepositoryTestDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));
        var source = Path.Combine(home, "SmartPipe.RepositoryChecks.Tests", Guid.NewGuid().ToString("N"), "Program.cs");
        var reportedSource = DiagnosticRedactor.Redact(source);
        Assert.StartsWith("<home>", reportedSource, StringComparison.Ordinal);
        var diagnostic = $"{reportedSource}(9,1): Trim analysis error IL2026: Using member requires unreferenced code.\n";

        await ConsumerScenarioRunner.ValidateExpectedPublishDiagnosticAsync(
            DiagnosticResult(fixture, 1, diagnostic, string.Empty),
            DiagnosticExpectation(),
            source,
            fixture.Path,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("wrong-source")]
    [InlineData("missing-boundary")]
    [InlineData("embedded-token")]
    public async Task ExpectedPublishDiagnostic_RejectsNonExactRedactedHomePath(string mutation)
    {
        using var fixture = new RepositoryTestDirectory();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));
        var source = Path.Combine(home, "SmartPipe.RepositoryChecks.Tests", Guid.NewGuid().ToString("N"), "Program.cs");
        var reportedSource = DiagnosticRedactor.Redact(source);
        Assert.StartsWith("<home>", reportedSource, StringComparison.Ordinal);
        var mutatedSource = mutation switch
        {
            "wrong-source" => reportedSource.Replace("Program.cs", "Other.cs", StringComparison.Ordinal),
            "missing-boundary" => reportedSource.Replace("<home>", "<home>suffix", StringComparison.Ordinal),
            "embedded-token" => "prefix" + reportedSource,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var diagnostic = $"{mutatedSource}(9,1): Trim analysis error IL2026: Using member requires unreferenced code.\n";

        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(() =>
            ConsumerScenarioRunner.ValidateExpectedPublishDiagnosticAsync(
                DiagnosticResult(fixture, 1, diagnostic, string.Empty),
                DiagnosticExpectation(),
                source,
                fixture.Path,
                TestContext.Current.CancellationToken));

        Assert.Equal("SPCONS024", error.Code);
    }

    [Fact]
    public async Task ExpectedPublishDiagnosticPhase_RunsDeclaredFailureAndRecordsItsEvent()
    {
        using var fixture = new RepositoryTestDirectory();
        var source = fixture.Write("source/Program.cs", new string('\n', 8) + "CallRuc();\n");
        var diagnostic = $"{source}(9,1): Trim analysis error IL2026: Using member requires unreferenced code.\n";
        var stdoutLog = fixture.Write("logs/stdout.log", diagnostic);
        var stderrLog = fixture.Write("logs/stderr.log", string.Empty);
        var process = new FakeProcessRunner(
            new ProcessResult(0, string.Empty, string.Empty, stdoutLog, stderrLog),
            new ProcessResult(1, diagnostic, string.Empty, stdoutLog, stderrLog));
        var runner = new ConsumerScenarioRunner(new DotNetProcessRunner(process));
        var events = new List<ConsumerCommandEvent>();

        await runner.RunExpectedPublishDiagnosticAsync(
            ["restore", "Consumer.csproj", "--locked-mode"],
            ["publish", "Consumer.csproj", "--no-restore"],
            DiagnosticExpectation(),
            fixture.Path,
            Path.Combine(fixture.Path, "logs"),
            fixture.Path,
            source,
            TimeSpan.FromMinutes(1),
            events,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["restore", "Consumer.csproj", "--locked-mode", "-p:EnableTrimAnalyzer=true", "-p:InvokeReflectionValidation=true"],
            process.Requests[0].Arguments);
        Assert.Equal(
            ["publish", "Consumer.csproj", "--no-restore", "-warnaserror", "-p:EnableTrimAnalyzer=true", "-p:InvokeReflectionValidation=true"],
            process.Requests[1].Arguments);
        Assert.Equal("process", events[0].Phase);
        var command = events[1];
        Assert.Equal("expected-publish-diagnostic", command.Phase);
        Assert.Equal(1, command.ExitCode);
    }

    [Theory]
    [InlineData("success", "SPCONS024")]
    [InlineData("wrong-code", "SPCONS014")]
    [InlineData("wrong-line", "SPCONS014")]
    [InlineData("wrong-source", "SPCONS024")]
    [InlineData("duplicate", "SPCONS024")]
    [InlineData("infrastructure", "SPCONS014")]
    [InlineData("expected-plus-infrastructure", "SPCONS014")]
    public async Task ExpectedPublishDiagnostic_RejectsSuccessAndNonExactFailures(string mutation, string code)
    {
        using var fixture = new RepositoryTestDirectory();
        var source = fixture.Write("source/Program.cs", new string('\n', 8) + "CallRuc();\n");
        var otherSource = fixture.Write("source/Other.cs", new string('\n', 8) + "CallRuc();\n");
        var exact = $"{source}(9,1): Trim analysis error IL2026: Using member requires unreferenced code.\n";
        var (exitCode, output) = mutation switch
        {
            "success" => (0, exact),
            "wrong-code" => (1, exact.Replace("IL2026", "IL2055", StringComparison.Ordinal)),
            "wrong-line" => (1, exact.Replace("(9,1)", "(8,1)", StringComparison.Ordinal)),
            "wrong-source" => (1, exact.Replace(source, otherSource, StringComparison.Ordinal)),
            "duplicate" => (1, exact + exact),
            "infrastructure" => (1, "error NETSDK1047: Assets file has no target.\n"),
            "expected-plus-infrastructure" => (1, exact + "error NETSDK1047: Assets file has no target.\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(() =>
            ConsumerScenarioRunner.ValidateExpectedPublishDiagnosticAsync(
                DiagnosticResult(fixture, exitCode, output, string.Empty),
                DiagnosticExpectation(),
                source,
                fixture.Path,
                TestContext.Current.CancellationToken));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void SuccessfulConsumerResult_SerializesWithSchemaVersionOneShape()
    {
        var result = new ConsumerScenarioResult(
            1,
            "fixture",
            "passed",
            "2.2.0",
            true,
            12,
            ["SmartPipe.Core"],
            [new("process", "dotnet build", 0, DateTimeOffset.UnixEpoch, 4, "logs/stdout.log", "logs/stderr.log")]);

        var json = JsonSerializer.Serialize(result, RepositoryChecksJsonContext.Default.ConsumerScenarioResult)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        Assert.Equal("""
{
  "schemaVersion": 1,
  "scenario": "fixture",
  "status": "passed",
  "packageVersion": "2.2.0",
  "restoreLocked": true,
  "durationMs": 12,
  "observedSmartPipeDependencies": [
    "SmartPipe.Core"
  ],
  "commands": [
    {
      "phase": "process",
      "command": "dotnet build",
      "exitCode": 0,
      "startedUtc": "1970-01-01T00:00:00+00:00",
      "durationMs": 4,
      "standardOutputLog": "logs/stdout.log",
      "standardErrorLog": "logs/stderr.log"
    }
  ]
}
""".TrimEnd('\n'), json);
    }

    [Fact]
    public async Task ProcessRunner_UsesArgumentListSpillsAndRedactsSecrets()
    {
        using var fixture = new RepositoryTestDirectory();
        var baseRunner = new SmartPipe.RepositoryChecks.Infrastructure.ProcessRunner(maximumRetainedOutputCharacters: 64, maximumSpillOutputCharacters: 5 * 1024 * 1024);
        var runner = new DotNetProcessRunner(baseRunner, maximumCapturedCharacters: 64);
        var result = await runner.RunAsync(new(FixtureExecutable(), ["spill-pressure"], fixture.Path, fixture.Path, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(result.StandardOutputLog));
        Assert.InRange(result.StandardOutput.Length, 1, 128);
        Assert.Contains("SPILL-END", result.StandardOutput);
        var log = await File.ReadAllTextAsync(result.StandardOutputLog, TestContext.Current.CancellationToken);
        Assert.True(log.Length > 4 * 1024 * 1024);
        Assert.Contains("[spill log truncated]", log);
        Assert.DoesNotContain("?token=", result.Command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunner_UsesExplicitIsolatedWorkingDirectory()
    {
        using var fixture = new RepositoryTestDirectory();
        var logs = Path.Combine(fixture.Path, "logs");
        var result = await new DotNetProcessRunner().RunAsync(new(FixtureExecutable(), ["touch", "cwd-proof.txt"], fixture.Path, logs, TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(fixture.Path, "cwd-proof.txt")));
    }

    [Fact]
    public void TemplateCopy_RejectsDirectoryReparsePointBeforeDescent()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("tests/Consumers/Scenarios/fixture/Consumer.csproj", "<Project />");
        fixture.Write("outside/secret.txt", "secret");
        if (!fixture.TryCreateDirectoryLink("tests/Consumers/Scenarios/fixture/linked", "outside")) return;
        var destination = Path.Combine(fixture.Path, "destination"); Directory.CreateDirectory(destination);
        var error = Assert.Throws<ConsumerScenarioException>(() => ConsumerScenarioRunner.CopyTemplateDirectory(
            fixture.Path, "tests/Consumers/Scenarios/fixture/Consumer.csproj", destination));
        Assert.Equal("SPCONS005", error.Code);
        Assert.False(File.Exists(Path.Combine(destination, "linked", "secret.txt")));
    }

    [Fact]
    public void BinaryPhaseEvidence_ProvesSingleBuildThenDeploymentMetadataAndHashReplacement()
    {
        var now = DateTimeOffset.UtcNow;
        var hash = new string('a', 64);
        var events = new ConsumerCommandEvent[]
        {
            new("process", "dotnet restore Consumer.csproj", 0, now, 1, "logs/a", "logs/b"),
            new("process", "dotnet build Consumer.csproj --no-restore", 0, now.AddSeconds(1), 1, "logs/c", "logs/d"),
            new("process", "dotnet restore Consumer.csproj --use-lock-file --force-evaluate", 0, now.AddSeconds(2), 1, "logs/e", "logs/f"),
            new("process", "dotnet msbuild Consumer.csproj -t:GenerateBuildDependencyFile -p:Configuration=Release", 0, now.AddSeconds(3), 1, "logs/g", "logs/h"),
            new("binary-deployment-metadata", $"refresh-deps consumer-before-sha256={hash} consumer-after-sha256={hash}", 0, now.AddSeconds(4), 0, "", ""),
            new("binary-runtime-replacement", "replace-runtime package=SmartPipe.Core sha256=" + hash, 0, now.AddSeconds(5), 0, "", ""),
            new("process", "dotnet Consumer.dll", 0, now.AddSeconds(6), 1, "logs/i", "logs/j"),
        };
        ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(events, 1);
        var missingMetadata = events.Where(item => item.Phase != "binary-deployment-metadata").ToArray();
        Assert.Equal("SPCONS020", Assert.Throws<ConsumerScenarioException>(() => ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(missingMetadata, 1)).Code);
        var changedBinary = events.Select(item => item.Phase == "binary-deployment-metadata"
            ? item with { Command = item.Command.Replace("consumer-after-sha256=" + hash, "consumer-after-sha256=" + new string('b', 64), StringComparison.Ordinal) }
            : item).ToArray();
        Assert.Equal("SPCONS020", Assert.Throws<ConsumerScenarioException>(() => ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(changedBinary, 1)).Code);
        var invalid = events.Append(new("process", "dotnet build Consumer.csproj", 0, now.AddSeconds(7), 1, "logs/k", "logs/l")).ToArray();
        Assert.Equal("SPCONS020", Assert.Throws<ConsumerScenarioException>(() => ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(invalid, 1)).Code);
    }

    [Fact]
    public async Task BinaryDeploymentMetadata_UsesCurrentRestoreAndDepsTargetWithoutChangingConsumerBinary()
    {
        using var fixture = new RepositoryTestDirectory();
        var project = fixture.Write("source/Consumer.csproj", "<Project />");
        var output = Path.Combine(fixture.Path, "source", "bin", "Release", "net10.0");
        Directory.CreateDirectory(output);
        var consumerAssembly = Path.Combine(output, "Consumer.dll");
        var binary = new byte[] { 1, 3, 3, 7 };
        await File.WriteAllBytesAsync(consumerAssembly, binary, TestContext.Current.CancellationToken);
        var stdoutLog = fixture.Write("logs/stdout.log", string.Empty);
        var stderrLog = fixture.Write("logs/stderr.log", string.Empty);
        var process = new FakeProcessRunner(
            new ProcessResult(0, string.Empty, string.Empty, stdoutLog, stderrLog),
            new ProcessResult(0, string.Empty, string.Empty, stdoutLog, stderrLog));
        var runner = new ConsumerScenarioRunner(new DotNetProcessRunner(process));
        var events = new List<ConsumerCommandEvent>();

        await runner.RefreshBinaryCompatibilityDeploymentMetadataAsync(
            fixture.Path,
            project,
            output,
            ["SmartPipe.Core", "SmartPipe.Extensions", "SmartPipe.Extensions.Channels"],
            Path.Combine(fixture.Path, "current-feed"),
            "2.2.0",
            new Dictionary<string, string> { ["Microsoft.Extensions.Logging.Abstractions"] = "10.0.8" },
            ["Microsoft.Extensions.*"],
            fixture.Path,
            TimeSpan.FromMinutes(1),
            events,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["restore", project, "--configfile", Path.Combine(fixture.Path, "NuGet.Config"), "--packages", Path.Combine(fixture.Path, "packages"), "--use-lock-file", "--force-evaluate"],
            process.Requests[0].Arguments);
        Assert.Equal(
            ["msbuild", project, "-t:GenerateBuildDependencyFile", "-p:Configuration=Release"],
            process.Requests[1].Arguments);
        Assert.Equal(binary, await File.ReadAllBytesAsync(consumerAssembly, TestContext.Current.CancellationToken));
        Assert.Equal("binary-deployment-metadata", events[^1].Phase);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(binary));
        Assert.Contains($"consumer-before-sha256={expectedHash}", events[^1].Command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"consumer-after-sha256={expectedHash}", events[^1].Command, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SmartPipe.Extensions.Channels\" Version=\"2.2.0", await File.ReadAllTextAsync(Path.Combine(fixture.Path, "Directory.Packages.props"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryReplacementClosure_IncludesCurrentFacadeForwardingDependencies()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var graph = await new SmartPipe.RepositoryChecks.PackageGraph.PackageGraphLoader().LoadAsync(
            root, "eng/package-graph.json", TestContext.Current.CancellationToken);

        var closure = ConsumerScenarioRunner.CurrentSmartPipeClosure(
            graph, ["SmartPipe.Core", "SmartPipe.Extensions", "SmartPipe.Extensions.Json"]);

        Assert.Equal(
            ["SmartPipe.Core", "SmartPipe.Extensions.Channels", "SmartPipe.Extensions.Transforms",
             "SmartPipe.Extensions.DataAnnotations", "SmartPipe.Extensions.DependencyInjection",
             "SmartPipe.Extensions.Hosting", "SmartPipe.Extensions.Json",
             "SmartPipe.Extensions.Logging", "SmartPipe.Extensions"],
            closure);
    }

    [Fact]
    public void Redact_RemovesUserInfoQueryAndCredentials()
    {
        var redacted = DotNetProcessRunner.Redact("https://user:pass@example.test/v3/index.json?token=secret password=hunter2");
        Assert.DoesNotContain("user", redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public async Task RuntimeReplacement_RejectsOversizedEntryBeforeWriting()
    {
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ($"lib/net10.0/SmartPipe.Core.dll", new byte[4096]),
        ]);
        using var fixture = new RepositoryTestDirectory();
        var target = Path.Combine(fixture.Path, "SmartPipe.Core.dll");

        await Assert.ThrowsAsync<RepositoryCheckException>(() => ConsumerScenarioRunner.ExtractValidatedEntryAsync(
            package.Path,
            "lib/net10.0/SmartPipe.Core.dll",
            target,
            TestContext.Current.CancellationToken,
            new NuGetPackageReaderOptions { MaxEntryUncompressedBytes = 1024 }));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task RuntimeReplacement_RejectsSuspiciousCompressionRatioBeforeWriting()
    {
        using var package = SyntheticNuGetPackage.Create(entries:
        [
            ($"lib/net10.0/SmartPipe.Core.dll", new byte[1024 * 1024]),
        ]);
        using var fixture = new RepositoryTestDirectory();
        var target = Path.Combine(fixture.Path, "SmartPipe.Core.dll");

        await Assert.ThrowsAsync<RepositoryCheckException>(() => ConsumerScenarioRunner.ExtractValidatedEntryAsync(
            package.Path,
            "lib/net10.0/SmartPipe.Core.dll",
            target,
            TestContext.Current.CancellationToken,
            new NuGetPackageReaderOptions { MaxCompressionRatio = 10 }));
        Assert.False(File.Exists(target));
    }


    private static string FixtureExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = output.Parent!.Name;
        var root = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "tests", "SmartPipe.RepositoryChecks.ProcessFixture", "bin", configuration, "net10.0",
            "SmartPipe.RepositoryChecks.ProcessFixture" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
    }

    private static ExpectedPublishDiagnostic DiagnosticExpectation() => new()
    {
        Code = "IL2026",
        SourcePath = "Program.cs",
        Line = 9,
        MsBuildProperties = ["EnableTrimAnalyzer=true", "InvokeReflectionValidation=true"],
    };

    private static DotNetProcessResult DiagnosticResult(RepositoryTestDirectory fixture, int exitCode, string stdout, string stderr)
    {
        var stdoutLog = fixture.Write("logs/stdout.log", stdout);
        var stderrLog = fixture.Write("logs/stderr.log", stderr);
        return new(exitCode, stdout, stderr, stdoutLog, stderrLog, "dotnet publish", DateTimeOffset.UnixEpoch, 1);
    }
}
