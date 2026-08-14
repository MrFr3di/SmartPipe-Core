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
    public void BinaryPhaseEvidence_ProvesSingleBuildThenHashReplacementAndRunWithoutRebuild()
    {
        var now = DateTimeOffset.UtcNow;
        var events = new ConsumerCommandEvent[]
        {
            new("process", "dotnet restore Consumer.csproj", 0, now, 1, "logs/a", "logs/b"),
            new("process", "dotnet build Consumer.csproj --no-restore", 0, now.AddSeconds(1), 1, "logs/c", "logs/d"),
            new("binary-runtime-replacement", "replace-runtime package=SmartPipe.Core sha256=" + new string('a', 64), 0, now.AddSeconds(2), 0, "", ""),
            new("process", "dotnet Consumer.dll", 0, now.AddSeconds(3), 1, "logs/e", "logs/f"),
        };
        ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(events, 1);
        var invalid = events.Append(new("process", "dotnet build Consumer.csproj", 0, now.AddSeconds(4), 1, "logs/g", "logs/h")).ToArray();
        Assert.Equal("SPCONS020", Assert.Throws<ConsumerScenarioException>(() => ConsumerScenarioRunner.ValidateBinaryCompatibilityPhases(invalid, 1)).Code);
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
}
