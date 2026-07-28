using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Tests.Repository;
using SmartPipe.RepositoryChecks.Tests.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

[Trait("Category", "PackageInfrastructure")]
[Collection(ExternalProcessCollection.Name)]
public sealed class ConsumerScenarioRunnerTests
{
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
