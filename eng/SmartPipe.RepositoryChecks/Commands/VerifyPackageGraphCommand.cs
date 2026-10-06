using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Consumers;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class VerifyPackageGraphCommand
{
    private readonly IEvaluatedProjectReader _projects;
    public VerifyPackageGraphCommand(IEvaluatedProjectReader? projects = null) => _projects = projects ?? new EvaluatedProjectReader();

    public async Task<PackageGraphValidationResult> ExecuteAsync(VerifyPackageGraphOptions options, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(options.RepositoryRoot, options.GraphPath, ct).ConfigureAwait(false);
        var violations = new List<PackageGraphViolation>();
        var validator = new PackageGraphValidator();
        var assetsReader = new AssetsGraphReader();
        var nuspecReader = new PackedNuspecReader();
        var graphPathToId = graph.Packages.ToDictionary(x => Path.GetFullPath(x.ProjectPath, options.RepositoryRoot), x => x.Id, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (options.Mode == PackageGraphMode.Release)
        {
            var scenarios = await new ConsumerScenarioLoader().LoadAsync(options.RepositoryRoot, "eng/consumer-scenarios.json", graph, ct).ConfigureAwait(false);
            var availableScenarioIds = scenarios.Scenarios.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            violations.AddRange((scenarios.RequiredAtRelease ?? []).Where(id => !availableScenarioIds.Contains(id))
                .Select(id => new PackageGraphViolation("SPCONS022", "consumer-scenarios", "scenario", id, "requiredAtRelease scenario is not implemented")));
            violations.AddRange(graph.Packages.Where(x => x.Lifecycle == PackageLifecycle.Planned)
                .Select(x => new PackageGraphViolation("SPGRAPH070", x.Id, "graph", null, "planned package must be activated before release")));
            violations.AddRange(graph.Packages.SelectMany(node => node.TemporaryAllowances.Where(x => x.ExpiresBeforeRelease)
                .Select(x => new PackageGraphViolation("SPGRAPH047", node.Id, "graph", x.Dependency, "temporary allowance must expire before release"))));
        }
        foreach (var node in graph.Packages.Where(x => x.Lifecycle != PackageLifecycle.Planned))
        {
            var project = await _projects.ReadAsync(Path.Combine(options.RepositoryRoot, node.ProjectPath), ct).ConfigureAwait(false);
            violations.AddRange(validator.ValidateProject(graph, node, project, options.Mode).Where(x => !(options.Mode == PackageGraphMode.Release && x.Code == "SPGRAPH047")));
            if (options.PackagesDirectory is null) continue;
            var directExternal = project.PackageReferences.Where(x => !string.Equals(x.PrivateAssets, "all", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directInternal = project.ProjectReferences.Select(path => graphPathToId.GetValueOrDefault(path)).Where(x => x is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedPacked = directExternal.Concat(directInternal).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var assetsPath = Path.Combine(Path.GetDirectoryName(project.ProjectPath)!, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
                violations.Add(new("SPGRAPH051", node.Id, "assets", null, "obj/project.assets.json must exist; run restore"));
            else
            {
                var restored = await assetsReader.ReadAsync(assetsPath, ct).ConfigureAwait(false);
                foreach (var framework in restored.Frameworks)
                {
                    violations.AddRange(PackageGraphValidator.ValidateRestoredProjectReferences(node.Id, directInternal, framework.DirectProjects));
                    foreach (var missing in directExternal.Except(framework.DirectPackages, StringComparer.OrdinalIgnoreCase))
                        violations.Add(new("SPGRAPH052", node.Id, $"assets:{framework.Target}", missing, "evaluated direct package must be direct in restore graph"));
                    foreach (var promoted in framework.DirectPackages.Except(project.PackageReferences.Select(x => x.Id), StringComparer.OrdinalIgnoreCase))
                        violations.Add(new("SPGRAPH053", node.Id, $"assets:{framework.Target}", promoted, "restore direct package must originate from evaluated PackageReference"));
                }
            }

            var expectedName = $"{node.Id}.{graph.ReleaseVersion}.nupkg";
            var matches = Directory.EnumerateFiles(options.PackagesDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
            {
                violations.Add(new("SPGRAPH061", node.Id, "nuspec", null, $"exactly one {expectedName} must exist"));
                continue;
            }
            var packed = await nuspecReader.ReadAsync(matches[0], ct).ConfigureAwait(false);
            var packedDependencies = packed.Groups.SelectMany(x => x.Dependencies).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            violations.AddRange(PackageGraphValidator.ValidatePackedDependencies(node.Id, expectedPacked, packedDependencies));
        }
        return new(violations.OrderBy(x => x.PackageId, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Dependency, StringComparer.Ordinal).ToArray());
    }
}
