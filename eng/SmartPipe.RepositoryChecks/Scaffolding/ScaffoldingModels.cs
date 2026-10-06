namespace SmartPipe.RepositoryChecks.Scaffolding;

internal sealed record ScaffoldFile(string RelativePath, string Content);

internal sealed record PackageScaffoldPlan(
    string PackageId,
    PackageGraph.PackageScaffoldKind Kind,
    IReadOnlyList<ScaffoldFile> Files,
    IReadOnlyList<string> RequiredSteps);

internal sealed record ScaffoldReport(
    bool Success,
    string PackageId,
    PackageGraph.PackageScaffoldKind Kind,
    bool DryRun,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> RequiredSteps);

internal sealed class ScaffoldException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
