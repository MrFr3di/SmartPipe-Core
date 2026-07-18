using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class BaselinePackageProvisioner(INuGetPackageFetcher fetcher)
{
    public async Task ProvisionAsync(VerifyBaselineOptions options, CancellationToken cancellationToken)
    {
        if (options.Offline)
        {
            throw new InvalidOperationException("Package provisioning is unavailable offline.");
        }

        var manifest = BaselineManifestSerializer.Deserialize(
            await File.ReadAllTextAsync(options.ManifestPath, cancellationToken).ConfigureAwait(false));
        Directory.CreateDirectory(options.PackagesDirectory);

        foreach (var package in manifest.Packages)
        {
            var canonicalPath = Path.Combine(options.PackagesDirectory, package.FileName);
            var existingPaths = Directory.EnumerateFiles(options.PackagesDirectory)
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
                package.Id, package.Version, options.PackagesDirectory, cancellationToken).ConfigureAwait(false);
            NormalizeFileName(fetchedPath, canonicalPath);
        }
    }

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
