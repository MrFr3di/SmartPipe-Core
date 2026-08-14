using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Agent;

internal sealed record AgentPrerequisiteStatus
{
    [JsonPropertyOrder(0)]
    public required string Epic { get; init; }

    [JsonPropertyOrder(1)]
    public required string Commit { get; init; }

    [JsonPropertyOrder(2)]
    public required string Status { get; init; }
}

internal sealed record AgentReadSlice
{
    [JsonPropertyOrder(0)]
    public required string Path { get; init; }

    [JsonPropertyOrder(1)]
    public required string Section { get; init; }
}

internal sealed record AgentContext
{
    [JsonPropertyOrder(0)]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyOrder(1)]
    public required string Epic { get; init; }

    [JsonPropertyOrder(2)]
    public required string Task { get; init; }

    [JsonPropertyOrder(3)]
    public required string BaseRef { get; init; }

    [JsonPropertyOrder(4)]
    public required string BaseCommit { get; init; }

    [JsonPropertyOrder(5)]
    public required string Head { get; init; }

    [JsonPropertyOrder(6)]
    public required string Branch { get; init; }

    [JsonPropertyOrder(7)]
    public bool Clean { get; init; }

    [JsonPropertyOrder(8)]
    public required IReadOnlyList<string> ChangedPaths { get; init; }

    [JsonPropertyOrder(9)]
    public required string TreeFingerprint { get; init; }

    [JsonPropertyOrder(10)]
    public required string PlanSha256 { get; init; }

    [JsonPropertyOrder(11)]
    public required IReadOnlyList<AgentPrerequisiteStatus> Prerequisites { get; init; }

    [JsonPropertyOrder(12)]
    public required IReadOnlyList<string> AllowedPaths { get; init; }

    [JsonPropertyOrder(13)]
    public required IReadOnlyList<string> Contracts { get; init; }

    [JsonPropertyOrder(14)]
    public required AgentReadSlice Read { get; init; }

    [JsonPropertyOrder(15)]
    public required string VerificationProfile { get; init; }
}

internal sealed record AgentContextSnapshot(
    AgentContext Context,
    ActiveExecPlan Plan,
    AgentTaskDefinition TaskDefinition,
    AgentRepositoryState State);

internal sealed class AgentContextBuilder
{
    private readonly ActiveExecPlanLoader _planLoader;
    private readonly AgentRepositoryStateReader _stateReader;
    private readonly AgentPrerequisiteReader _prerequisiteReader;
    private readonly IProcessRunner _processRunner;

    public AgentContextBuilder(IProcessRunner? processRunner = null)
    {
        var runner = processRunner ?? new ProcessRunner();
        _processRunner = runner;
        _planLoader = new ActiveExecPlanLoader();
        _stateReader = new AgentRepositoryStateReader(runner);
        _prerequisiteReader = new AgentPrerequisiteReader(runner);
    }

    public async Task<AgentContextSnapshot> BuildAsync(
        string repositoryRoot,
        string epic,
        string task,
        CancellationToken cancellationToken)
    {
        var plan = await _planLoader.LoadAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(plan.Epic, epic, StringComparison.Ordinal))
        {
            throw new AgentPlanException("The requested epic is not defined in the active ExecPlan.");
        }

        var taskDefinition = plan.FindTask(task);
        var state = await _stateReader.CaptureAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        AgentScopeValidator.RequireWithinEpicScope(plan, state.ChangedPaths);
        await VerifyBaseReferenceAsync(repositoryRoot, plan, cancellationToken).ConfigureAwait(false);

        var prerequisites = new List<AgentPrerequisiteStatus>(plan.Prerequisites.Count);
        foreach (var prerequisite in plan.Prerequisites)
        {
            var result = await _prerequisiteReader.ReadAsync(repositoryRoot, prerequisite, cancellationToken).ConfigureAwait(false);
            prerequisites.Add(new()
            {
                Epic = result.Epic,
                Commit = result.Commit,
                Status = result.Status,
            });
        }

        var trackedPlanPath = plan.TrackedPlan.Path.Replace('\\', '/');
        var context = new AgentContext
        {
            Epic = plan.Epic,
            Task = taskDefinition.Id,
            BaseRef = plan.BaseRef,
            BaseCommit = plan.BaseCommit,
            Head = state.Head,
            Branch = state.Branch,
            Clean = state.Clean,
            ChangedPaths = state.ChangedPaths,
            TreeFingerprint = state.TreeFingerprint,
            PlanSha256 = plan.PlanSha256,
            Prerequisites = prerequisites,
            AllowedPaths = taskDefinition.AllowedPaths,
            Contracts = taskDefinition.Contracts,
            Read = new AgentReadSlice { Path = trackedPlanPath, Section = plan.TrackedPlan.Section },
            VerificationProfile = taskDefinition.VerificationProfile,
        };

        return new(context, plan, taskDefinition, state);
    }

    private async Task VerifyBaseReferenceAsync(
        string repositoryRoot,
        ActiveExecPlan plan,
        CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest("git", ["-C", root, "rev-parse", "--verify", plan.BaseRef], TimeSpan.FromMinutes(2)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("git base reference verification was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git base reference verification failed.", exception);
        }

        if (result.ExitCode != 0)
        {
            throw new RepositoryCheckException(ExitCodes.RepositorySnapshotMismatch, "The declared base ref could not be resolved.");
        }

        var resolved = result.StandardOutput.Trim();
        if (!string.Equals(resolved, plan.BaseCommit, StringComparison.Ordinal))
        {
            throw new RepositoryCheckException(ExitCodes.RepositorySnapshotMismatch, "The declared base ref does not resolve to the declared base commit.");
        }
    }
}

internal static class AgentScopeValidator
{
    public static void RequireWithinEpicScope(ActiveExecPlan plan, IReadOnlyList<string> changedPaths)
    {
        var allowed = plan.Tasks.SelectMany(static task => task.AllowedPaths).ToArray();
        var outside = changedPaths.FirstOrDefault(path => !allowed.Any(pattern => Matches(pattern, path)));
        if (outside is not null)
        {
            throw new RepositoryCheckException(
                ExitCodes.RepositorySnapshotMismatch,
                "Repository changes are outside the active epic scope.");
        }
    }

    private static bool Matches(string pattern, string path)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^3];
            return path.StartsWith(prefix + "/", StringComparison.Ordinal)
                || path.Equals(prefix, StringComparison.Ordinal);
        }

        return string.Equals(pattern, path, StringComparison.Ordinal);
    }
}
