using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed record VerifyLockFilesResult(IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

internal sealed class VerifyLockFilesCommand
{
    public async Task<VerifyLockFilesResult> ExecuteAsync(string repositoryRoot, CancellationToken ct)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var errors = new List<string>();
        var central = await new CentralPackageVersionReader().VerifyAsync(root, CentralPackageValidationMode.Current, ct).ConfigureAwait(false);
        errors.AddRange(central.Errors.Select(x => $"{x.Code}:{x.Path}:{x.PackageId}"));
        var configuredSources = ReadConfiguredSources(root, errors);
        foreach (var project in EnumerateProjects(root))
        {
            var projectReferences = ReadPackageReferences(root, project, errors);
            var lockPath = Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json");
            if (!File.Exists(lockPath))
            {
                errors.Add($"SPLOCK001:{Relative(root, project)}:tracked lock file is missing");
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(lockPath, ct).ConfigureAwait(false);
            var autoReferencedPackages = ReadAutoReferencedPackages(project);
            if (bytes.AsSpan().StartsWith(new System.Text.UTF8Encoding(true).GetPreamble()) || bytes.Contains((byte)'\r'))
                errors.Add($"SPLOCK002:{Relative(root, lockPath)}:lock file must be UTF-8 without BOM and use LF");
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (!document.RootElement.TryGetProperty("version", out var version) || version.GetInt32() != 2)
                    errors.Add($"SPLOCK003:{Relative(root, lockPath)}:lock file version must be 2");
                if (!document.RootElement.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"SPLOCK005:{Relative(root, lockPath)}:lock file dependencies are missing");
                    continue;
                }

                var lockedDirectReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var framework in dependencies.EnumerateObject())
                    foreach (var package in framework.Value.EnumerateObject())
                    {
                        var typeName = package.Value.TryGetProperty("type", out var type) ? type.GetString() : null;
                        var isProject = string.Equals(typeName, "Project", StringComparison.OrdinalIgnoreCase);
                        if (!isProject && (!package.Value.TryGetProperty("contentHash", out var hash) || string.IsNullOrWhiteSpace(hash.GetString())))
                            errors.Add($"SPLOCK004:{Relative(root, lockPath)}:{package.Name}:contentHash is required");
                        if (!string.Equals(typeName, "Direct", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!autoReferencedPackages.Contains(package.Name))
                        {
                            lockedDirectReferences.Add(package.Name);
                            if (!projectReferences.Contains(package.Name))
                                errors.Add($"SPLOCK007:{Relative(root, lockPath)}:{package.Name}:direct lock dependency is not referenced by the project");
                            if (!central.Versions.TryGetValue(package.Name, out var centralVersion)
                                || !package.Value.TryGetProperty("resolved", out var resolved)
                                || !string.Equals(resolved.GetString(), centralVersion, StringComparison.Ordinal))
                                errors.Add($"SPLOCK006:{Relative(root, lockPath)}:{package.Name}:resolved direct dependency must match its central package version");
                        }
                    }

                foreach (var packageId in projectReferences.Except(lockedDirectReferences, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"SPLOCK007:{Relative(root, lockPath)}:{packageId}:project PackageReference is missing from direct lock dependencies");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                errors.Add($"SPLOCK005:{Relative(root, lockPath)}:lock file is invalid JSON");
            }

            if (configuredSources is not null)
                ValidateAssetsSources(root, project, configuredSources, errors);
        }

        return new(errors.Order(StringComparer.Ordinal).ToArray());
    }

    private static HashSet<string>? ReadConfiguredSources(string root, List<string> errors)
    {
        var path = Path.Combine(root, "NuGet.Config");
        if (!File.Exists(path))
        {
            errors.Add("SPLOCK008:NuGet.Config:root package sources are missing");
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            var sources = document.Root?.Element("packageSources")?.Elements("add")
                .Select(element => element.Attribute("value")?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (sources is null || sources.Count == 0)
            {
                errors.Add("SPLOCK008:NuGet.Config:root package sources are missing");
                return null;
            }

            return sources;
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            errors.Add("SPLOCK008:NuGet.Config:root package sources are invalid");
            return null;
        }
    }

    private static HashSet<string> ReadPackageReferences(string root, string project, List<string> errors)
    {
        try
        {
            using var reader = XmlReader.Create(project, RepositoryXml.CreateSettings());
            var document = XDocument.Load(reader, LoadOptions.None);
            return document.Root?.Elements("ItemGroup").SelectMany(static group => group.Elements("PackageReference"))
                .Select(element => element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? [];
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            errors.Add($"SPLOCK007:{Relative(root, project)}:project PackageReference entries are unreadable");
            return [];
        }
    }

    private static void ValidateAssetsSources(string root, string project, HashSet<string> configuredSources, List<string> errors)
    {
        var assetsPath = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            errors.Add($"SPLOCK008:{Relative(root, project)}:project.assets.json source evidence is missing");
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            if (!document.RootElement.TryGetProperty("project", out var projectElement)
                || !projectElement.TryGetProperty("restore", out var restore)
                || !restore.TryGetProperty("sources", out var sourcesElement)
                || sourcesElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"SPLOCK008:{Relative(root, assetsPath)}:restore source evidence is missing");
                return;
            }

            var restoredSources = sourcesElement.EnumerateObject().Select(static source => source.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!configuredSources.SetEquals(restoredSources))
                errors.Add($"SPLOCK008:{Relative(root, assetsPath)}:restore sources differ from NuGet.Config");
        }
        catch (JsonException)
        {
            errors.Add($"SPLOCK008:{Relative(root, assetsPath)}:restore source evidence is invalid JSON");
        }
    }

    private static HashSet<string> ReadAutoReferencedPackages(string project)
    {
        var assetsPath = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
            if (!document.RootElement.TryGetProperty("project", out var projectElement)
                || !projectElement.TryGetProperty("frameworks", out var frameworks)
                || frameworks.ValueKind != JsonValueKind.Object)
                return [];

            return frameworks.EnumerateObject()
                .Where(static framework => framework.Value.TryGetProperty("dependencies", out _))
                .SelectMany(framework => framework.Value.GetProperty("dependencies").EnumerateObject())
                .Where(static dependency => dependency.Value.TryGetProperty("autoReferenced", out var autoReferenced) && autoReferenced.GetBoolean())
                .Select(static dependency => dependency.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateProjects(string root)
    {
        foreach (var directory in new[] { "src", "tests", "benchmarks", "eng" })
        {
            var path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            foreach (var project in Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
                .Where(file => !file.Contains("tests\\Consumers\\", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains("tests/Consumers/", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains("Fixtures\\", StringComparison.OrdinalIgnoreCase)
                    && !file.Contains("Fixtures/", StringComparison.OrdinalIgnoreCase)))
                yield return project;
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
