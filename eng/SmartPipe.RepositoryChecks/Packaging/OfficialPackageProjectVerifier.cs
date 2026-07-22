using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Repository;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Packaging;

internal sealed record PackageProjectViolation(string Code, string Message, string? Path = null);

internal sealed record PackageProjectVerificationResult(IReadOnlyList<PackageProjectViolation> Errors)
{
    public bool Success => Errors.Count == 0;
}

internal sealed class OfficialPackageProjectVerifier
{
    private readonly IReadOnlySet<string>? _expectedPackageIds;
    internal OfficialPackageProjectVerifier(IReadOnlySet<string>? expectedPackageIds = null) => _expectedPackageIds = expectedPackageIds;

    private static readonly string[] SharedProperties =
    [
        "Authors", "Copyright", "PackageLicenseExpression", "RepositoryUrl", "RepositoryType", "PackageProjectUrl",
        "PublishRepositoryUrl", "EmbedUntrackedSources", "DebugType", "IncludeSymbols", "SymbolPackageFormat",
        "EnablePackageValidation", "ApiCompatEnableRuleCannotChangeParameterName", "EnableStrictModeForCompatibleTfms",
        "PackageIcon", "PackageReadmeFile",
    ];
    private static readonly HashSet<string> AllowedProjectProperties = new(
        [
            "SmartPipePackage", "PackageId", "Description", "PackageTags", "SmartPipePackageReadmeSource",
            "PackageValidationBaselineVersion", "IsPackable",
        ],
        StringComparer.Ordinal);

    public async Task<PackageProjectVerificationResult> VerifyAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var errors = new List<PackageProjectViolation>();
        IReadOnlySet<string> activeIds;
        IReadOnlySet<string> allIds;
        IReadOnlySet<string> baselineIds;
        if (_expectedPackageIds is not null)
        {
            activeIds = allIds = baselineIds = _expectedPackageIds;
        }
        else
        {
            var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", cancellationToken).ConfigureAwait(false);
            activeIds = graph.Packages.Where(x => x.Lifecycle != PackageLifecycle.Planned).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            allIds = graph.Packages.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            baselineIds = graph.Packages.Where(x => x.BaselineVersion is not null).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectPath in EnumerateProjects(root).OrderBy(path => Relative(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = ReadProject(root, projectPath, errors);
            if (!project.Marker)
            {
                if (project.PackageId is not null && activeIds.Contains(project.PackageId))
                {
                    errors.Add(new("SPPKG001", $"Existing package project {project.PackageId} is missing SmartPipePackage=true.", Relative(root, projectPath)));
                }

                continue;
            }

            if (Relative(root, projectPath).StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
                || project.IsPackableFalse)
            {
                errors.Add(new("SPPKG006", "Test or non-packable project must not be marked SmartPipePackage=true.", Relative(root, projectPath)));
            }

            if (string.IsNullOrWhiteSpace(project.PackageId)
                || string.IsNullOrWhiteSpace(project.Description)
                || string.IsNullOrWhiteSpace(project.Tags)
                || string.IsNullOrWhiteSpace(project.ReadmeSource))
            {
                errors.Add(new("SPPKG002", "Official package requires PackageId, Description, PackageTags and SmartPipePackageReadmeSource.", Relative(root, projectPath)));
            }

            if (!project.HasPackagePropsImport)
            {
                errors.Add(new("SPPKG003", "Official package must import eng/SmartPipe.Package.props.", Relative(root, projectPath)));
            }

            foreach (var property in project.SharedOverrides)
            {
                errors.Add(new("SPPKG007", $"Official package must not redefine shared property {property}.", Relative(root, projectPath)));
            }

            if (project.PackageId is not null)
            {
                found.Add(project.PackageId);
                if (!allIds.Contains(project.PackageId))
                    errors.Add(new("SPPKG008", $"Marked package {project.PackageId} is not registered in package graph.", Relative(root, projectPath)));
                else if (!activeIds.Contains(project.PackageId))
                    errors.Add(new("SPPKG009", $"Planned package {project.PackageId} must not be marked active before graph activation.", Relative(root, projectPath)));
                if (baselineIds.Contains(project.PackageId)
                    && !string.Equals(project.BaselineVersion, "2.1.2", StringComparison.Ordinal))
                {
                    errors.Add(new("SPPKG004", $"Existing package {project.PackageId} requires PackageValidationBaselineVersion=2.1.2.", Relative(root, projectPath)));
                }
            }

            var readme = ResolveReadmePath(root, projectPath, project.ReadmeSource);
            if (readme is null || !File.Exists(readme) || !IsWithin(root, readme))
            {
                errors.Add(new("SPPKG005", "Package README source must resolve to an existing repository file.", Relative(root, projectPath)));
            }
        }

        if (File.Exists(Path.Combine(root, "Directory.Packages.props")) || _expectedPackageIds is not null)
        {
            foreach (var packageId in activeIds)
            {
                if (!found.Contains(packageId))
                {
                    errors.Add(new("SPPKG001", $"Current package {packageId} is not marked SmartPipePackage=true."));
                }
            }
        }

        return new PackageProjectVerificationResult(
            errors.OrderBy(error => error.Code, StringComparer.Ordinal)
                .ThenBy(error => error.Path, StringComparer.Ordinal)
                .ToArray());
    }

    private static ProjectProperties ReadProject(string root, string path, List<PackageProjectViolation> errors)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var sharedOverrides = new List<string>();
        var hasImport = false;
        try
        {
            using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            foreach (var import in document.Root?.Elements().Where(element => element.Name.LocalName == "Import") ?? [])
            {
                hasImport |= (string?)import.Attribute("Project") is { } importPath
                    && importPath.Contains("SmartPipe.Package.props", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var property in document.Root?.Elements().Where(element => element.Name.LocalName == "PropertyGroup")
                .SelectMany(group => group.Elements()) ?? [])
            {
                var name = property.Name.LocalName;
                if (!AllowedProjectProperties.Contains(name) && !SharedProperties.Contains(name))
                {
                    continue;
                }

                var value = property.Value.Trim();
                if (!properties.TryAdd(name, value) && SharedProperties.Contains(name, StringComparer.Ordinal))
                {
                    sharedOverrides.Add(name);
                }

                if (SharedProperties.Contains(name, StringComparer.Ordinal))
                {
                    sharedOverrides.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            errors.Add(new("SPPKG002", $"Project is invalid XML: {exception.Message}", Relative(root, path)));
        }

        return new ProjectProperties(
            string.Equals(properties.GetValueOrDefault("SmartPipePackage"), "true", StringComparison.OrdinalIgnoreCase),
            properties.GetValueOrDefault("PackageId"),
            properties.GetValueOrDefault("Description"),
            properties.GetValueOrDefault("PackageTags"),
            properties.GetValueOrDefault("SmartPipePackageReadmeSource"),
            properties.GetValueOrDefault("PackageValidationBaselineVersion"),
            string.Equals(properties.GetValueOrDefault("IsPackable"), "false", StringComparison.OrdinalIgnoreCase),
            hasImport,
            sharedOverrides.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IEnumerable<string> EnumerateProjects(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var project in Directory.EnumerateFiles(directory, "*.csproj"))
            {
                yield return project;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name is not (".git" or "artifacts" or "bin" or "obj" or ".work" or ".opencode" or ".kilo" or "BenchmarkDotNet.Artifacts"))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static string? ResolveReadmePath(string root, string projectPath, string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var expanded = source.Replace("$(SmartPipeRepositoryRoot)", root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase);
        if (expanded.Contains("$(", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetFullPath(expanded, projectDirectory);
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private sealed record ProjectProperties(
        bool Marker,
        string? PackageId,
        string? Description,
        string? Tags,
        string? ReadmeSource,
        string? BaselineVersion,
        bool IsPackableFalse,
        bool HasPackagePropsImport,
        IReadOnlyList<string> SharedOverrides);
}
