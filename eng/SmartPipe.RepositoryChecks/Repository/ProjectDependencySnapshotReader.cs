using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Repository;

internal sealed record DirectReferenceSnapshot(
    string Include,
    string? Version,
    string? Condition,
    string? PrivateAssets,
    string? IncludeAssets,
    string? ExcludeAssets);

internal sealed record ProjectDirectDependencySnapshot(
    string ProjectPath,
    IReadOnlyList<DirectReferenceSnapshot> ProjectReferences,
    IReadOnlyList<DirectReferenceSnapshot> PackageReferences);

internal sealed record RestoredPackageSnapshot(
    string Id,
    string? RequestedVersion,
    string ResolvedVersion,
    string? AutoReferenced);

internal sealed record RestoredFrameworkSnapshot(
    string Framework,
    IReadOnlyList<RestoredPackageSnapshot> TopLevelPackages,
    IReadOnlyList<RestoredPackageSnapshot> TransitivePackages);

internal sealed record RestoredProjectSnapshot(
    string ProjectPath,
    IReadOnlyList<RestoredFrameworkSnapshot> Frameworks);

internal sealed record RestoredDependencySnapshot(
    IReadOnlyList<RestoredProjectSnapshot> Projects,
    string CanonicalJson,
    string Sha256);

internal static class DirectReferenceTotalComparer
{
    public static IComparer<DirectReferenceSnapshot> ProjectReference { get; } =
        new Comparer(StringComparer.Ordinal);

    public static IComparer<DirectReferenceSnapshot> PackageReference { get; } =
        new Comparer(StringComparer.OrdinalIgnoreCase);

    private sealed class Comparer(StringComparer identityComparer) : IComparer<DirectReferenceSnapshot>
    {
        public int Compare(DirectReferenceSnapshot? left, DirectReferenceSnapshot? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            return CompareField(left.Include, right.Include, identityComparer)
                ?? CompareField(left.Include, right.Include, StringComparer.Ordinal)
                ?? CompareField(left.Condition, right.Condition, StringComparer.Ordinal)
                ?? CompareField(left.Version, right.Version, StringComparer.Ordinal)
                ?? CompareField(left.PrivateAssets, right.PrivateAssets, StringComparer.Ordinal)
                ?? CompareField(left.IncludeAssets, right.IncludeAssets, StringComparer.Ordinal)
                ?? CompareField(left.ExcludeAssets, right.ExcludeAssets, StringComparer.Ordinal)
                ?? 0;
        }

        private static int? CompareField(string? left, string? right, StringComparer comparer)
        {
            var result = comparer.Compare(left, right);
            return result == 0 ? null : result;
        }
    }
}

internal static class DirectReferenceSemanticComparer
{
    public static IEqualityComparer<DirectReferenceSnapshot> ProjectReference { get; } =
        new Comparer(RepositoryPaths.FileSystemPathComparer);

    public static IEqualityComparer<DirectReferenceSnapshot> PackageReference { get; } =
        new Comparer(StringComparer.OrdinalIgnoreCase);

    private sealed class Comparer(StringComparer identityComparer) : IEqualityComparer<DirectReferenceSnapshot>
    {
        public bool Equals(DirectReferenceSnapshot? left, DirectReferenceSnapshot? right) =>
            ReferenceEquals(left, right)
            || (left is not null
                && right is not null
                && identityComparer.Equals(left.Include, right.Include)
                && StringComparer.Ordinal.Equals(left.Version, right.Version)
                && StringComparer.Ordinal.Equals(left.Condition, right.Condition)
                && StringComparer.Ordinal.Equals(left.PrivateAssets, right.PrivateAssets)
                && StringComparer.Ordinal.Equals(left.IncludeAssets, right.IncludeAssets)
                && StringComparer.Ordinal.Equals(left.ExcludeAssets, right.ExcludeAssets));

        public int GetHashCode(DirectReferenceSnapshot value)
        {
            var hash = new HashCode();
            hash.Add(value.Include, identityComparer);
            hash.Add(value.Version, StringComparer.Ordinal);
            hash.Add(value.Condition, StringComparer.Ordinal);
            hash.Add(value.PrivateAssets, StringComparer.Ordinal);
            hash.Add(value.IncludeAssets, StringComparer.Ordinal);
            hash.Add(value.ExcludeAssets, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}

internal sealed class ProjectDependencySnapshotReader
{
    private const int MaximumProjects = 256;
    private const int MaximumFrameworksPerProject = 64;
    private const int MaximumPackagesPerFrameworkList = 4096;
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;
    private readonly string _dotnetPath;
    private readonly TimeSpan _processTimeout;

    public ProjectDependencySnapshotReader(
        IProcessRunner processRunner,
        string dotnetPath,
        TimeSpan? processTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetPath);
        if (processTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        }

        _processRunner = processRunner;
        _dotnetPath = dotnetPath;
        _processTimeout = processTimeout ?? DefaultProcessTimeout;
    }

    public IReadOnlyList<ProjectDirectDependencySnapshot> ReadDirect(
        string repositoryRoot,
        IReadOnlyList<ProjectIdentitySnapshot> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        var result = new List<ProjectDirectDependencySnapshot>(projects.Count);
        foreach (var project in projects.OrderBy(static item => item.ProjectPath, StringComparer.Ordinal))
        {
            var fullProjectPath = RepositoryPaths.ResolveWithinRoot(root, project.ProjectPath, "project");
            RepositoryPaths.RequireExistingRegularProject(root, fullProjectPath, project.ProjectPath);
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(fullProjectPath, RepositoryXml.CreateSettings());
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (Exception exception) when (exception is XmlException or IOException)
            {
                throw new InvalidDataException($"Project XML is malformed or unreadable: {project.ProjectPath}", exception);
            }

            if (document.Root?.Name != "Project")
            {
                throw new InvalidDataException($"Project XML root must be namespace-empty Project: {project.ProjectPath}");
            }

            ValidateReferencePlacement(document.Root);
            var directReferences = document.Root.Elements("ItemGroup").SelectMany(static group => group.Elements());
            var projectReferences = directReferences
                .Where(static element => element.Name == "ProjectReference")
                .Select(element => ParseReference(element, isProjectReference: true, root, fullProjectPath))
                .Order(DirectReferenceTotalComparer.ProjectReference)
                .ToArray();
            var packageReferences = directReferences
                .Where(static element => element.Name == "PackageReference")
                .Select(element => ParseReference(element, isProjectReference: false, root, fullProjectPath))
                .Order(DirectReferenceTotalComparer.PackageReference)
                .ToArray();
            RejectSemanticDuplicates(projectReferences, DirectReferenceSemanticComparer.ProjectReference, project.ProjectPath);
            RejectSemanticDuplicates(packageReferences, DirectReferenceSemanticComparer.PackageReference, project.ProjectPath);
            result.Add(new ProjectDirectDependencySnapshot(project.ProjectPath, projectReferences, packageReferences));
        }

        return result;
    }

    public async Task<RestoredDependencySnapshot> ReadRestoredAsync(
        string repositoryRoot,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        var fullSolutionPath = RepositoryPaths.ResolveWithinRoot(root, solutionPath, "solution");
        RepositoryPaths.RequireExistingRegularFile(root, fullSolutionPath, "solution");
        var request = new ProcessRequest(
            _dotnetPath,
            [
                "package", "list", "--project", fullSolutionPath,
                "--include-transitive", "--format", "json", "--output-version", "1", "--no-restore",
            ],
            _processTimeout);
        ProcessResult processResult;
        try
        {
            processResult = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("dotnet package list was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new InvalidDataException("dotnet package list process failed.", exception);
        }

        if (processResult.ExitCode != 0)
        {
            throw new InvalidDataException($"dotnet package list failed with exit code {processResult.ExitCode}.");
        }

        var projects = ParseRestoredGraph(root, processResult.StandardOutput);
        var canonicalJson = SerializeCanonical(projects);
        return new RestoredDependencySnapshot(
            projects,
            canonicalJson,
            Hashing.Sha256Hex(CanonicalText.ToUtf8Bytes(canonicalJson)));
    }

    private static DirectReferenceSnapshot ParseReference(
        XElement element,
        bool isProjectReference,
        string root,
        string projectPath)
    {
        ValidateReferenceShape(element);
        var include = element.Attribute("Include")?.Value;
        if (string.IsNullOrWhiteSpace(include))
        {
            throw new InvalidDataException($"{element.Name.LocalName} in {RepositoryPaths.ToRelativePath(root, projectPath)} has no Include.");
        }

        if (isProjectReference)
        {
            if (RepositoryPaths.IsPortableAbsolutePath(include))
            {
                throw new InvalidDataException("ProjectReference Include must be repository-relative.");
            }

            var referencedPath = Path.GetFullPath(include.Replace('/', Path.DirectorySeparatorChar), Path.GetDirectoryName(projectPath)!);
            _ = RepositoryPaths.NormalizeContainedFullPath(root, referencedPath, "ProjectReference");
            include = include.Replace('\\', '/');
        }

        var itemGroupCondition = element.Parent?.Name.LocalName == "ItemGroup"
            ? NullIfWhiteSpace(element.Parent.Attribute("Condition")?.Value)
            : null;
        var itemCondition = NullIfWhiteSpace(element.Attribute("Condition")?.Value);
        var condition = itemGroupCondition switch
        {
            null => itemCondition,
            _ when itemCondition is null => itemGroupCondition,
            _ => $"{itemGroupCondition} && {itemCondition}",
        };
        return new DirectReferenceSnapshot(
            include,
            GetMetadata(element, "Version"),
            condition,
            GetMetadata(element, "PrivateAssets"),
            GetMetadata(element, "IncludeAssets"),
            GetMetadata(element, "ExcludeAssets"));
    }

    private static string? GetMetadata(XElement element, string name)
    {
        var attributeValue = NullIfWhiteSpace(element.Attribute(name)?.Value);
        var childElements = element.Elements(name).ToArray();
        if (childElements.Length > 1 || (attributeValue is not null && childElements.Length != 0))
        {
            throw new InvalidDataException($"Reference metadata {name} is duplicated.");
        }

        return attributeValue ?? NullIfWhiteSpace(childElements.SingleOrDefault()?.Value);
    }

    private static void ValidateReferencePlacement(XElement project)
    {
        foreach (var element in project.Descendants().Where(static element =>
                     element.Name.LocalName is "ProjectReference" or "PackageReference"))
        {
            if (element.Name.Namespace != XNamespace.None
                || element.Parent?.Name != "ItemGroup"
                || element.Parent.Parent != project)
            {
                throw new InvalidDataException($"{element.Name.LocalName} must be a namespace-empty direct ItemGroup child.");
            }
        }
    }

    private static void ValidateReferenceShape(XElement element)
    {
        var allowedAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Include", "Version", "Condition", "PrivateAssets", "IncludeAssets", "ExcludeAssets",
        };
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.Namespace != XNamespace.None || !allowedAttributes.Contains(attribute.Name.LocalName))
            {
                throw new InvalidDataException($"Unsupported {element.Name.LocalName} attribute: {attribute.Name}");
            }
        }

        var allowedMetadata = new HashSet<string>(StringComparer.Ordinal)
        {
            "Version", "PrivateAssets", "IncludeAssets", "ExcludeAssets",
        };
        foreach (var child in element.Elements())
        {
            if (child.Name.Namespace != XNamespace.None
                || !allowedMetadata.Contains(child.Name.LocalName)
                || child.HasAttributes
                || child.HasElements)
            {
                throw new InvalidDataException($"Unsupported {element.Name.LocalName} metadata element: {child.Name}");
            }
        }
    }

    private static void RejectSemanticDuplicates(
        IReadOnlyList<DirectReferenceSnapshot> references,
        IEqualityComparer<DirectReferenceSnapshot> comparer,
        string projectPath)
    {
        var unique = new HashSet<DirectReferenceSnapshot>(comparer);
        foreach (var reference in references)
        {
            if (!unique.Add(reference))
            {
                throw new InvalidDataException($"Project {projectPath} contains a duplicate {reference.Include} reference.");
            }
        }
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<RestoredProjectSnapshot> ParseRestoredGraph(string root, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, RepositoryJson.DocumentOptions);
            var rootElement = document.RootElement;
            RequireObject(rootElement, "package-list root");
            ValidateProperties(rootElement, "package-list root", "version", "parameters", "projects");
            if (GetRequired(rootElement, "version", "package-list root").ValueKind != JsonValueKind.Number
                || GetRequired(rootElement, "version", "package-list root").GetInt32() != 1)
            {
                throw new InvalidDataException("dotnet package list output version must be 1.");
            }

            if (!string.Equals(
                    GetRequiredString(rootElement, "parameters", "package-list root"),
                    "--include-transitive",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("dotnet package list parameters must be exactly --include-transitive.");
            }

            var projectsElement = GetRequired(rootElement, "projects", "package-list root");
            RequireArray(projectsElement, "projects");
            var projects = new List<RestoredProjectSnapshot>();
            var logicalProjects = new HashSet<string>(StringComparer.Ordinal);
            var physicalProjects = new HashSet<string>(RepositoryPaths.FileSystemPathComparer);
            var projectCount = 0;
            foreach (var projectElement in projectsElement.EnumerateArray())
            {
                if (++projectCount > MaximumProjects)
                {
                    throw new InvalidDataException($"Package-list project count exceeds {MaximumProjects}.");
                }

                RequireObject(projectElement, "project");
                ValidateProperties(projectElement, "project", "path", "frameworks");
                var outputPath = RehydrateRedactedProjectPath(root, GetRequiredString(projectElement, "path", "project"));
                var projectPath = RepositoryPaths.NormalizeOutputProjectPath(root, outputPath);
                if (!logicalProjects.Add(projectPath))
                {
                    throw new InvalidDataException($"Package-list JSON contains duplicate project {projectPath}.");
                }

                if (!physicalProjects.Add(projectPath))
                {
                    throw new InvalidDataException($"Package-list JSON contains project paths that alias the same physical project: {projectPath}.");
                }

                var frameworksElement = GetRequired(projectElement, "frameworks", projectPath);
                RequireArray(frameworksElement, $"frameworks for {projectPath}");
                var frameworks = new List<RestoredFrameworkSnapshot>();
                var uniqueFrameworks = new HashSet<string>(StringComparer.Ordinal);
                var frameworkCount = 0;
                foreach (var frameworkElement in frameworksElement.EnumerateArray())
                {
                    if (++frameworkCount > MaximumFrameworksPerProject)
                    {
                        throw new InvalidDataException($"Framework count exceeds {MaximumFrameworksPerProject} for {projectPath}.");
                    }

                    RequireObject(frameworkElement, "framework");
                    ValidateProperties(frameworkElement, "framework", "framework", "topLevelPackages", "transitivePackages");
                    var framework = GetRequiredString(frameworkElement, "framework", projectPath);
                    if (!uniqueFrameworks.Add(framework))
                    {
                        throw new InvalidDataException($"Package-list JSON contains duplicate framework {framework} for {projectPath}.");
                    }

                    frameworks.Add(new RestoredFrameworkSnapshot(
                        framework,
                        ParsePackages(frameworkElement, "topLevelPackages", topLevel: true, projectPath, framework),
                        ParsePackages(frameworkElement, "transitivePackages", topLevel: false, projectPath, framework)));
                }

                if (frameworkCount == 0)
                {
                    throw new InvalidDataException($"Package-list project {projectPath} must contain at least one framework.");
                }

                frameworks.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Framework, right.Framework));
                projects.Add(new RestoredProjectSnapshot(projectPath, frameworks));
            }

            if (projectCount == 0)
            {
                throw new InvalidDataException("Package-list JSON must contain at least one project.");
            }

            projects.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ProjectPath, right.ProjectPath));
            return projects;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("dotnet package list returned malformed JSON.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("dotnet package list returned values of unexpected JSON types.", exception);
        }
    }

    private static string RehydrateRedactedProjectPath(string root, string path)
    {
        const string marker = "<home>";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return path;
        }

        home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        var relativeRoot = Path.GetRelativePath(home, root).Replace('\\', '/');
        if (relativeRoot == ".." || relativeRoot.StartsWith("../", StringComparison.Ordinal))
        {
            return path;
        }

        var redactedRoot = relativeRoot == "." ? marker : $"{marker}/{relativeRoot}";
        var normalizedPath = path.Replace('\\', '/');
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!normalizedPath.StartsWith(redactedRoot + '/', comparison))
        {
            return path;
        }

        return root + normalizedPath[redactedRoot.Length..].Replace('/', Path.DirectorySeparatorChar);
    }

    private static IReadOnlyList<RestoredPackageSnapshot> ParsePackages(
        JsonElement frameworkElement,
        string propertyName,
        bool topLevel,
        string projectPath,
        string framework)
    {
        if (!frameworkElement.TryGetProperty(propertyName, out var packagesElement))
        {
            return [];
        }

        RequireArray(packagesElement, propertyName);
        var packages = new List<RestoredPackageSnapshot>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packageCount = 0;
        foreach (var packageElement in packagesElement.EnumerateArray())
        {
            if (++packageCount > MaximumPackagesPerFrameworkList)
            {
                throw new InvalidDataException($"{propertyName} exceeds {MaximumPackagesPerFrameworkList} entries for {projectPath}/{framework}.");
            }

            RequireObject(packageElement, propertyName);
            ValidateProperties(packageElement, propertyName, "id", "requestedVersion", "resolvedVersion", "autoReferenced");
            var id = GetRequiredString(packageElement, "id", propertyName);
            if (!unique.Add(id))
            {
                throw new InvalidDataException($"Package-list JSON contains duplicate package ID {id} in {projectPath}/{framework}/{propertyName}.");
            }

            var requestedVersion = GetOptionalString(packageElement, "requestedVersion", propertyName);
            if (topLevel && string.IsNullOrWhiteSpace(requestedVersion))
            {
                throw new InvalidDataException($"Top-level package {id} has no requestedVersion.");
            }

            packages.Add(new RestoredPackageSnapshot(
                id,
                requestedVersion,
                GetRequiredString(packageElement, "resolvedVersion", propertyName),
                GetOptionalString(packageElement, "autoReferenced", propertyName)));
        }

        packages.Sort(static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id));
        return packages;
    }

    private static void ValidateProperties(JsonElement element, string description, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"Package-list JSON contains duplicate property {property.Name} in {description}.");
            }

            if (!allowedSet.Contains(property.Name))
            {
                throw new InvalidDataException($"Package-list JSON contains unsupported property {property.Name} in {description}.");
            }
        }
    }

    private static JsonElement GetRequired(JsonElement element, string propertyName, string description) =>
        element.TryGetProperty(propertyName, out var value)
            ? value
            : throw new InvalidDataException($"Package-list JSON is missing {propertyName} in {description}.");

    private static string GetRequiredString(JsonElement element, string propertyName, string description)
    {
        var value = GetRequired(element, propertyName, description);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Package-list JSON property {propertyName} must be a non-empty string in {description}.");
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName, string description)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"Package-list JSON property {propertyName} must be a non-empty string in {description}.");
        }

        return value.GetString();
    }

    private static void RequireObject(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Package-list JSON {description} must be an object.");
        }
    }

    private static void RequireArray(JsonElement element, string description)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Package-list JSON {description} must be an array.");
        }
    }

    private static string SerializeCanonical(IReadOnlyList<RestoredProjectSnapshot> projects)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WritePropertyName("projects");
            writer.WriteStartArray();
            foreach (var project in projects)
            {
                writer.WriteStartObject();
                writer.WriteString("path", project.ProjectPath);
                writer.WritePropertyName("frameworks");
                writer.WriteStartArray();
                foreach (var framework in project.Frameworks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("framework", framework.Framework);
                    WritePackages(writer, "topLevelPackages", framework.TopLevelPackages);
                    WritePackages(writer, "transitivePackages", framework.TransitivePackages);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WritePackages(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<RestoredPackageSnapshot> packages)
    {
        if (packages.Count == 0)
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var package in packages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", package.Id);
            if (package.RequestedVersion is not null)
            {
                writer.WriteString("requestedVersion", package.RequestedVersion);
            }

            writer.WriteString("resolvedVersion", package.ResolvedVersion);
            if (package.AutoReferenced is not null)
            {
                writer.WriteString("autoReferenced", package.AutoReferenced);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
