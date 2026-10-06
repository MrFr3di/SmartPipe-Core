using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class BaselinePackageProvisioner(INuGetPackageFetcher fetcher)
{
    public async Task<int> ProvisionAsync(ProvisionBaselineOptions options, CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(options.RepositoryRoot);
        var manifestPath = ResolvePath(root, options.ManifestPath);
        _ = RepositoryPaths.NormalizeContainedFullPath(root, manifestPath, "manifest");
        RepositoryPaths.RequireExistingRegularFile(root, manifestPath, "manifest");
        var packagesDirectory = ResolvePath(root, options.PackagesDirectory);
        _ = RepositoryPaths.NormalizeContainedFullPath(root, packagesDirectory, "packages directory");

        var manifest = BaselineManifestSerializer.Deserialize(
            await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        Directory.CreateDirectory(packagesDirectory);

        foreach (var package in manifest.Packages)
        {
            var canonicalPath = Path.Combine(packagesDirectory, package.FileName);
            var existingPaths = Directory.EnumerateFiles(packagesDirectory)
                .Where(path => string.Equals(Path.GetFileName(path), package.FileName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (existingPaths.Length > 1)
            {
                throw new InvalidDataException($"Multiple package files differ only by case: {package.FileName}.");
            }

            if (existingPaths.Length == 1)
            {
                NormalizeFileName(existingPaths[0], canonicalPath);
                continue;
            }

            var fetchedPath = await fetcher.FetchAsync(
                package.Id, package.Version, packagesDirectory, cancellationToken).ConfigureAwait(false);
            NormalizeFileName(fetchedPath, canonicalPath);
        }

        return manifest.Packages.Count;
    }

    private static string ResolvePath(string root, string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), root);

    private static void NormalizeFileName(string sourcePath, string canonicalPath)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var canonicalFullPath = Path.GetFullPath(canonicalPath);
        if (string.Equals(sourceFullPath, canonicalFullPath, StringComparison.Ordinal))
        {
            return;
        }

        if (!RepositoryPaths.FileSystemPathComparer.Equals(sourceFullPath, canonicalFullPath))
        {
            File.Move(sourceFullPath, canonicalFullPath);
            return;
        }

        var temporaryPath = canonicalFullPath + ".rename-" + Guid.NewGuid().ToString("N");
        File.Move(sourceFullPath, temporaryPath);
        try
        {
            File.Move(temporaryPath, canonicalFullPath);
        }
        catch
        {
            if (File.Exists(temporaryPath) && !File.Exists(sourceFullPath))
            {
                File.Move(temporaryPath, sourceFullPath);
            }

            throw;
        }
    }
}
