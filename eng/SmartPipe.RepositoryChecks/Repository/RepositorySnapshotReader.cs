using System.Text.Json;
using System.Xml;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Repository;

internal sealed record ProjectIdentitySnapshot(
    string ProjectPath,
    string PackageId,
    string Version,
    string TargetFramework,
    string AssemblyName);

internal sealed class RepositorySnapshotReader
{
    private const int MaximumProjects = 256;
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;
    private readonly string _dotnetPath;
    private readonly TimeSpan _processTimeout;

    public RepositorySnapshotReader(
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

    public async Task<IReadOnlyList<ProjectIdentitySnapshot>> ReadPackableProjectsAsync(
        string repositoryRoot,
        string solutionPath,
        CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        var fullSolutionPath = RepositoryPaths.ResolveWithinRoot(root, solutionPath, "solution");
        RepositoryPaths.RequireExistingRegularFile(root, fullSolutionPath, "solution");
        var projectPaths = ReadSolutionProjects(root, fullSolutionPath);
        var result = new List<ProjectIdentitySnapshot>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullProjectPath = RepositoryPaths.ResolveWithinRoot(root, projectPath, "project");
            RepositoryPaths.RequireExistingRegularProject(root, fullProjectPath, projectPath);

            var request = new ProcessRequest(
                _dotnetPath,
                [
                    "msbuild",
                    fullProjectPath,
                    "-nologo",
                    "-getProperty:PackageId",
                    "-getProperty:Version",
                    "-getProperty:TargetFramework",
                    "-getProperty:IsPackable",
                    "-getProperty:AssemblyName",
                ],
                _processTimeout);
            ProcessResult processResult;
            try
            {
                processResult = await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
            {
                throw new OperationCanceledException(
                    $"MSBuild property evaluation was canceled for {projectPath}.",
                    exception,
                    cancellationToken);
            }
            catch (ProcessRunnerException exception)
            {
                throw new InvalidDataException($"MSBuild property evaluation failed for {projectPath}.", exception);
            }

            if (processResult.ExitCode != 0)
            {
                throw new InvalidDataException($"MSBuild property evaluation failed for {projectPath} with exit code {processResult.ExitCode}.");
            }

            var properties = ParseEvaluatedProperties(processResult.StandardOutput, projectPath);
            if (!properties.IsPackable)
            {
                continue;
            }

            RequireValue(properties.PackageId, "PackageId", projectPath);
            RequireValue(properties.Version, "Version", projectPath);
            RequireValue(properties.TargetFramework, "TargetFramework", projectPath);
            RequireValue(properties.AssemblyName, "AssemblyName", projectPath);
            result.Add(new ProjectIdentitySnapshot(
                projectPath,
                properties.PackageId,
                properties.Version,
                properties.TargetFramework,
                properties.AssemblyName));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadSolutionProjects(string root, string solutionPath)
    {
        var projects = new List<string>();
        var logicalPaths = new HashSet<string>(StringComparer.Ordinal);
        var physicalPaths = new HashSet<string>(RepositoryPaths.FileSystemPathComparer);
        var elements = new Stack<SlnxFrame>();
        var totalProjectCount = 0;
        var rootSeen = false;
        var rootClosed = false;
        try
        {
            using var reader = XmlReader.Create(solutionPath, RepositoryXml.CreateSettings());
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (rootClosed || reader.NamespaceURI.Length != 0)
                        {
                            throw new InvalidDataException("The SLNX contains an element outside the strict namespace-empty schema.");
                        }

                        if (!rootSeen)
                        {
                            if (reader.Depth != 0 || reader.LocalName != "Solution")
                            {
                                throw new InvalidDataException("The SLNX root must be namespace-empty Solution.");
                            }

                            ValidateAttributes(reader, "Solution");
                            rootSeen = true;
                        }
                        else
                        {
                            var parent = elements.TryPeek(out var parentFrame) ? parentFrame : null;
                            if (parent?.Name is not ("Solution" or "Folder"))
                            {
                                throw new InvalidDataException("SLNX Project elements cannot contain children.");
                            }

                            if (reader.LocalName == "Folder")
                            {
                                ValidateAttributes(reader, "Folder", "Name");
                                var folderName = RequireAttribute(reader, "Name", "Folder");
                                if (!reader.IsEmptyElement)
                                {
                                    elements.Push(new SlnxFrame(
                                        "Folder",
                                        parent!.InProductionFolder || string.Equals(folderName, "/src/", StringComparison.Ordinal)));
                                }
                            }
                            else if (reader.LocalName == "Project")
                            {
                                ValidateAttributes(reader, "Project", "Path");
                                var path = RequireAttribute(reader, "Path", "Project");
                                if (++totalProjectCount > MaximumProjects)
                                {
                                    throw new InvalidDataException($"The SLNX project count exceeds {MaximumProjects}.");
                                }

                                var fullPath = RepositoryPaths.ResolveWithinRoot(root, path, "project");
                                var normalized = RepositoryPaths.ToRelativePath(root, fullPath);
                                if (!logicalPaths.Add(normalized))
                                {
                                    throw new InvalidDataException($"The SLNX contains a duplicate project path: {normalized}");
                                }

                                if (!physicalPaths.Add(fullPath))
                                {
                                    throw new InvalidDataException($"The SLNX contains project paths that alias the same physical path: {normalized}");
                                }

                                var physicallyUnderSrc = normalized.Length >= "src/".Length
                                    && RepositoryPaths.FileSystemPathComparer.Equals(normalized[.."src/".Length], "src/");
                                if (parent!.InProductionFolder != physicallyUnderSrc)
                                {
                                    throw new InvalidDataException(
                                        $"SLNX project {normalized} is inconsistent with the semantic /src/ folder boundary.");
                                }

                                if (parent.InProductionFolder)
                                {
                                    projects.Add(normalized);
                                }
                            }
                            else
                            {
                                throw new InvalidDataException($"Unsupported SLNX element: {reader.LocalName}");
                            }
                        }

                        if (!reader.IsEmptyElement && reader.LocalName != "Folder")
                        {
                            elements.Push(new SlnxFrame(
                                reader.LocalName,
                                elements.TryPeek(out var containing) && containing.InProductionFolder));
                        }
                        else if (reader.Depth == 0)
                        {
                            rootClosed = true;
                        }

                        break;

                    case XmlNodeType.EndElement:
                        if (reader.NamespaceURI.Length != 0
                            || !elements.TryPop(out var opened)
                            || !string.Equals(opened.Name, reader.LocalName, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException("The SLNX element structure is invalid.");
                        }

                        if (elements.Count == 0)
                        {
                            rootClosed = true;
                        }

                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        if (!string.IsNullOrWhiteSpace(reader.Value))
                        {
                            throw new InvalidDataException("The SLNX schema does not allow text content.");
                        }

                        break;

                    case XmlNodeType.Whitespace:
                    case XmlNodeType.Comment:
                    case XmlNodeType.XmlDeclaration:
                    case XmlNodeType.ProcessingInstruction:
                        break;

                    default:
                        throw new InvalidDataException($"Unsupported SLNX XML node: {reader.NodeType}");
                }
            }
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            throw new InvalidDataException("The SLNX document is malformed or unreadable.", exception);
        }

        if (!rootSeen || !rootClosed || elements.Count != 0)
        {
            throw new InvalidDataException("The SLNX does not contain one complete Solution root.");
        }

        if (totalProjectCount == 0)
        {
            throw new InvalidDataException($"The SLNX project count must be between 1 and {MaximumProjects}.");
        }

        if (projects.Count == 0)
        {
            throw new InvalidDataException("The SLNX does not contain any production projects in semantic folder /src/.");
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    private static void ValidateAttributes(XmlReader reader, string elementName, params string[] allowed)
    {
        if (!reader.HasAttributes)
        {
            return;
        }

        while (reader.MoveToNextAttribute())
        {
            if (reader.NamespaceURI.Length != 0 || !allowed.Contains(reader.LocalName))
            {
                throw new InvalidDataException($"Unsupported attribute {reader.Name} on SLNX {elementName}.");
            }
        }

        reader.MoveToElement();
    }

    private static string RequireAttribute(XmlReader reader, string attributeName, string elementName)
    {
        var value = reader.GetAttribute(attributeName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"SLNX {elementName} requires non-empty {attributeName}.");
        }

        return value;
    }

    private static EvaluatedProperties ParseEvaluatedProperties(string json, string projectPath)
    {
        try
        {
            using var document = JsonDocument.Parse(json, RepositoryJson.DocumentOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"MSBuild returned malformed property JSON for {projectPath}.");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in properties.EnumerateObject())
            {
                if (!values.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()!
                        : throw new InvalidDataException($"MSBuild property {property.Name} is not a string for {projectPath}.")))
                {
                    throw new InvalidDataException($"MSBuild returned duplicate property {property.Name} for {projectPath}.");
                }
            }

            var isPackableText = GetRequired(values, "IsPackable", projectPath);
            if (!bool.TryParse(isPackableText, out var isPackable))
            {
                throw new InvalidDataException($"MSBuild property IsPackable is invalid for {projectPath}.");
            }

            return new EvaluatedProperties(
                GetRequired(values, "PackageId", projectPath),
                GetRequired(values, "Version", projectPath),
                GetRequired(values, "TargetFramework", projectPath),
                isPackable,
                GetRequired(values, "AssemblyName", projectPath));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"MSBuild returned malformed property JSON for {projectPath}.", exception);
        }
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> properties, string name, string projectPath) =>
        properties.TryGetValue(name, out var value)
            ? value
            : throw new InvalidDataException($"MSBuild did not return property {name} for {projectPath}.");

    private static void RequireValue(string value, string propertyName, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Evaluated {propertyName} is empty for packable project {projectPath}.");
        }
    }

    private sealed record EvaluatedProperties(
        string PackageId,
        string Version,
        string TargetFramework,
        bool IsPackable,
        string AssemblyName);

    private sealed record SlnxFrame(string Name, bool InProductionFolder);
}

internal static class RepositoryPaths
{
    public static StringComparer FileSystemPathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string NormalizeRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        root = Path.TrimEndingDirectorySeparator(root);
        RejectLinkOrReparsePoint(root, "repository root");
        return root;
    }

    public static string ResolveWithinRoot(string root, string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (IsPortableAbsolutePath(path))
        {
            throw new InvalidDataException($"The {description} path must be repository-relative, not absolute.");
        }

        var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
        if (!IsWithinRoot(root, fullPath))
        {
            throw new InvalidDataException($"The {description} path resolves outside the repository.");
        }

        RejectExistingLinkedComponents(root, fullPath, description);
        return fullPath;
    }

    public static string NormalizeOutputProjectPath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathRooted(path) && IsPortableAbsolutePath(path))
        {
            throw new InvalidDataException("Package-list project path uses a foreign absolute-path syntax.");
        }

        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
        var relativePath = NormalizeContainedFullPath(root, fullPath, "package-list project");
        RequireExistingRegularProject(root, fullPath, path);
        return relativePath;
    }

    public static string NormalizeContainedFullPath(string root, string fullPath, string description)
    {
        if (!IsWithinRoot(root, fullPath))
        {
            throw new InvalidDataException($"The {description} path resolves outside the repository.");
        }

        RejectExistingLinkedComponents(root, fullPath, description);
        return ToRelativePath(root, fullPath);
    }

    public static void RequireExistingRegularProject(string root, string fullPath, string displayPath)
    {
        RejectExistingLinkedComponents(root, fullPath, "project");
        if (!string.Equals(Path.GetExtension(fullPath), ".csproj", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException($"Project path must name an existing regular .csproj file: {displayPath}");
        }

        RejectLinkOrReparsePoint(fullPath, "project");
    }

    public static void RequireExistingRegularFile(string root, string fullPath, string description)
    {
        RejectExistingLinkedComponents(root, fullPath, description);
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
        {
            throw new FileNotFoundException($"Expected regular file does not exist: {description}", fullPath);
        }

        RejectLinkOrReparsePoint(fullPath, description);
    }

    public static string ToRelativePath(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    private static bool IsWithinRoot(string root, string fullPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(root, fullPath, comparison)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    public static bool IsPortableAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path) || path[0] == '\\')
        {
            return true;
        }

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }

    private static void RejectExistingLinkedComponents(string root, string fullPath, string description)
    {
        RejectLinkOrReparsePoint(root, "repository root");
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var component in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            RejectLinkOrReparsePoint(current, description);
        }
    }

    private static void RejectLinkOrReparsePoint(string path, string description)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The {description} path traverses a symbolic link or reparse point.");
        }
    }
}

internal static class RepositoryXml
{
    public static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 4 * 1024 * 1024,
        IgnoreComments = false,
        IgnoreWhitespace = false,
    };
}

internal static class RepositoryJson
{
    public static JsonDocumentOptions DocumentOptions => new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32,
    };
}
