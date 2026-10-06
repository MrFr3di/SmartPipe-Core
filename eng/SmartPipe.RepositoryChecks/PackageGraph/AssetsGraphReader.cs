using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed record RestoredFrameworkGraph(string Target, IReadOnlyList<string> DirectPackages, IReadOnlyList<string> DirectProjects, IReadOnlyList<string> TransitivePackages, IReadOnlyList<string> FrameworkReferences, IReadOnlyList<string> PrunedPackages);
internal sealed record RestoredDependencyGraph(IReadOnlyList<RestoredFrameworkGraph> Frameworks);
internal interface IAssetsGraphReader { Task<RestoredDependencyGraph> ReadAsync(string assetsFile, CancellationToken ct); }

internal sealed record AssetsDocument
{
    public required int Version { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, AssetsTargetLibrary>> Targets { get; init; }
    public required IReadOnlyDictionary<string, AssetsLibrary> Libraries { get; init; }
    public required AssetsProject Project { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ProjectFileDependencyGroups { get; init; }
}
internal sealed record AssetsTargetLibrary { public IReadOnlyDictionary<string, string>? Dependencies { get; init; } }
internal sealed record AssetsLibrary { public required string Type { get; init; } }
internal sealed record AssetsProject { public required IReadOnlyDictionary<string, AssetsFramework> Frameworks { get; init; } }
internal sealed record AssetsFramework
{
    public IReadOnlyDictionary<string, JsonElement>? Dependencies { get; init; }
    public IReadOnlyDictionary<string, JsonElement>? FrameworkReferences { get; init; }
}

internal sealed class AssetsGraphReader : IAssetsGraphReader
{
    public async Task<RestoredDependencyGraph> ReadAsync(string assetsFile, CancellationToken ct)
    {
        await using var stream = new FileStream(assetsFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        AssetsDocument document;
        try { document = await JsonSerializer.DeserializeAsync(stream, AssetsJsonContext.Default.AssetsDocument, ct).ConfigureAwait(false) ?? throw new JsonException("null assets document"); }
        catch (JsonException exception) { throw new PackageGraphException("SPGRAPH050", $"project.assets.json is malformed or incomplete: {exception.Message}", exception); }
        if (document.Version is not (3 or 4)) throw new PackageGraphException("SPGRAPH050", $"Unsupported project.assets.json version {document.Version}.");
        var groups = new List<RestoredFrameworkGraph>();
        foreach (var target in document.Targets.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var frameworkName = target.Key.Split('/')[0];
            document.Project.Frameworks.TryGetValue(frameworkName, out var framework);
            var declared = framework?.Dependencies ?? new Dictionary<string, JsonElement>();
            var autoReferenced = declared.Where(x => IsAutoReferenced(x.Value)).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var direct = declared.Where(x => DependencyTarget(x.Value) == "Package" && !IsAutoReferenced(x.Value)).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directProjects = declared.Where(x => DependencyTarget(x.Value) == "Project").Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (document.ProjectFileDependencyGroups?.TryGetValue(frameworkName, out var fileDependencies) == true)
                foreach (var dependency in fileDependencies.Select(ParseDependencyId).Where(id => HasResolvedLibraryType(document, id, "project")))
                    directProjects.Add(dependency);
            var allPackages = target.Value.Keys.Where(key => document.Libraries.TryGetValue(key, out var library) && library.Type == "package")
                .Select(key => key[..key.LastIndexOf('/')]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            groups.Add(new(target.Key,
                direct.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                directProjects.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                allPackages.Except(direct, StringComparer.OrdinalIgnoreCase).Except(autoReferenced, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                (framework?.FrameworkReferences?.Keys ?? []).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                direct.Except(allPackages, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        return new(groups);
    }

    private static string DependencyTarget(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("target", out var target)
            ? target.GetString() ?? "Package"
            : "Package";
    private static bool IsAutoReferenced(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("autoReferenced", out var auto) && auto.ValueKind == JsonValueKind.True;
    private static string ParseDependencyId(string value)
    {
        var separator = value.IndexOf(" >=", StringComparison.Ordinal);
        return separator > 0 ? value[..separator] : value;
    }
    private static bool HasResolvedLibraryType(AssetsDocument document, string id, string type) => document.Libraries.Any(x =>
        x.Key.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase) && x.Value.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AssetsDocument))]
internal partial class AssetsJsonContext : JsonSerializerContext;
