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
    public void Parse_RejectsOfflineVerificationWithMissingPackage()
    {
        using var repository = new CommandRepository();

        var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(
        [
            "verify-baseline", "--repo-root", repository.Path,
            "--manifest", "eng/baselines/2.1.2/manifest.json",
            "--packages-dir", repository.PackagesPath,
            "--offline",
        ]));

        Assert.Contains("SmartPipe.Core.2.1.2.nupkg", exception.Message, StringComparison.Ordinal);
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
