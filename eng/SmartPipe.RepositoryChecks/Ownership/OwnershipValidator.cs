using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Ownership;

internal sealed class OwnershipValidator
{
    public OwnershipResult Validate(OwnershipDocument document, PackageGraphDocument graph, TypeOwnershipSnapshot baseline, TypeOwnershipSnapshot current, PackageGraphMode mode)
    {
        var errors = new List<OwnershipViolation>();
        var nodes = graph.Packages.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var baselineTypes = baseline.Implementations.Keys.Concat(baseline.Forwarders.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var resolved = new Dictionary<string, OwnershipAssignment>(StringComparer.Ordinal);
        foreach (var duplicate in current.Implementations.Where(x => x.Value.Count > 1)) errors.Add(new("SPOWN020", duplicate.Key, $"type is implemented by multiple packages: {string.Join(",", duplicate.Value)}"));
        foreach (var type in baselineTypes)
        {
            OwnershipAssignment assignment;
            try { assignment = OwnershipResolver.Resolve(type, document.Assignments); }
            catch (OwnershipException exception) { errors.Add(new(exception.Code, type, exception.Message)); continue; }
            resolved.Add(type, assignment);
            var baselineOwners = (baseline.Implementations.GetValueOrDefault(type) ?? new HashSet<string>())
                .Concat(baseline.Forwarders.GetValueOrDefault(type) ?? new HashSet<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!baselineOwners.Contains(assignment.BaselineAssembly)) errors.Add(new("SPOWN021", type, $"baseline assembly {assignment.BaselineAssembly} does not expose the type"));
            if (assignment.Strategy == OwnershipStrategy.Removed)
            {
                if (HasAny(current.Implementations, type) || HasAny(current.Forwarders, type))
                    errors.Add(new("SPOWN025", type, "removed type is still present in the current package graph"));
                continue;
            }

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
        ValidateFacadeSurface(graph, current, resolved, mode, errors);
        ValidateAssets(current, resolved, nodes, mode, errors);
        return new(baselineTypes.Length, errors.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Rule, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateFacadeSurface(PackageGraphDocument graph, TypeOwnershipSnapshot current,
        IReadOnlyDictionary<string, OwnershipAssignment> resolved, PackageGraphMode mode, List<OwnershipViolation> errors)
    {
        foreach (var facadeId in graph.Packages.Where(node => node.Lifecycle == PackageLifecycle.CompatibilityFacade).Select(node => node.Id))
        {
            var implementations = resolved.Where(pair => ImplementationOwner(pair.Value, graph.Packages, mode) == facadeId)
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            var forwarders = resolved.Where(pair => pair.Value.Strategy == OwnershipStrategy.TypeForward
                    && pair.Value.CompatibilityAssembly == facadeId
                    && ImplementationOwner(pair.Value, graph.Packages, mode) != facadeId)
                .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var type in current.Implementations.Where(pair => pair.Value.Contains(facadeId)).Select(pair => pair.Key).Except(implementations))
                errors.Add(new("SPOWN027", type, "unexpected facade public implementation"));
            foreach (var type in current.Forwarders.Where(pair => pair.Value.Contains(facadeId)).Select(pair => pair.Key).Except(forwarders))
                errors.Add(new("SPOWN028", type, "unexpected facade forwarder"));
        }
    }

    private static void ValidateAssets(TypeOwnershipSnapshot current, IReadOnlyDictionary<string, OwnershipAssignment> resolved,
        IReadOnlyDictionary<string, PackageNode> nodes, PackageGraphMode mode, List<OwnershipViolation> errors)
    {
        var assets = current.Assets ?? [];
        foreach (var asset in assets)
        {
            var assembly = asset.Assembly;
            foreach (var (type, assignment) in resolved)
            {
                var owner = ImplementationOwner(assignment, nodes.Values, mode);
                if (owner == asset.PackageId && !assembly.ExportedTypes.Contains(type, StringComparer.Ordinal))
                    errors.Add(new("SPOWN029", type, $"implementation missing in {asset.PackageId}:{assembly.AssetPath}"));
                if (assignment.Strategy != OwnershipStrategy.TypeForward || owner == assignment.CompatibilityAssembly
                    || assignment.CompatibilityAssembly != asset.PackageId) continue;
                ValidateForwarderAsset(asset, type, owner, assets, errors);
            }
        }
    }

    private static void ValidateForwarderAsset(OwnedAssemblySnapshot asset, string type, string? owner,
        IReadOnlyList<OwnedAssemblySnapshot> assets, List<OwnershipViolation> errors)
    {
        var assembly = asset.Assembly;
        if (!assembly.TypeForwarders.Contains(type, StringComparer.Ordinal))
        {
            errors.Add(new("SPOWN029", type, $"forwarder missing in {asset.PackageId}:{assembly.AssetPath}"));
            return;
        }
        if (!assembly.ForwarderDestinations.TryGetValue(type, out var destination) || destination.Name != owner)
            errors.Add(new("SPOWN026", type, $"forwarder in {assembly.AssetPath} must target {owner}; observed {destination?.ToString() ?? "missing AssemblyRef"}"));
        var targetAssets = FindImplementationAssets(assets, owner, assembly);
        if (targetAssets.Length != 1 || !targetAssets[0].Assembly.ExportedTypes.Contains(type, StringComparer.Ordinal))
            errors.Add(new("SPOWN029", type, $"unique implementation asset for {owner} missing for {assembly.AssetPath}"));
        else if (destination is not null && !destination.CanBindTo(targetAssets[0].Assembly))
            errors.Add(new("SPOWN026", type, $"forwarder in {assembly.AssetPath} cannot bind to {owner}:{targetAssets[0].Assembly.AssetPath}; observed {destination}"));
    }

    private static OwnedAssemblySnapshot[] FindImplementationAssets(IReadOnlyList<OwnedAssemblySnapshot> assets,
        string? owner, PackageAssemblySnapshot assembly)
    {
        var targets = assets.Where(target => target.PackageId == owner
            && target.Assembly.Name == owner && target.Assembly.TargetFramework == assembly.TargetFramework
            && target.Assembly.AssetFamily == assembly.AssetFamily).ToArray();
        if (targets.Length == 0 && assembly.AssetFamily == "ref")
            return assets.Where(target => target.PackageId == owner && target.Assembly.Name == owner
                && target.Assembly.TargetFramework == assembly.TargetFramework && target.Assembly.AssetFamily == "lib").ToArray();
        return targets;
    }

    private static string? ImplementationOwner(OwnershipAssignment assignment, IEnumerable<PackageNode> nodes, PackageGraphMode mode)
    {
        if (assignment.Strategy == OwnershipStrategy.Removed) return null;
        if (mode == PackageGraphMode.Current && nodes.Any(node => node.Id == assignment.TargetImplementationAssembly && node.Lifecycle == PackageLifecycle.Planned))
            return assignment.CurrentImplementationAssembly;
        return assignment.Strategy == OwnershipStrategy.ObsoleteWrapper
            ? assignment.CompatibilityAssembly ?? assignment.CurrentImplementationAssembly : assignment.TargetImplementationAssembly;
    }
    private static bool Has(IReadOnlyDictionary<string, IReadOnlySet<string>> map, string type, string package) => map.TryGetValue(type, out var owners) && owners.Contains(package);
    private static bool HasAny(IReadOnlyDictionary<string, IReadOnlySet<string>> map, string type) => map.TryGetValue(type, out var owners) && owners.Count != 0;
}
