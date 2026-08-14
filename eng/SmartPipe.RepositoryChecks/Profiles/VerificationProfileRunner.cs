using SmartPipe.RepositoryChecks.Reporting;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Profiles;

internal sealed record VerificationProfileRunResult(
    IReadOnlyList<CheckRun> CheckRuns,
    int ExitCode);

internal sealed class VerificationProfileRunner
{
    private readonly IReadOnlyDictionary<string, Func<CancellationToken, Task<CheckRun>>> _checks;

    public VerificationProfileRunner(IReadOnlyDictionary<string, Func<CancellationToken, Task<CheckRun>>> checks)
    {
        _checks = checks;
    }

    public async Task<VerificationProfileRunResult> RunAsync(
        VerificationProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var runs = new List<CheckRun>(profile.Checks.Count);
        var exitCode = ExitCodes.Success;
        foreach (var checkId in profile.Checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_checks.TryGetValue(checkId, out var check))
            {
                throw new InvalidOperationException($"No implementation is registered for profile check '{checkId}'.");
            }

            var run = await check(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(run);
            run = CheckRunNormalizer.Normalize(run with { Check = checkId, Profile = profile.Name });
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(run);
            if (!run.Success && exitCode == ExitCodes.Success)
            {
                exitCode = run.ExitCode;
            }
        }

        return new(runs, exitCode);
    }
}
