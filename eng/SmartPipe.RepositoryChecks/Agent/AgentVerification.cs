using System.Text.Json;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Profiles;
using SmartPipe.RepositoryChecks.Reporting;

namespace SmartPipe.RepositoryChecks.Agent;

internal sealed record AgentVerificationResult(
    IReadOnlyList<CheckRun> CheckRuns,
    int ExitCode);

internal sealed class AgentTaskVerifier
{
    private readonly AgentContextBuilder _contextBuilder;
    private readonly Func<string, VerificationProfile, CancellationToken, Task<VerificationProfileRunResult>> _runProfile;

    public AgentTaskVerifier(
        AgentContextBuilder contextBuilder,
        Func<string, VerificationProfile, CancellationToken, Task<VerificationProfileRunResult>>? runProfile = null)
    {
        _contextBuilder = contextBuilder;
        _runProfile = runProfile ?? RunProfileAsync;
    }

    public async Task<AgentVerificationResult> VerifyAsync(
        string repositoryRoot,
        string epic,
        string task,
        ProfileOutputFormat format,
        bool failuresOnly,
        CancellationToken cancellationToken)
    {
        _ = format;
        _ = failuresOnly;
        var before = await _contextBuilder.BuildAsync(repositoryRoot, epic, task, cancellationToken).ConfigureAwait(false);
        var profile = await LoadProfileAsync(repositoryRoot, before.TaskDefinition.VerificationProfile, cancellationToken).ConfigureAwait(false);
        var profileResult = await _runProfile(repositoryRoot, profile, cancellationToken).ConfigureAwait(false);
        var runs = NormalizeRuns(profileResult.CheckRuns, before.TaskDefinition.VerificationProfile).ToList();
        var exitCode = profileResult.ExitCode;
        if (before.Context.Prerequisites.Any(item => item.Status != "satisfied"))
        {
            runs.Add(new CheckRun(
                "verify-task",
                before.TaskDefinition.VerificationProfile,
                false,
                ExitCodes.RepositorySnapshotMismatch,
                [new CheckDiagnostic("SPAGENT002", "A required prerequisite is not an ancestor of HEAD.")]));
            exitCode = ExitCodes.RepositorySnapshotMismatch;
        }
        var stale = false;
        try
        {
            var after = await _contextBuilder.BuildAsync(repositoryRoot, epic, task, cancellationToken).ConfigureAwait(false);
            stale = !SameState(before, after);
        }
        catch (Exception exception) when (exception is AgentPlanException or RepositoryCheckException or IOException or UnauthorizedAccessException)
        {
            stale = true;
        }

        if (stale)
        {
            runs.Add(StaleRun(before.TaskDefinition.VerificationProfile));
            exitCode = ExitCodes.RepositorySnapshotMismatch;
        }

        return new(runs, exitCode);
    }

    internal static IReadOnlyList<CheckRun> NormalizeRuns(
        IReadOnlyList<CheckRun> runs,
        string profile)
    {
        return runs.Select(run => CheckRunNormalizer.Normalize(run with
        {
            Profile = profile,
        })).ToArray();
    }

    internal static CheckRun StaleRun(string profile) => new(
        "verify-task",
        profile,
        false,
        ExitCodes.RepositorySnapshotMismatch,
        [new CheckDiagnostic("SPAGENT001", "Repository state changed during verification.")]);

    internal static async Task<VerificationProfile> LoadProfileAsync(
        string repositoryRoot,
        string profileName,
        CancellationToken cancellationToken)
    {
        VerificationProfileManifest manifest;
        try
        {
            manifest = await VerificationProfileManifestLoader.LoadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new RepositoryCheckException(ExitCodes.SchemaOrManifestInvalid, "Verification profile manifest is invalid.", exception);
        }

        return manifest.Profiles.FirstOrDefault(item =>
                   string.Equals(item.Name, profileName, StringComparison.Ordinal))
               ?? throw new RepositoryCheckException(ExitCodes.SchemaOrManifestInvalid, "Verification profile is not defined.");
    }

    private static bool SameState(AgentContextSnapshot before, AgentContextSnapshot after) =>
        before.Context.Head == after.Context.Head
        && before.Context.Branch == after.Context.Branch
        && before.Context.TreeFingerprint == after.Context.TreeFingerprint
        && before.Context.PlanSha256 == after.Context.PlanSha256
        && before.Context.ChangedPaths.SequenceEqual(after.Context.ChangedPaths, StringComparer.Ordinal);

    internal static async Task<VerificationProfileRunResult> RunProfileAsync(
        string repositoryRoot,
        VerificationProfile requested,
        CancellationToken cancellationToken)
    {
        return await new VerificationProfileRunner(VerificationProfileChecks.Create(repositoryRoot))
            .RunAsync(requested, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AgentEvidenceResult(
    AgentEvidence Evidence,
    int ExitCode);

internal sealed class AgentEvidenceService
{
    private readonly AgentContextBuilder _contextBuilder;
    private readonly Func<string, VerificationProfile, CancellationToken, Task<VerificationProfileRunResult>> _runProfile;

    public AgentEvidenceService(
        AgentContextBuilder contextBuilder,
        Func<string, VerificationProfile, CancellationToken, Task<VerificationProfileRunResult>>? runProfile = null)
    {
        _contextBuilder = contextBuilder;
        _runProfile = runProfile ?? AgentTaskVerifier.RunProfileAsync;
    }

    public async Task<AgentEvidenceResult> CollectAsync(
        string repositoryRoot,
        string epic,
        CancellationToken cancellationToken)
    {
        var before = await _contextBuilder.BuildAsync(repositoryRoot, epic, "T25", cancellationToken).ConfigureAwait(false);
        var profile = await AgentTaskVerifier.LoadProfileAsync(repositoryRoot, before.TaskDefinition.VerificationProfile, cancellationToken).ConfigureAwait(false);
        var profileResult = await _runProfile(repositoryRoot, profile, cancellationToken).ConfigureAwait(false);
        var checks = AgentTaskVerifier.NormalizeRuns(profileResult.CheckRuns, before.TaskDefinition.VerificationProfile)
            .Select(ToEvidenceCheck)
            .ToList();
        var exitCode = profileResult.ExitCode;
        var stale = false;
        AgentContextSnapshot state = before;
        try
        {
            state = await _contextBuilder.BuildAsync(repositoryRoot, epic, "T25", cancellationToken).ConfigureAwait(false);
            stale = !SameState(before, state);
        }
        catch (Exception exception) when (exception is AgentPlanException or RepositoryCheckException or IOException or UnauthorizedAccessException)
        {
            stale = true;
        }

        if (before.Context.Prerequisites.Any(item => item.Status != "satisfied"))
        {
            stale = true;
        }

        if (stale)
        {
            checks.Add(new AgentEvidenceCheck
            {
                Check = "verify-task",
                Success = false,
                ExitCode = ExitCodes.RepositorySnapshotMismatch,
            });
            exitCode = ExitCodes.RepositorySnapshotMismatch;
        }

        var evidence = new AgentEvidence
        {
            Epic = before.Context.Epic,
            Head = state.Context.Head,
            Base = before.Context.BaseCommit,
            Branch = state.Context.Branch,
            Clean = state.Context.Clean,
            ChangedPaths = state.Context.ChangedPaths,
            Fingerprint = state.Context.TreeFingerprint,
            PlanSha = state.Context.PlanSha256,
            Profile = before.Context.VerificationProfile,
            Status = exitCode == ExitCodes.Success ? "passed" : "failed",
            ExitCode = exitCode,
            Checks = checks,
        };
        return new(evidence, exitCode);
    }

    private static AgentEvidenceCheck ToEvidenceCheck(CheckRun run)
    {
        IReadOnlyDictionary<string, int>? counters = null;
        if (run.Counters is not null)
        {
            counters = new SortedDictionary<string, int>(
                run.Counters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }

        return new AgentEvidenceCheck
        {
            Check = run.Check,
            Success = run.Success,
            ExitCode = run.ExitCode,
            Counters = counters,
        };
    }

    private static bool SameState(AgentContextSnapshot before, AgentContextSnapshot after) =>
        before.Context.Head == after.Context.Head
        && before.Context.Branch == after.Context.Branch
        && before.Context.TreeFingerprint == after.Context.TreeFingerprint
        && before.Context.PlanSha256 == after.Context.PlanSha256
        && before.Context.ChangedPaths.SequenceEqual(after.Context.ChangedPaths, StringComparer.Ordinal);
}
