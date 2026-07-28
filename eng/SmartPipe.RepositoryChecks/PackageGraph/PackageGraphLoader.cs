using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed class PackageGraphLoader
{
    private static readonly string[] CanonicalPackageIds =
    [
        "SmartPipe.Core", "SmartPipe.Extensions.Channels", "SmartPipe.Extensions.Transforms", "SmartPipe.Extensions.Logging",
        "SmartPipe.Extensions.Json", "SmartPipe.Extensions.Csv", "SmartPipe.Extensions.Dapper", "SmartPipe.Extensions.EntityFrameworkCore",
        "SmartPipe.Extensions.Mapster", "SmartPipe.Extensions.Polly", "SmartPipe.Extensions.Http", "SmartPipe.Testing",
        "SmartPipe.Extensions.Http.Json", "SmartPipe.Extensions.DependencyInjection", "SmartPipe.Extensions.OpenTelemetry",
        "SmartPipe.Extensions.Hosting", "SmartPipe.Extensions.HealthChecks", "SmartPipe.Extensions.DataAnnotations", "SmartPipe.Extensions",
    ];
    private readonly bool _enforceCanonicalCatalog;
    internal PackageGraphLoader(bool enforceCanonicalCatalog = true) => _enforceCanonicalCatalog = enforceCanonicalCatalog;
    public async Task<PackageGraphDocument> LoadAsync(string repositoryRoot, string graphPath, CancellationToken ct)
        => await ReadAsync(repositoryRoot, graphPath, requireCanonical: true, ct).ConfigureAwait(false);

    public async Task CanonicalizeAsync(string repositoryRoot, string graphPath, bool check, CancellationToken ct)
    {
        var graph = await ReadAsync(repositoryRoot, graphPath, requireCanonical: false, ct).ConfigureAwait(false);
        var path = Path.GetFullPath(graphPath, repositoryRoot);
        var canonical = CanonicalJson.Serialize(graph, RepositoryChecksJsonContext.Default.PackageGraphDocument);
        var current = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (current == canonical) return;
        if (check) throw new PackageGraphException("SPGRAPH002", "Package graph is not in canonical JSON form or package order.");
        await File.WriteAllTextAsync(path, canonical, new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    private async Task<PackageGraphDocument> ReadAsync(string repositoryRoot, string graphPath, bool requireCanonical, CancellationToken ct)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = Path.GetFullPath(graphPath, root);
        EnsureContained(root, path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) || bytes.Contains((byte)'\r'))
        {
            throw new PackageGraphException("SPGRAPH001", "Package graph must be UTF-8 without BOM and use LF line endings.");
        }

        RejectDuplicateProperties(bytes);
        PackageGraphDocument graph;
        try
        {
            graph = JsonSerializer.Deserialize(bytes, RepositoryChecksJsonContext.Default.PackageGraphDocument)
                ?? throw new JsonException("Document is null.");
        }
        catch (JsonException exception)
        {
            throw new PackageGraphException("SPGRAPH001", "Package graph JSON does not satisfy the strict schema.", exception);
        }

        Validate(root, graph, _enforceCanonicalCatalog);
        var canonical = CanonicalJson.Serialize(graph, RepositoryChecksJsonContext.Default.PackageGraphDocument);
        if (requireCanonical && !bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(canonical)))
        {
            throw new PackageGraphException("SPGRAPH002", "Package graph is not in canonical JSON form or package order.");
        }

        return graph;
    }

    private static void Validate(string root, PackageGraphDocument graph, bool enforceCanonicalCatalog)
    {
        if (graph.SchemaVersion != 1 || graph.ReleaseVersion != "2.2.0" || graph.Packages.Count == 0)
            throw new PackageGraphException("SPGRAPH003", "Unsupported schema/release version or empty package catalog.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var orders = new HashSet<int>();
        var knownIds = graph.Packages.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var previousOrder = 0;
        foreach (var node in graph.Packages)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) throw new PackageGraphException("SPGRAPH004", $"Duplicate/empty package ID '{node.Id}'.");
            if (node.PublishOrder <= previousOrder || !orders.Add(node.PublishOrder)) throw new PackageGraphException("SPGRAPH005", $"Publish order {node.PublishOrder} is duplicate or unordered.");
            previousOrder = node.PublishOrder;
            if (Path.IsPathRooted(node.ProjectPath) || node.ProjectPath.Contains('\\') || node.ProjectPath.Split('/').Any(x => x is "" or "." or ".."))
                throw new PackageGraphException("SPGRAPH006", $"Project path for {node.Id} must be repository-relative and normalized.");
            var fullProject = Path.GetFullPath(node.ProjectPath, root); EnsureContained(root, fullProject);
            if (!paths.Add(node.ProjectPath)) throw new PackageGraphException("SPGRAPH007", $"Duplicate project path '{node.ProjectPath}'.");
            if (node.Lifecycle is PackageLifecycle.Active or PackageLifecycle.CompatibilityFacade && !File.Exists(fullProject))
                throw new PackageGraphException("SPGRAPH008", $"Active package project does not exist: {node.ProjectPath}.");
            if (node.Lifecycle == PackageLifecycle.Planned && string.IsNullOrWhiteSpace(node.ActivationEpic))
                throw new PackageGraphException("SPGRAPH009", $"Planned package {node.Id} requires an activation epic.");
            if ((node.Lifecycle == PackageLifecycle.Planned) != (node.ScaffoldKind is not null))
                throw new PackageGraphException("SPGRAPH017", $"Only planned packages must declare scaffoldKind: {node.Id}.");
            if (node.Lifecycle == PackageLifecycle.Planned && node.BaselineVersion is not null)
                throw new PackageGraphException("SPGRAPH010", $"Planned package {node.Id} cannot declare a baseline version.");
            ValidatePolicy(node, node.CurrentDependencies, knownIds);
            ValidatePolicy(node, node.ReleaseDependencies, knownIds);
            foreach (var allowance in node.TemporaryAllowances)
            {
                if (string.IsNullOrWhiteSpace(allowance.Dependency) || string.IsNullOrWhiteSpace(allowance.Reason) || string.IsNullOrWhiteSpace(allowance.OwnerEpic) || string.IsNullOrWhiteSpace(allowance.Evidence))
                    throw new PackageGraphException("SPGRAPH012", $"Allowance for {node.Id} requires dependency, reason, owner epic and evidence.");
                if (!allowance.ExpiresBeforeRelease
                    && (node.Lifecycle != PackageLifecycle.CompatibilityFacade
                        || !node.ReleaseDependencies.AllowedExternalPackages.Contains(allowance.Dependency, StringComparer.OrdinalIgnoreCase)))
                    throw new PackageGraphException("SPGRAPH014", $"Non-expiring allowance {allowance.Dependency} is only valid for an evidence-backed facade release dependency.");
            }
        }
        if (enforceCanonicalCatalog && (graph.Packages.Count != CanonicalPackageIds.Length
            || !graph.Packages.Select(x => x.Id).SequenceEqual(CanonicalPackageIds, StringComparer.Ordinal)))
            throw new PackageGraphException("SPGRAPH016", "Package graph must contain the exact canonical 19 package IDs in publish order.");
        _ = TopologicalPackageSorter.Sort(graph.Packages.ToDictionary(
            x => x.Id,
            x => (IReadOnlyList<string>)x.CurrentDependencies.RequiredSmartPipePackages.Concat(x.CurrentDependencies.AllowedSmartPipePackages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase));
        _ = TopologicalPackageSorter.Sort(graph.Packages.ToDictionary(
            x => x.Id,
            x => (IReadOnlyList<string>)x.ReleaseDependencies.RequiredSmartPipePackages.Concat(x.ReleaseDependencies.AllowedSmartPipePackages).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase));
    }

    private static void ValidatePolicy(PackageNode node, DependencyPolicy policy, HashSet<string> knownIds)
    {
        foreach (var dependency in policy.RequiredSmartPipePackages.Concat(policy.AllowedSmartPipePackages))
        {
            if (dependency.Equals(node.Id, StringComparison.OrdinalIgnoreCase)) throw new PackageGraphException("SPGRAPH011", $"Package {node.Id} cannot depend on itself.");
            if (!knownIds.Contains(dependency)) throw new PackageGraphException("SPGRAPH013", $"Package {node.Id} references unknown SmartPipe dependency {dependency}.");
            if (IsInvariantForbidden(node.Id, dependency)) throw new PackageGraphException("SPGRAPH015", $"Invariant architecture edge is forbidden: {node.Id} -> {dependency}.");
        }
    }

    internal static bool IsInvariantForbidden(string packageId, string dependency) =>
        packageId.Equals("SmartPipe.Core", StringComparison.OrdinalIgnoreCase) && dependency.StartsWith("SmartPipe.Extensions", StringComparison.OrdinalIgnoreCase)
        || !packageId.Equals("SmartPipe.Extensions", StringComparison.OrdinalIgnoreCase) && dependency.Equals("SmartPipe.Extensions", StringComparison.OrdinalIgnoreCase)
        || packageId.Equals("SmartPipe.Extensions.Http", StringComparison.OrdinalIgnoreCase) && dependency.Equals("SmartPipe.Extensions.Polly", StringComparison.OrdinalIgnoreCase)
        || packageId.Equals("SmartPipe.Extensions.HealthChecks", StringComparison.OrdinalIgnoreCase) && dependency.Equals("SmartPipe.Extensions.Hosting", StringComparison.OrdinalIgnoreCase);

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            var stack = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!))
                    throw new PackageGraphException("SPGRAPH001", $"Duplicate JSON property '{reader.GetString()}'.");
            }
        }
        catch (JsonException exception) { throw new PackageGraphException("SPGRAPH001", "Package graph JSON is malformed.", exception); }
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PackageGraphException("SPGRAPH006", "Package graph path escapes the repository.");
    }
}
