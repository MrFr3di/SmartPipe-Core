using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
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
        var projectPaths = ReadSolutionProjects(root, fullSolutionPath);
        var result = new List<ProjectIdentitySnapshot>(projectPaths.Count);
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullProjectPath = RepositoryPaths.ResolveWithinRoot(root, projectPath, "project");
            if (!File.Exists(fullProjectPath))
            {
                throw new FileNotFoundException($"Solution project does not exist: {projectPath}", fullProjectPath);
            }

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
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(solutionPath, RepositoryXml.CreateSettings());
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or IOException)
        {
            throw new InvalidDataException("The SLNX document is malformed or unreadable.", exception);
        }

        if (document.Root?.Name != "Solution")
        {
            throw new InvalidDataException("The SLNX root element must be Solution without an XML namespace.");
        }

        var rawProjects = document.Descendants("Project").ToArray();
        if (rawProjects.Length == 0 || rawProjects.Length > MaximumProjects)
        {
            throw new InvalidDataException($"The SLNX project count must be between 1 and {MaximumProjects}.");
        }

        var projects = new List<string>(rawProjects.Length);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in rawProjects)
        {
            var path = project.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException("Every SLNX Project must have a non-empty Path attribute.");
            }

            var normalized = RepositoryPaths.ToRelativePath(root, RepositoryPaths.ResolveWithinRoot(root, path, "project"));
            if (!unique.Add(normalized))
            {
                throw new InvalidDataException($"The SLNX contains a duplicate project path: {normalized}");
            }

            projects.Add(normalized);
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
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
}

internal static class RepositoryPaths
{
    public static string NormalizeRoot(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        return Path.TrimEndingDirectorySeparator(root);
    }

    public static string ResolveWithinRoot(string root, string path, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"The {description} path must be repository-relative, not absolute.");
        }

        var fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
        if (!IsWithinRoot(root, fullPath))
        {
            throw new InvalidDataException($"The {description} path resolves outside the repository.");
        }

        return fullPath;
    }

    public static string NormalizeOutputProjectPath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);
        if (!IsWithinRoot(root, fullPath))
        {
            throw new InvalidDataException("Package-list project path resolves outside the repository.");
        }

        return ToRelativePath(root, fullPath);
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
