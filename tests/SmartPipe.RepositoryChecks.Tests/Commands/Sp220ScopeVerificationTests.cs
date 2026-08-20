using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.Commands;

[Collection(ExternalProcessCollection.Name)]
public sealed class Sp220ScopeVerificationTests
{
    private const string BaseCommit = "8e79902d22de714f493582946f7c260462b0895e";

    [Fact]
    public void Parse_ReturnsTemporarySp220ScopeOptions()
    {
        using var repository = new TemporaryRepository();

        var command = Assert.IsType<VerifySp220ScopeOptions>(CommandLineParser.Parse(
            ["verify-sp220-scope", "--repo-root", repository.Path, "--base-commit", BaseCommit]));

        Assert.Equal(repository.Path, command.RepositoryRoot);
        Assert.Equal(BaseCommit, command.BaseCommit);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Verify_MapsGitDiffExitCode(int gitExitCode, bool expectedSuccess)
    {
        using var repository = new TemporaryRepository();
        var runner = new RecordingRunner(gitExitCode);

        var result = await new Sp220ScopeVerificationService(runner, "git").VerifyAsync(
            new VerifySp220ScopeOptions(repository.Path, BaseCommit),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedSuccess, result.Success);
        Assert.Equal(
        [
            "-C", repository.Path, "diff", "--quiet", BaseCommit, "HEAD", "--",
            "src/SmartPipe.Core", "src/SmartPipe.Extensions", "src/SmartPipe.Extensions.Json",
        ], runner.Request!.Arguments);
    }

    [Fact]
    public async Task Verify_TreatsGitExitCodeAboveOneAsToolFailure()
    {
        using var repository = new TemporaryRepository();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(() =>
            new Sp220ScopeVerificationService(new RecordingRunner(128), "git").VerifyAsync(
                new VerifySp220ScopeOptions(repository.Path, BaseCommit),
                TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
    }

    [Fact]
    public async Task Verify_WithRealGit_DistinguishesGovernanceProductionAndInvalidBase()
    {
        using var repository = new GitRepository();
        var baseCommit = await repository.InitializeAsync();
        var service = new Sp220ScopeVerificationService(new ProcessRunner(), "git");

        await repository.CommitFileAsync("eng/governance.txt", "governance", "governance change");
        var governanceResult = await service.VerifyAsync(
            new VerifySp220ScopeOptions(repository.Path, baseCommit),
            TestContext.Current.CancellationToken);

        await repository.CommitFileAsync(
            "src/SmartPipe.Core/Production.cs",
            "namespace SmartPipe.Core;",
            "production change");
        var productionResult = await service.VerifyAsync(
            new VerifySp220ScopeOptions(repository.Path, baseCommit),
            TestContext.Current.CancellationToken);
        var invalidBaseException = await Assert.ThrowsAsync<RepositoryCheckException>(() =>
            service.VerifyAsync(
                new VerifySp220ScopeOptions(repository.Path, new string('f', 40)),
                TestContext.Current.CancellationToken));

        Assert.True(governanceResult.Success);
        Assert.False(productionResult.Success);
        Assert.Equal(ExitCodes.ExternalSourceUnavailable, invalidBaseException.ExitCode);
    }

    private sealed class RecordingRunner(int exitCode) : IProcessRunner
    {
        public ProcessRequest? Request { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty));
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartPipe.ScopeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class GitRepository : IDisposable
    {
        private readonly ProcessRunner _runner = new();

        public GitRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SmartPipe.ScopeGitTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public async Task<string> InitializeAsync()
        {
            await GitAsync("init");
            await GitAsync("config", "commit.gpgSign", "false");
            await GitAsync("config", "tag.gpgSign", "false");
            await GitAsync("config", "user.email", "smartpipe-tests@example.invalid");
            await GitAsync("config", "user.name", "SmartPipe Tests");
            await CommitFileAsync("README.md", "fixture", "initial");
            return (await GitAsync("rev-parse", "HEAD")).StandardOutput.Trim();
        }

        public async Task CommitFileAsync(string relativePath, string content, string message)
        {
            var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            await GitAsync("add", "--", relativePath);
            await GitAsync("commit", "-m", message);
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }

        private async Task<ProcessResult> GitAsync(params string[] arguments)
        {
            var result = await _runner.RunAsync(
                new ProcessRequest("git", ["-C", Path, .. arguments], TimeSpan.FromSeconds(30)),
                TestContext.Current.CancellationToken);
            Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result;
        }
    }
}
