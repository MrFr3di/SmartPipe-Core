using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Ownership;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true)]
[JsonSerializable(typeof(OwnershipDocument))]
internal partial class OwnershipJsonContext : JsonSerializerContext;

internal sealed class OwnershipLoader
{
    public async Task<OwnershipDocument> LoadAsync(string root, string path, PackageGraphDocument graph, CancellationToken ct)
    {
        var full = Path.GetFullPath(path, root); var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) || bytes.Contains((byte)'\r')) throw new OwnershipException("SPOWN010", "Ownership JSON must be UTF-8 without BOM and LF-only.");
        OwnershipDocument document;
        try { document = JsonSerializer.Deserialize(bytes, OwnershipJsonContext.Default.OwnershipDocument) ?? throw new JsonException("null"); }
        catch (JsonException exception) { throw new OwnershipException("SPOWN010", "Ownership JSON is malformed or has unknown properties.", exception); }
        Validate(document, graph);
        if (!bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(document, OwnershipJsonContext.Default.OwnershipDocument)))) throw new OwnershipException("SPOWN011", "Ownership JSON is not canonical.");
        return document;
    }

    public async Task CanonicalizeAsync(string root, string path, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", ct).ConfigureAwait(false);
        var full = Path.GetFullPath(path, root); var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize(bytes, OwnershipJsonContext.Default.OwnershipDocument) ?? throw new OwnershipException("SPOWN010", "Ownership JSON is null.");
        Validate(document, graph);
        var canonical = document with { Assignments = document.Assignments.OrderBy(x => x.TypePattern, StringComparer.Ordinal).ToArray() };
        await File.WriteAllTextAsync(full, CanonicalJson.Serialize(canonical, OwnershipJsonContext.Default.OwnershipDocument), new UTF8Encoding(false), ct).ConfigureAwait(false);
    }

    internal static void Validate(OwnershipDocument document, PackageGraphDocument graph)
    {
        if (document.SchemaVersion != 1 || document.Assignments.Count == 0) throw new OwnershipException("SPOWN012", "Unsupported ownership schema or empty assignments.");
        var packages = graph.Packages.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.Assignments)
        {
            if (string.IsNullOrWhiteSpace(item.TypePattern) || string.IsNullOrWhiteSpace(item.Evidence) || !item.NamespacePreserved) throw new OwnershipException("SPOWN013", $"Ownership assignment {item.TypePattern} lacks evidence or namespace preservation.");
            foreach (var id in new[] { item.BaselineAssembly, item.CurrentImplementationAssembly, item.TargetImplementationAssembly }.Append(item.CompatibilityAssembly).Where(x => x is not null))
                if (!packages.ContainsKey(id!)) throw new OwnershipException("SPOWN014", $"Ownership assignment {item.TypePattern} references unknown package {id}.");
            if (item.TypePattern.StartsWith("SmartPipe.Core.", StringComparison.Ordinal) && item.TargetImplementationAssembly.StartsWith("SmartPipe.Extensions", StringComparison.Ordinal)) throw new OwnershipException("SPOWN015", "Core type cannot target Extensions.");
            if ((item.TypePattern.StartsWith("SmartPipe.Extensions.Selectors.Http", StringComparison.Ordinal) || item.TypePattern.StartsWith("SmartPipe.Extensions.Sinks.Http", StringComparison.Ordinal)) && item.TargetImplementationAssembly != "SmartPipe.Extensions") throw new OwnershipException("SPOWN016", "Legacy HTTP APIs must remain facade wrappers.");
            var target = packages[item.TargetImplementationAssembly];
            if (target.Lifecycle == PackageLifecycle.Planned && !item.MigrationEpic.Equals(target.ActivationEpic, StringComparison.Ordinal)) throw new OwnershipException("SPOWN017", $"Migration epic for {item.TypePattern} must match {target.ActivationEpic}.");
        }
    }
}
