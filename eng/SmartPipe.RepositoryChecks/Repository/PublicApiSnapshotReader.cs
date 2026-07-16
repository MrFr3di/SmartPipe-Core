using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Repository;

internal sealed record PublicApiFileSnapshot(
    string Path,
    string Sha256,
    int LineCount,
    int ApiEntryCount,
    string? FirstApiEntry,
    string? LastApiEntry);

internal sealed record PublicApiSnapshot(
    IReadOnlyList<PublicApiFileSnapshot> Files,
    IReadOnlyList<string> UnexpectedFiles);

internal sealed class PublicApiSnapshotReader
{
    private const long MaximumPublicApiFileBytes = 16 * 1024 * 1024;
    private const int MaximumDiscoveredFiles = 4096;
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".work", ".opencode", ".kilo", "artifacts", "BenchmarkDotNet.Artifacts",
        "bin", "obj", "packages", "coverage", "logs", "node_modules",
    };

    public PublicApiSnapshot Read(
        string repositoryRoot,
        IReadOnlyList<ProjectIdentitySnapshot> packableProjects)
    {
        ArgumentNullException.ThrowIfNull(packableProjects);
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshots = new List<PublicApiFileSnapshot>(packableProjects.Count * 2);
        foreach (var project in packableProjects.OrderBy(static item => item.ProjectPath, StringComparer.Ordinal))
        {
            var projectPath = RepositoryPaths.ResolveWithinRoot(root, project.ProjectPath, "project");
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var shippedPath = Path.Combine(projectDirectory, "PublicAPI.Shipped.txt");
            if (!File.Exists(shippedPath))
            {
                throw new FileNotFoundException(
                    $"Packable project {project.ProjectPath} is missing PublicAPI.Shipped.txt.",
                    shippedPath);
            }

            AddSnapshot(root, shippedPath, expected, snapshots);
            var unshippedPath = Path.Combine(projectDirectory, "PublicAPI.Unshipped.txt");
            if (File.Exists(unshippedPath))
            {
                AddSnapshot(root, unshippedPath, expected, snapshots);
            }
        }

        snapshots.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var unexpected = EnumeratePublicApiFiles(root)
            .Where(path => !expected.Contains(path))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        return new PublicApiSnapshot(snapshots, unexpected);
    }

    private static void AddSnapshot(
        string root,
        string fullPath,
        ISet<string> expected,
        ICollection<PublicApiFileSnapshot> snapshots)
    {
        var relativePath = RepositoryPaths.ToRelativePath(root, fullPath);
        if (!expected.Add(relativePath))
        {
            throw new InvalidDataException($"Duplicate PublicAPI path: {relativePath}");
        }

        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaximumPublicApiFileBytes)
        {
            throw new InvalidDataException($"PublicAPI file exceeds the size limit: {relativePath}");
        }

        var canonicalBytes = CanonicalText.ToUtf8Bytes(File.ReadAllBytes(fullPath));
        var text = Encoding.UTF8.GetString(canonicalBytes);
        var lines = SplitLogicalLines(text);
        var apiEntries = lines
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        snapshots.Add(new PublicApiFileSnapshot(
            relativePath,
            Hashing.Sha256Hex(canonicalBytes),
            lines.Count,
            apiEntries.Length,
            apiEntries.FirstOrDefault(),
            apiEntries.LastOrDefault()));
    }

    private static IReadOnlyList<string> SplitLogicalLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static IEnumerable<string> EnumeratePublicApiFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var discovered = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "PublicAPI.*.txt", SearchOption.TopDirectoryOnly))
            {
                discovered++;
                if (discovered > MaximumDiscoveredFiles)
                {
                    throw new InvalidDataException($"Repository contains more than {MaximumDiscoveredFiles} PublicAPI files.");
                }

                yield return RepositoryPaths.ToRelativePath(root, file);
            }

            foreach (var child in Directory.EnumerateDirectories(directory).OrderByDescending(static item => item, StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(child);
                if (!ExcludedDirectoryNames.Contains(info.Name) && info.LinkTarget is null)
                {
                    pending.Push(child);
                }
            }
        }
    }
}
