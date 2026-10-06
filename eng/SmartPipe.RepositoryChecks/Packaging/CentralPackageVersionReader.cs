using System.Xml;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Packaging;

internal enum CentralPackageValidationMode
{
    Current,
    Release,
}

internal sealed record CentralPackageViolation(string Code, string Message, string? Path = null, string? PackageId = null);

internal sealed record CentralPackageValidationResult(
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyList<CentralPackageViolation> Errors,
    IReadOnlyList<CentralPackageViolation> Warnings)
{
    public bool Success => Errors.Count == 0;
}

internal sealed class CentralPackageVersionReader
{
    private const int MaximumPackageEntries = 2048;
    private static readonly string[] IgnoredDirectoryNames =
    [
        ".agents", ".codebase-memory", ".codex", ".git", ".kilo", ".opencode", ".recovery", ".sonarqube", ".vscode",
        ".work", ".worktrees", "artifacts", "BenchmarkDotNet.Artifacts", "bin", "coverage", "Fixtures", "logs", "node_modules", "obj", "packages",
    ];

    public Task<CentralPackageValidationResult> VerifyAsync(
        string repositoryRoot,
        CentralPackageValidationMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var root = Path.GetFullPath(repositoryRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var errors = new List<CentralPackageViolation>();
        var warnings = new List<CentralPackageViolation>();
        var propsFiles = EnumerateFiles(root, "Directory.Packages.props")
            .OrderBy(path => RelativePath(root, path), StringComparer.Ordinal)
            .ToArray();
        var rootProps = Path.Combine(root, "Directory.Packages.props");
        if (!File.Exists(rootProps))
        {
            errors.Add(new("SPCPM001", "Root Directory.Packages.props is missing.", "Directory.Packages.props"));
        }

        foreach (var nested in propsFiles.Where(path => !PathEquals(path, rootProps)))
        {
            errors.Add(new("SPCPM002", "Nested Directory.Packages.props is not allowed.", RelativePath(root, nested)));
        }

        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(rootProps))
        {
            ParseCentralProps(root, rootProps, versions, errors);
        }

        var references = new List<PackageReferenceInfo>();
        foreach (var path in EnumerateFiles(root, "*.csproj", "*.props", "*.targets")
            .OrderBy(path => RelativePath(root, path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathEquals(path, rootProps))
            {
                continue;
            }

            ParseProjectReferences(root, path, references, errors);
        }

        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            referencedIds.Add(reference.Id);
            if (reference.HasLocalVersion)
            {
                errors.Add(new("SPCPM004", "PackageReference must not define Version or VersionOverride locally.", reference.Path, reference.Id));
            }

            if (!versions.ContainsKey(reference.Id))
            {
                errors.Add(new("SPCPM005", "PackageReference has no central PackageVersion entry.", reference.Path, reference.Id));
            }
        }

        foreach (var pair in versions)
        {
            if (referencedIds.Contains(pair.Key))
            {
                continue;
            }

            var violation = new CentralPackageViolation("SPCPM006", "Central PackageVersion is not referenced by a project.", "Directory.Packages.props", pair.Key);
            if (mode == CentralPackageValidationMode.Release)
            {
                errors.Add(violation);
            }
            else
            {
                warnings.Add(violation);
            }
        }

        if (File.Exists(rootProps))
        {
            ValidatePolicyProperties(rootProps, errors);
        }

        return Task.FromResult<CentralPackageValidationResult>(new CentralPackageValidationResult(
            versions,
            errors.OrderBy(violation => violation.Code, StringComparer.Ordinal)
                .ThenBy(violation => violation.Path, StringComparer.Ordinal)
                .ThenBy(violation => violation.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.OrderBy(violation => violation.Code, StringComparer.Ordinal)
                .ThenBy(violation => violation.Path, StringComparer.Ordinal)
                .ThenBy(violation => violation.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray()));
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var result = await VerifyAsync(repositoryRoot, CentralPackageValidationMode.Current, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, result.Errors.Select(static violation => $"[{violation.Code}] {violation.Message}")));
        }

        return result.Versions;
    }

    private static void ParseCentralProps(
        string root,
        string path,
        Dictionary<string, string> versions,
        List<CentralPackageViolation> errors)
    {
        try
        {
            using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.LocalName, "PackageVersion", StringComparison.Ordinal))
                {
                    continue;
                }

                var id = reader.GetAttribute("Include");
                var version = reader.GetAttribute("Version");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                {
                    errors.Add(new("SPCPM009", "PackageVersion requires non-empty Include and exact Version.", RelativePath(root, path), id));
                    continue;
                }

                if (!versions.TryAdd(id, version))
                {
                    errors.Add(new("SPCPM003", "Duplicate central PackageVersion ID.", RelativePath(root, path), id));
                    continue;
                }

                if (!IsExactVersion(version))
                {
                    errors.Add(new("SPCPM009", "Central PackageVersion must be an exact non-floating version.", RelativePath(root, path), id));
                }
            }
        }
        catch (XmlException exception)
        {
            errors.Add(new("SPCPM001", $"Directory.Packages.props is invalid XML: {exception.Message}", RelativePath(root, path)));
        }
    }

    private static void ParseProjectReferences(
        string root,
        string path,
        List<PackageReferenceInfo> references,
        List<CentralPackageViolation> errors)
    {
        try
        {
            using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || !string.Equals(reader.LocalName, "PackageReference", StringComparison.Ordinal))
                {
                    continue;
                }

                var id = reader.GetAttribute("Include") ?? reader.GetAttribute("Update");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var hasLocalVersion = reader.GetAttribute("Version") is not null
                    || reader.GetAttribute("VersionOverride") is not null;
                if (!reader.IsEmptyElement)
                {
                    using var subtree = reader.ReadSubtree();
                    while (subtree.Read())
                    {
                        if (subtree.NodeType == XmlNodeType.Element
                            && string.Equals(subtree.LocalName, "Version", StringComparison.Ordinal)
                            || subtree.NodeType == XmlNodeType.Element
                            && string.Equals(subtree.LocalName, "VersionOverride", StringComparison.Ordinal))
                        {
                            hasLocalVersion = true;
                        }
                    }
                }

                references.Add(new(id, RelativePath(root, path), hasLocalVersion));
                if (references.Count > MaximumPackageEntries)
                {
                    errors.Add(new("SPCPM005", $"PackageReference count exceeds {MaximumPackageEntries}.", RelativePath(root, path)));
                    return;
                }
            }
        }
        catch (XmlException exception)
        {
            errors.Add(new("SPCPM005", $"Project file is invalid XML: {exception.Message}", RelativePath(root, path)));
        }
    }

    private static void ValidatePolicyProperties(string path, List<CentralPackageViolation> errors)
    {
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var reader = XmlReader.Create(path, RepositoryXml.CreateSettings());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.IsEmptyElement || reader.LocalName is not (
                "ManagePackageVersionsCentrally" or "CentralPackageTransitivePinningEnabled" or "CentralPackageVersionOverrideEnabled"))
            {
                continue;
            }

            properties[reader.LocalName] = reader.ReadElementContentAsString().Trim();
        }

        if (!string.Equals(properties.GetValueOrDefault("ManagePackageVersionsCentrally"), "true", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(properties.GetValueOrDefault("CentralPackageTransitivePinningEnabled"), "false", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("SPCPM007", "Central package management must be enabled with transitive pinning disabled.", "Directory.Packages.props"));
        }

        if (!string.Equals(properties.GetValueOrDefault("CentralPackageVersionOverrideEnabled"), "false", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new("SPCPM008", "Central package version overrides must remain disabled.", "Directory.Packages.props"));
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, params string[] patterns)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(directory);
                directories = Directory.EnumerateDirectories(directory);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (patterns.Any(pattern => string.Equals(Path.GetFileName(file), pattern, StringComparison.OrdinalIgnoreCase)
                        || pattern.StartsWith("*.", StringComparison.Ordinal) && file.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)))
                {
                    yield return file;
                }
            }

            foreach (var child in directories)
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsExactVersion(string version)
    {
        var coreAndBuild = version.Split('+', 2, StringSplitOptions.None);
        if (coreAndBuild.Length == 2 && !IsIdentifierList(coreAndBuild[1]))
        {
            return false;
        }

        var coreAndPreRelease = coreAndBuild[0].Split('-', 2, StringSplitOptions.None);
        var core = coreAndPreRelease[0];
        var coreParts = core.Split('.', StringSplitOptions.None);
        if (coreParts.Length != 3 || coreParts.Any(static part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        if (coreAndPreRelease.Length == 2 && !IsIdentifierList(coreAndPreRelease[1]))
        {
            return false;
        }

        return true;
    }

    private static bool IsIdentifierList(string value) =>
        value.Split('.', StringSplitOptions.None).All(static part =>
            part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record PackageReferenceInfo(string Id, string Path, bool HasLocalVersion);
}
