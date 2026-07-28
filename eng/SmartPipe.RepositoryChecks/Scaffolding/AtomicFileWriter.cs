using System.Text;

namespace SmartPipe.RepositoryChecks.Scaffolding;

internal sealed class AtomicFileWriter(int? writeFailureAt = null)
{
    public async Task WriteAsync(string repositoryRoot, IReadOnlyList<ScaffoldFile> files, CancellationToken ct)
    {
        var root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targets = files.Select(file => (File: file, Target: ResolveContained(root, file.RelativePath))).ToArray();
        if (targets.Select(x => x.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Length)
            throw new ScaffoldException("SPSCAF003", "Scaffold contains duplicate target paths.");
        var collision = targets.FirstOrDefault(x => File.Exists(x.Target) || Directory.Exists(x.Target));
        if (collision.Target is not null) throw new ScaffoldException("SPSCAF004", $"Refusing to overwrite '{collision.File.RelativePath}'.");

        var stagingRoot = Path.Combine(root, $".smartpipe-scaffold-{Guid.NewGuid():N}");
        var moved = new List<string>();
        try
        {
            for (var index = 0; index < targets.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (writeFailureAt == index + 1) throw new IOException($"Injected failure on scaffold file write {writeFailureAt}.");
                var staged = Path.Combine(stagingRoot, index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                await File.WriteAllTextAsync(staged, targets[index].File.Content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }

            for (var index = 0; index < targets.Length; index++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targets[index].Target)!);
                File.Move(Path.Combine(stagingRoot, index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)), targets[index].Target);
                moved.Add(targets[index].Target);
            }
        }
        catch
        {
            foreach (var path in moved) File.Delete(path);
            throw;
        }
        finally
        {
            if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true);
        }
    }

    private static string ResolveContained(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\\')
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ScaffoldException("SPSCAF003", $"Scaffold path must be normalized and repository-relative: '{relativePath}'.");
        var target = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, target);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ScaffoldException("SPSCAF003", $"Scaffold path escapes the repository: '{relativePath}'.");
        return target;
    }
}
