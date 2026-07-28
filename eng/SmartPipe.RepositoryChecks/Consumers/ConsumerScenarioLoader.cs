using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed class ConsumerScenarioLoader
{
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(30);

    public async Task<ConsumerScenarioDocument> LoadAsync(string repositoryRoot, string manifestPath, PackageGraphDocument graph, CancellationToken ct)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var path = ResolveContained(root, manifestPath, "manifest");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) || bytes.Contains((byte)'\r'))
            throw new ConsumerScenarioException("SPCONS001", "Consumer scenario manifest must be UTF-8 without BOM and use LF line endings.");
        RejectDuplicateProperties(bytes);
        ConsumerScenarioDocument document;
        try
        {
            document = JsonSerializer.Deserialize(bytes, RepositoryChecksJsonContext.Default.ConsumerScenarioDocument)
                ?? throw new JsonException("Document is null.");
        }
        catch (JsonException exception)
        {
            throw new ConsumerScenarioException("SPCONS001", "Consumer scenario manifest does not satisfy the strict schema.", exception);
        }
        Validate(root, document, graph);
        return document;
    }

    private static void Validate(string root, ConsumerScenarioDocument document, PackageGraphDocument graph)
    {
        if (document.SchemaVersion != 1 || document.Scenarios.Count == 0) throw new ConsumerScenarioException("SPCONS002", "Unsupported schema version or empty scenarios.");
        var requiredAtRelease = document.RequiredAtRelease;
        if (requiredAtRelease.Any(string.IsNullOrWhiteSpace) || requiredAtRelease.Distinct(StringComparer.Ordinal).Count() != requiredAtRelease.Count)
            throw new ConsumerScenarioException("SPCONS003", "requiredAtRelease must contain unique non-empty scenario IDs.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var graphIds = graph.Packages.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var scenariosById = new Dictionary<string, ConsumerScenario>(StringComparer.Ordinal);
        foreach (var scenario in document.Scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Id) || !ids.Add(scenario.Id)) throw new ConsumerScenarioException("SPCONS003", $"Duplicate or empty scenario ID '{scenario.Id}'.");
            scenariosById.Add(scenario.Id, scenario);
            if (scenario.Set != "current") throw new ConsumerScenarioException("SPCONS004", $"Scenario '{scenario.Id}' has unsupported set '{scenario.Set}'.");
            var template = ResolveContained(root, scenario.TemplatePath, "templatePath");
            if (!File.Exists(template) || (File.GetAttributes(template) & FileAttributes.ReparsePoint) != 0) throw new ConsumerScenarioException("SPCONS005", $"Scenario template must be a tracked regular file: {scenario.TemplatePath}.");
            if (scenario.PackageIds.Count == 0 || scenario.PackageIds.Any(id => !graphIds.Contains(id))) throw new ConsumerScenarioException("SPCONS006", $"Scenario '{scenario.Id}' references an unknown package ID.");
            if (scenario.Timeout <= TimeSpan.Zero || scenario.Timeout > MaximumTimeout) throw new ConsumerScenarioException("SPCONS007", $"Scenario '{scenario.Id}' timeout is outside policy.");
            if ((scenario.Mode == ConsumerMode.BinaryCompatibility) != (scenario.BaselineVersion is not null)) throw new ConsumerScenarioException("SPCONS008", $"Scenario '{scenario.Id}' baseline version contract is invalid.");
            if (scenario.ExpectedSmartPipeDependencies.Any(id => !graphIds.Contains(id))) throw new ConsumerScenarioException("SPCONS006", $"Scenario '{scenario.Id}' expects an unknown package ID.");
        }
        if (requiredAtRelease.Any(id => ids.Contains(id)))
            throw new ConsumerScenarioException("SPCONS003", "requiredAtRelease must identify future scenarios not present in the current set.");

        foreach (var scenario in document.Scenarios)
            foreach (var packageId in scenario.PackageIds)
            {
                var package = graph.Packages.Single(package => string.Equals(package.Id, packageId, StringComparison.Ordinal));
                if (!package.ConsumerScenarios.Contains(scenario.Id, StringComparer.Ordinal))
                    throw new ConsumerScenarioException("SPCONS009", $"Scenario '{scenario.Id}' is not registered by package '{packageId}'.");
            }

        var requiredAtReleaseSet = requiredAtRelease.ToHashSet(StringComparer.Ordinal);
        var graphRequiredAtRelease = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in graph.Packages)
        {
            if (package.ConsumerScenarios.Any(string.IsNullOrWhiteSpace)
                || package.ConsumerScenarios.Distinct(StringComparer.Ordinal).Count() != package.ConsumerScenarios.Count)
                throw new ConsumerScenarioException("SPCONS009", $"Package '{package.Id}' has duplicate or empty consumer scenario IDs.");

            foreach (var scenarioId in package.ConsumerScenarios)
            {
                if (package.Lifecycle == PackageLifecycle.Planned)
                {
                    if (!requiredAtReleaseSet.Contains(scenarioId))
                        throw new ConsumerScenarioException("SPCONS009", $"Planned package '{package.Id}' references unknown required-at-release scenario '{scenarioId}'.");
                    graphRequiredAtRelease.Add(scenarioId);
                    continue;
                }

                if (!scenariosById.TryGetValue(scenarioId, out var scenario)
                    || !scenario.PackageIds.Contains(package.Id, StringComparer.Ordinal))
                    throw new ConsumerScenarioException("SPCONS009", $"Package '{package.Id}' has an invalid current consumer scenario '{scenarioId}'.");
            }
        }

        if (!requiredAtReleaseSet.SetEquals(graphRequiredAtRelease))
            throw new ConsumerScenarioException("SPCONS009", "requiredAtRelease must match the planned package consumer scenarios.");
    }

    internal static string ResolveContained(string root, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(x => x is "" or "." or ".."))
            throw new ConsumerScenarioException("SPCONS005", $"{name} must be a normalized repository-relative path.");
        var full = Path.GetFullPath(value, root);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ConsumerScenarioException("SPCONS005", $"{name} escapes the repository.");
        return full;
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes);
            var stack = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) stack.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !stack.Peek().Add(reader.GetString()!))
                    throw new ConsumerScenarioException("SPCONS001", $"Duplicate JSON property '{reader.GetString()}'.");
            }
        }
        catch (JsonException exception) { throw new ConsumerScenarioException("SPCONS001", "Consumer scenario JSON is malformed.", exception); }
    }
}
