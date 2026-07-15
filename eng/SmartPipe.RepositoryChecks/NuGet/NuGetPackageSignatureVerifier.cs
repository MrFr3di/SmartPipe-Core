using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal interface INuGetPackageSignatureVerifier
{
    Task VerifyAsync(string packagePath, CancellationToken cancellationToken);
}

internal sealed class NuGetPackageSignatureVerifier : INuGetPackageSignatureVerifier
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly IProcessRunner _processRunner;
    private readonly string _dotnetExecutable;
    private readonly TimeSpan _timeout;

    public NuGetPackageSignatureVerifier(
        IProcessRunner processRunner,
        string dotnetExecutable,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetExecutable);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _processRunner = processRunner;
        _dotnetExecutable = dotnetExecutable;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task VerifyAsync(string packagePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        var request = new ProcessRequest(
            _dotnetExecutable,
            ["nuget", "verify", packagePath, "--all", "--verbosity", "normal"],
            _timeout);

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                $"NuGet signature verification tool failed: {DiagnosticRedactor.Redact(exception.Message)}",
                exception);
        }

        if (result.ExitCode == 0)
        {
            return;
        }

        var diagnostic = !string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardError
            : result.StandardOutput;
        diagnostic = string.IsNullOrWhiteSpace(diagnostic)
            ? "No diagnostic output was produced."
            : DiagnosticRedactor.Redact(diagnostic.Trim());
        throw new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet signature verification failed for {Path.GetFileName(packagePath)}: {diagnostic}");
    }
}
