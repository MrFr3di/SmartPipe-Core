using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Ownership;

internal sealed class OwnershipValidator
{
    public OwnershipResult Validate(OwnershipDocument document, PackageGraphDocument graph, TypeOwnershipSnapshot baseline, TypeOwnershipSnapshot current, PackageGraphMode mode)
    {
        var errors = new List<OwnershipViolation>();
        var nodes = graph.Packages.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var baselineTypes = baseline.Implementations.Keys.Concat(baseline.Forwarders.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var duplicate in current.Implementations.Where(x => x.Value.Count > 1)) errors.Add(new("SPOWN020", duplicate.Key, $"type is implemented by multiple packages: {string.Join(",", duplicate.Value)}"));
        foreach (var type in baselineTypes)
        {
            OwnershipAssignment assignment;
            try { assignment = OwnershipResolver.Resolve(type, document.Assignments); }
            catch (OwnershipException exception) { errors.Add(new(exception.Code, type, exception.Message)); continue; }
            var baselineOwners = (baseline.Implementations.GetValueOrDefault(type) ?? new HashSet<string>())
                .Concat(baseline.Forwarders.GetValueOrDefault(type) ?? new HashSet<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!baselineOwners.Contains(assignment.BaselineAssembly)) errors.Add(new("SPOWN021", type, $"baseline assembly {assignment.BaselineAssembly} does not expose the type"));
            var targetNode = nodes[assignment.TargetImplementationAssembly];
            var future = mode == PackageGraphMode.Current && targetNode.Lifecycle == PackageLifecycle.Planned;
            if (future)
            {
                RequireImplementation(assignment.CurrentImplementationAssembly, "current implementation missing before activation");
                continue;
            }
            switch (assignment.Strategy)
            {
                case OwnershipStrategy.Stay:
                    RequireImplementation(assignment.TargetImplementationAssembly, "stay target implementation missing"); break;
                case OwnershipStrategy.TypeForward:
                    RequireImplementation(assignment.TargetImplementationAssembly, "type-forward target implementation missing");
                    if (assignment.CompatibilityAssembly is null || !Has(current.Forwarders, type, assignment.CompatibilityAssembly)) errors.Add(new("SPOWN022", type, "compatibility assembly forwarder missing"));
                    break;
                case OwnershipStrategy.ObsoleteWrapper:
                    var wrapper = assignment.CompatibilityAssembly ?? assignment.CurrentImplementationAssembly;
                    RequireImplementation(wrapper, "obsolete wrapper implementation missing");
                    if (Has(current.Forwarders, type, wrapper)) errors.Add(new("SPOWN023", type, "obsolete wrapper must not also be a forwarder"));
                    break;
            }
            void RequireImplementation(string package, string rule) { if (!Has(current.Implementations, type, package)) errors.Add(new("SPOWN024", type, rule + $" ({package})")); }
        }
        return new(baselineTypes.Length, errors.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray());
    }
    private static bool Has(IReadOnlyDictionary<string, IReadOnlySet<string>> map, string type, string package) => map.TryGetValue(type, out var owners) && owners.Contains(package);
}
