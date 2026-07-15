using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

public sealed class NuGetPackageSignatureVerifierTests
{
    [Fact]
    public async Task VerifyAsync_InvokesDotnetNuGetVerify_WithArgumentList()
    {
        var runner = new StubProcessRunner(new ProcessResult(0, "verified", string.Empty));
        var verifier = new NuGetPackageSignatureVerifier(
            runner,
            "C:\\sdk path\\dotnet.exe",
            TimeSpan.FromSeconds(17));
        const string packagePath = "C:\\package path\\package.nupkg";

        await verifier.VerifyAsync(packagePath, TestContext.Current.CancellationToken);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("C:\\sdk path\\dotnet.exe", request.FileName);
        Assert.Equal(["nuget", "verify", packagePath, "--all", "--verbosity", "normal"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(17), request.Timeout);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsNormally_WhenToolExitCodeIsZero()
    {
        var verifier = CreateVerifier(new StubProcessRunner(new ProcessResult(0, string.Empty, string.Empty)));

        await verifier.VerifyAsync("package.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VerifyAsync_ThrowsIntegrityError_AndRedactsSensitiveStderr_WhenToolExitCodeIsNonZero()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stderr = $"Signature failed at {home}\\.nuget\\secret. See https://example.test/a?token=secret";
        var verifier = CreateVerifier(new StubProcessRunner(new ProcessResult(1, string.Empty, stderr)));

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => verifier.VerifyAsync("package.nupkg", TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
        Assert.DoesNotContain(home, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=secret", exception.Message, StringComparison.Ordinal);
        Assert.Contains("<home>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("?<redacted>", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)ProcessFailureKind.StartFailure)]
    [InlineData((int)ProcessFailureKind.Timeout)]
    [InlineData((int)ProcessFailureKind.Canceled)]
    [InlineData((int)ProcessFailureKind.TerminationFailure)]
    public async Task VerifyAsync_ThrowsExternalToolError_WhenProcessCannotComplete(int failureKindValue)
    {
        var failureKind = (ProcessFailureKind)failureKindValue;
        var runner = new StubProcessRunner(new ProcessRunnerException(failureKind, "tool failed"));
        var verifier = CreateVerifier(runner);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => verifier.VerifyAsync("package.nupkg", TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
        Assert.Contains("signature verification tool", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateStartInfo_UsesArgumentListAndNeverShellConcatenates()
    {
        var request = new ProcessRequest(
            "C:\\sdk path\\dotnet.exe",
            ["nuget", "verify", "C:\\package path\\package.nupkg", "--all"],
            TimeSpan.FromMinutes(1));

        var hostLaunch = new ProcessHostLaunch("C:\\host path\\SmartPipe.RepositoryChecks.exe", ["host.dll"]);
        var startInfo = ProcessRunner.CreateStartInfo(request, hostLaunch, "0123456789abcdef0123456789abcdef");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(hostLaunch.FileName, startInfo.FileName);
        Assert.Equal(
            [
                "host.dll",
                RepositoryCheckProcessHost.DispatchArgument,
                "0123456789abcdef0123456789abcdef",
                request.FileName,
                "--",
                .. request.Arguments,
            ],
            startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    private static NuGetPackageSignatureVerifier CreateVerifier(IProcessRunner processRunner)
    {
        return new NuGetPackageSignatureVerifier(processRunner, "dotnet");
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly ProcessResult? _result;
        private readonly ProcessRunnerException? _exception;

        public StubProcessRunner(ProcessResult result)
        {
            _result = result;
        }

        public StubProcessRunner(ProcessRunnerException exception)
        {
            _exception = exception;
        }

        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_result!);
        }
    }
}
