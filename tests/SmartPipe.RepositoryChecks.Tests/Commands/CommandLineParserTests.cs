using SmartPipe.RepositoryChecks.Commands;

namespace SmartPipe.RepositoryChecks.Tests.Commands;

public sealed class CommandLineParserTests
{
    private const string Sha = "8e79902d22de714f493582946f7c260462b0895e";

    [Fact]
    public void Parse_RejectsMissingCommand() =>
        Assert.Equal("A command is required.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse([])).Message);

    [Fact]
    public void Parse_RejectsUnknownCommand() =>
        Assert.Equal("Unknown command 'ship-it'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(["ship-it"])).Message);

    [Fact]
    public void Parse_ReturnsProvisionBaselineOptions()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<ProvisionBaselineOptions>(CommandLineParser.Parse(
        [
            "provision-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
        ]));

        Assert.Equal(repository.Path, command.RepositoryRoot);
        Assert.Equal(Path.Combine(repository.Path, "eng", "baselines", "2.1.2", "manifest.json"), command.ManifestPath);
        Assert.Equal(repository.PackagesPath, command.PackagesDirectory);
    }

    [Fact]
    public void Parse_ProvisionBaselineRejectsOfflineOption()
    {
        using var repository = new CommandRepository();

        Assert.Equal("Unknown option '--offline'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
        [
            "provision-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath, "--offline",
        ])).Message);
    }

    [Fact]
    public void Parse_ProvisionBaselineRejectsPathsOutsideRepository()
    {
        using var repository = new CommandRepository();

        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
        [
            "provision-baseline", "--repo-root", repository.Path,
            "--manifest", "../manifest.json",
            "--packages-dir", repository.PackagesPath,
        ]));

        Assert.Contains("inside the repository", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReturnsVerificationProfileOptions()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyProfileOptions>(CommandLineParser.Parse(
        [
            "verify", "--profile", "repository-checks-fast", "--repo-root", repository.Path,
            "--format", "jsonl", "--failures-only",
        ]));

        Assert.Equal(repository.Path, command.RepositoryRoot);
        Assert.Equal("repository-checks-fast", command.Profile);
        Assert.Equal(ProfileOutputFormat.Jsonl, command.Format);
        Assert.True(command.FailuresOnly);
    }

    [Fact]
    public void Parse_VerificationProfileDefaultsAreSafe()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyProfileOptions>(CommandLineParser.Parse(
            ["verify", "--profile", "repository-checks-fast", "--repo-root", repository.Path]));

        Assert.Equal(ProfileOutputFormat.Text, command.Format);
        Assert.False(command.FailuresOnly);
    }

    [Theory]
    [InlineData("--profile")]
    [InlineData("--format")]
    [InlineData("--repo-root")]
    public void Parse_RejectsMissingVerificationProfileOptionValue(string option)
    {
        using var repository = new CommandRepository();
        string[] args = option == "--profile"
            ? ["verify", option]
            : ["verify", "--profile", "repository-checks-fast", option];

        Assert.Contains("requires a value", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsUnknownAndDuplicateVerificationProfileOptions()
    {
        using var repository = new CommandRepository();

        Assert.Equal("Unknown option '--network'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["verify", "--profile", "repository-checks-fast", "--network", "true"])).Message);

        Assert.Equal("Duplicate option '--failures-only'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            ["verify", "--profile", "repository-checks-fast", "--repo-root", repository.Path, "--failures-only", "--failures-only"])).Message);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("JSONL")]
    public void Parse_RejectsInvalidVerificationProfileFormat(string format)
    {
        using var repository = new CommandRepository();

        Assert.Contains("format", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
        ["verify", "--profile", "repository-checks-fast", "--repo-root", repository.Path, "--format", format]
        )).Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsDuplicateVerificationProfileOption()
    {
        using var repository = new CommandRepository();

        Assert.Equal("Duplicate option '--profile'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
        ["verify", "--profile", "repository-checks-fast", "--profile", "sp220-05", "--repo-root", repository.Path]
        )).Message);
    }

    [Fact]
    public void Parse_ReturnsNuGetAuditOptions()
    {
        using var repository = new CommandRepository();
        var report = Path.Combine(repository.Path, "artifacts", "audit", "vulnerable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report, "{}");

        var command = Assert.IsType<VerifyNuGetAuditOptions>(CommandLineParser.Parse(
        [
            "verify-nuget-audit", "--repo-root", repository.Path,
            "--report", "artifacts/audit/vulnerable.json",
        ]));

        Assert.Equal(repository.Path, command.RepositoryRoot);
        Assert.Equal(report, command.ReportPath);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredOption()
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path).Where(value => value != "--workflow-evidence" && value != repository.WorkflowEvidencePath).ToArray();

        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args));

        Assert.Equal("Missing required option '--workflow-evidence'.", exception.Message);
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("8e79902d22de714f493582946f7c260462b0895E")]
    public void Parse_RejectsInvalidCommit(string commit)
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path);
        args[Array.IndexOf(args, "--commit") + 1] = commit;

        Assert.Contains("40 lowercase hexadecimal", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.1")]
    [InlineData("v2.1.2")]
    [InlineData("2.1.2-beta")]
    public void Parse_RejectsInvalidSemanticVersion(string version)
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path);
        args[Array.IndexOf(args, "--baseline-version") + 1] = version;

        Assert.Contains("three-part numeric version", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsOutputOutsideRepository()
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path);
        args[Array.IndexOf(args, "--output-dir") + 1] = "../escaped";

        Assert.Contains("inside the repository", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsPackagesOutsideRepository()
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path);
        args[Array.IndexOf(args, "--packages-dir") + 1] = Path.GetTempPath();

        Assert.Contains("inside the repository", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownOption()
    {
        using var repository = new CommandRepository();

        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
            CaptureArgs(repository.Path).Concat(["--mystery", "value"]).ToArray()));

        Assert.Equal("Unknown option '--mystery'.", exception.Message);
    }

    [Fact]
    public void Parse_AllowsOfflineVerificationWithMissingManifestPackages()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyBaselineOptions>(CommandLineParser.Parse(
        [
            "verify-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
            "--offline",
        ]));

        Assert.True(command.Offline);
    }

    [Fact]
    public void Parse_AllowsOnlineVerificationWithMissingPackages()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyBaselineOptions>(CommandLineParser.Parse(
        [
            "verify-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
        ]));

        Assert.False(command.Offline);
    }

    [Fact]
    public void Parse_IntegrityVerificationMode_IsExplicit()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyBaselineOptions>(CommandLineParser.Parse(
        [
            "verify-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
            "--mode", "integrity",
        ]));

        Assert.Equal(BaselineVerificationMode.Integrity, command.Mode);
    }

    [Fact]
    public void Parse_DefaultVerificationMode_RemainsFull()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<VerifyBaselineOptions>(CommandLineParser.Parse(
        [
            "verify-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
        ]));

        Assert.Equal(BaselineVerificationMode.Full, command.Mode);
    }

    [Fact]
    public void Parse_RejectsDuplicateOption()
    {
        using var repository = new CommandRepository();
        var args = CaptureArgs(repository.Path).Concat(["--commit", Sha]).ToArray();

        Assert.Equal("Duplicate option '--commit'.", Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args)).Message);
    }

    [Fact]
    public void Parse_ReturnsCaptureOptions()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<CaptureBaselineOptions>(CommandLineParser.Parse(CaptureArgs(repository.Path)));

        Assert.Equal(repository.Path, command.RepositoryRoot);
        Assert.Equal("2.1.2", command.BaselineVersion);
        Assert.Equal("eng/baselines/2.1.2", command.OutputDirectory);
    }

    [Fact]
    public void Parse_RunConsumersAcceptsHostingCategory()
    {
        using var repository = new CommandRepository();

        var command = Assert.IsType<RunConsumersCommandOptions>(CommandLineParser.Parse(
        [
            "run-consumers", "--repo-root", repository.Path,
            "--set", "current",
            "--package-directory", "packages",
            "--package-version", "2.2.0",
            "--category", "hosting",
        ]));

        Assert.Equal("hosting", command.Category);
    }

    private static string[] CaptureArgs(string repositoryRoot)
    {
        var workflowEvidence = Path.Combine(repositoryRoot, "workflow.json");
        return
        [
            "capture-baseline", "--repo-root", repositoryRoot,
            "--repository", "MrFr3di/SmartPipe-Core",
            "--commit", Sha,
            "--target-release", "2.2.0",
            "--baseline-version", "2.1.2",
            "--packages-dir", Path.Combine(repositoryRoot, "packages"),
            "--output-dir", "eng/baselines/2.1.2",
            "--workflow-evidence", workflowEvidence,
        ];
    }

    private sealed class CommandRepository : IDisposable
    {
        public CommandRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartPipe.CommandTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "eng", "baselines", "2.1.2"));
            PackagesPath = System.IO.Path.Combine(Path, "packages");
            Directory.CreateDirectory(PackagesPath);
            WorkflowEvidencePath = System.IO.Path.Combine(Path, "workflow.json");
            File.WriteAllText(WorkflowEvidencePath, "{}");
        }

        public string Path { get; }

        public string PackagesPath { get; }

        public string WorkflowEvidencePath { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
