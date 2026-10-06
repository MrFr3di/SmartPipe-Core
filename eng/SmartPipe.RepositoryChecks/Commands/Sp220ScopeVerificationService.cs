using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed record Sp220ScopeVerificationResult(bool Success)
{
    public string Format() => Success
        ? "SP220-00 SCOPE VERIFICATION PASSED"
        : "SP220-00 SCOPE VERIFICATION FAILED: production files changed";
}

internal sealed class Sp220ScopeVerificationService(IProcessRunner processRunner, string gitPath)
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    public async Task<Sp220ScopeVerificationResult> VerifyAsync(
        VerifySp220ScopeOptions options,
        CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(new ProcessRequest(
                gitPath,
                [
                    "-C", options.RepositoryRoot, "diff", "--quiet", options.BaseCommit, "HEAD", "--",
                    "src/SmartPipe.Core", "src/SmartPipe.Extensions", "src/SmartPipe.Extensions.Json",
                ],
                ProcessTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("SP220-00 scope verification was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                "git diff failed while verifying the SP220-00 production scope.",
                exception);
        }

        return result.ExitCode switch
        {
            0 => new(true),
            1 => new(false),
            _ => throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                $"git diff failed while verifying the SP220-00 production scope (exit code {result.ExitCode})."),
        };
    }
}
