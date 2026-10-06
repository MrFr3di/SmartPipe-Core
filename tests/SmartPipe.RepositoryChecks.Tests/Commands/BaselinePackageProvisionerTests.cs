using System.Text.Json;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Tests.Baselines;

namespace SmartPipe.RepositoryChecks.Tests.Commands;

public sealed class BaselinePackageProvisionerTests
{
    [Fact]
    public async Task ProvisionAsync_NormalizesPreExistingLowercaseFileWithoutFetching()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartPipe.ProvisionerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var packages = Path.Combine(root, "packages");
            var manifestPath = Path.Combine(root, "manifest.json");
            Directory.CreateDirectory(packages);
            var manifest = BaselineFixtures.CreateManifest();
            await File.WriteAllTextAsync(manifestPath, BaselineManifestSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            foreach (var package in manifest.Packages)
            {
                await File.WriteAllTextAsync(Path.Combine(packages, package.FileName.ToLowerInvariant()), "existing", TestContext.Current.CancellationToken);
            }
            var fetcher = new LowercaseFetcher();

            await new BaselinePackageProvisioner(fetcher).ProvisionAsync(
                new ProvisionBaselineOptions(root, manifestPath, packages),
                TestContext.Current.CancellationToken);

            Assert.Empty(fetcher.Requests);
            Assert.Equal(
                manifest.Packages.Select(package => package.FileName).Order(StringComparer.Ordinal),
                Directory.EnumerateFiles(packages).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProvisionAsync_RejectsMissingManifestBeforeCreatingPackagesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartPipe.ProvisionerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var packages = Path.Combine(root, "packages");
            var manifestPath = Path.Combine(root, "manifest.json");
            Directory.CreateDirectory(root);
            var fetcher = new LowercaseFetcher();

            await Assert.ThrowsAsync<FileNotFoundException>(() => new BaselinePackageProvisioner(fetcher).ProvisionAsync(
                new ProvisionBaselineOptions(root, manifestPath, packages),
                TestContext.Current.CancellationToken));

            Assert.Empty(fetcher.Requests);
            Assert.False(Directory.Exists(packages));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProvisionAsync_RejectsMalformedManifestBeforeCreatingPackagesDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartPipe.ProvisionerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var packages = Path.Combine(root, "packages");
            var manifestPath = Path.Combine(root, "manifest.json");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(manifestPath, "{", TestContext.Current.CancellationToken);
            var fetcher = new LowercaseFetcher();

            await Assert.ThrowsAsync<JsonException>(() => new BaselinePackageProvisioner(fetcher).ProvisionAsync(
                new ProvisionBaselineOptions(root, manifestPath, packages),
                TestContext.Current.CancellationToken));

            Assert.Empty(fetcher.Requests);
            Assert.False(Directory.Exists(packages));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProvisionAsync_FetchesOnlyMissingPackageAndUsesManifestFileName()
    {
        var root = Path.Combine(Path.GetTempPath(), "SmartPipe.ProvisionerTests", Guid.NewGuid().ToString("N"));
        try
        {
            var packages = Path.Combine(root, "packages");
            var manifestPath = Path.Combine(root, "eng", "baselines", "2.1.2", "manifest.json");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var manifest = BaselineFixtures.CreateManifest();
            await File.WriteAllTextAsync(manifestPath, BaselineManifestSerializer.Serialize(manifest), TestContext.Current.CancellationToken);
            foreach (var package in manifest.Packages.Skip(1))
            {
                await File.WriteAllTextAsync(Path.Combine(packages, package.FileName), "existing", TestContext.Current.CancellationToken);
            }

            var fetcher = new LowercaseFetcher();
            await new BaselinePackageProvisioner(fetcher).ProvisionAsync(
                new ProvisionBaselineOptions(root, manifestPath, packages),
                TestContext.Current.CancellationToken);

            var missing = manifest.Packages[0];
            Assert.Equal([(missing.Id, missing.Version)], fetcher.Requests);
            Assert.Contains(Directory.EnumerateFiles(packages).Select(Path.GetFileName),
                name => string.Equals(name, missing.FileName, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LowercaseFetcher : INuGetPackageFetcher
    {
        public List<(string Id, string Version)> Requests { get; } = [];

        public async Task<string> FetchAsync(string packageId, string version, string destinationDirectory, CancellationToken cancellationToken)
        {
            Requests.Add((packageId, version));
            var path = Path.Combine(destinationDirectory, $"{packageId}.{version}.nupkg".ToLowerInvariant());
            await File.WriteAllTextAsync(path, "fetched", cancellationToken);
            return path;
        }
    }
}
