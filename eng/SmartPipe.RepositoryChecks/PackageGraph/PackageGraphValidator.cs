namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed record PackageGraphValidationResult(IReadOnlyList<PackageGraphViolation> Violations)
{
    public bool Success => Violations.Count == 0;
}

internal sealed class PackageGraphValidator
{
    public IReadOnlyList<PackageGraphViolation> ValidateProject(
        PackageGraphDocument graph, PackageNode node, EvaluatedProject project, PackageGraphMode mode)
    {
        var violations = new List<PackageGraphViolation>();
        void Add(string code, string? dependency, string rule) => violations.Add(new(code, node.Id, "project", dependency, rule));
        if (!project.SmartPipePackage || !project.IsPackable) Add("SPGRAPH040", null, "official project must evaluate SmartPipePackage=true and IsPackable=true");
        if (!project.PackageId.Equals(node.Id, StringComparison.Ordinal)) Add("SPGRAPH041", null, $"PackageId must equal {node.Id}");
        if (!project.Version.Equals(graph.ReleaseVersion, StringComparison.Ordinal) || !project.PackageVersion.Equals(graph.ReleaseVersion, StringComparison.Ordinal))
            Add("SPGRAPH042", null, $"Version and PackageVersion must equal {graph.ReleaseVersion}");

        var projectByPath = graph.Packages.ToDictionary(x => Path.GetFullPath(x.ProjectPath, Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(project.ProjectPath)))!), x => x.Id, PathComparer());
        var actualInternal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in project.ProjectReferences)
        {
            if (!projectByPath.TryGetValue(reference, out var dependency)) Add("SPGRAPH043", reference, "ProjectReference must resolve to a graph package");
            else actualInternal.Add(dependency);
        }

        var policy = mode == PackageGraphMode.Current ? node.CurrentDependencies : node.ReleaseDependencies;
        ValidateSet(policy.RequiredSmartPipePackages, actualInternal, "SPGRAPH044", "required SmartPipe dependency missing");
        foreach (var dependency in actualInternal)
        {
            if (!policy.RequiredSmartPipePackages.Concat(policy.AllowedSmartPipePackages).Contains(dependency, StringComparer.OrdinalIgnoreCase))
                Add("SPGRAPH045", dependency, "SmartPipe dependency is not allowed");
            if (policy.ForbiddenPackagePatterns.Any(pattern => Matches(pattern, dependency)))
                Add("SPGRAPH048", dependency, $"dependency matches forbidden pattern {policy.ForbiddenPackagePatterns.First(pattern => Matches(pattern, dependency))}");
            if (PackageGraphLoader.IsInvariantForbidden(node.Id, dependency))
                Add("SPGRAPH049", dependency, "dependency violates an invariant architecture edge");
        }

        var actualExternal = project.PackageReferences.Where(x => !string.Equals(x.PrivateAssets, "all", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var temporary = mode == PackageGraphMode.Current ? node.TemporaryAllowances.Select(x => x.Dependency) : [];
        foreach (var dependency in actualExternal)
            if (!policy.AllowedExternalPackages.Contains(dependency, StringComparer.OrdinalIgnoreCase) && !temporary.Contains(dependency, StringComparer.OrdinalIgnoreCase))
                Add("SPGRAPH046", dependency, mode == PackageGraphMode.Release ? "external dependency is not release-allowed" : "external dependency is not current-allowed or evidenced");
        if (mode == PackageGraphMode.Release)
            foreach (var allowance in node.TemporaryAllowances.Where(x => x.ExpiresBeforeRelease))
                Add("SPGRAPH047", allowance.Dependency, "temporary allowance must expire before release");
        return violations;

        void ValidateSet(IEnumerable<string> required, HashSet<string> actual, string code, string rule)
        {
            foreach (var dependency in required) if (!actual.Contains(dependency)) Add(code, dependency, rule);
        }
    }

    public static IReadOnlyList<PackageGraphViolation> ValidatePackedDependencies(string packageId, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSet = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedSet.Except(actualSet, StringComparer.OrdinalIgnoreCase).Select(x => new PackageGraphViolation("SPGRAPH062", packageId, "nuspec", x, "evaluated direct dependency must be packed"))
            .Concat(actualSet.Except(expectedSet, StringComparer.OrdinalIgnoreCase).Select(x => new PackageGraphViolation("SPGRAPH063", packageId, "nuspec", x, "packed dependency must not be promoted from transitive restore state"))).ToArray();
    }

    public static IReadOnlyList<PackageGraphViolation> ValidateRestoredProjectReferences(string packageId, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var expectedSet = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualSet = actual.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedSet.Except(actualSet, StringComparer.OrdinalIgnoreCase).Select(x => new PackageGraphViolation("SPGRAPH054", packageId, "assets", x, "evaluated ProjectReference must be direct project dependency in assets"))
            .Concat(actualSet.Except(expectedSet, StringComparer.OrdinalIgnoreCase).Select(x => new PackageGraphViolation("SPGRAPH055", packageId, "assets", x, "assets direct project must originate from evaluated ProjectReference"))).ToArray();
    }

    private static bool Matches(string pattern, string value) => pattern.EndsWith('*')
        ? value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
