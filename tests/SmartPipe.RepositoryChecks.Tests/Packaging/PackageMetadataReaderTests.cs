using System.Text;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Serialization;
using SmartPipe.RepositoryChecks.Tests.NuGet;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class PackageMetadataReaderTests
{
    [Fact]
    public async Task Read_RejectsDtdAndMissingRequiredRepositoryMetadata()
    {
        using var dtd = SyntheticNuGetPackage.Create(nuspec: "<!DOCTYPE package [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><package><metadata><id>SmartPipe.Core</id><version>2.2.0</version></metadata></package>");
        var dtdError = await Assert.ThrowsAsync<RepositoryCheckException>(() => new PackageMetadataReader().ReadAsync(dtd.Path, TestContext.Current.CancellationToken));
        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, dtdError.ExitCode);

        using var missing = SyntheticNuGetPackage.Create(version: "2.2.0", nuspec: SyntheticNuGetPackage.CreateNuspec("SmartPipe.Core", "2.2.0"));
        var metadataError = await Assert.ThrowsAsync<RepositoryCheckException>(() => new PackageMetadataReader().ReadAsync(missing.Path, TestContext.Current.CancellationToken));
        Assert.Equal(ExitCodes.PackedPackageViolation, metadataError.ExitCode);
    }

    [Fact]
    public async Task Command_MapsDuplicateCaseInsensitiveArchiveEntriesToStableMetadataDiagnostic()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/SmartPipe.Core/SmartPipe.Core.csproj", "<Project />");
        Directory.CreateDirectory(Path.Combine(repository.Path, "packages"));
        using var package = SyntheticNuGetPackage.Create(version: "2.2.0", entries:
        [
            ("README.md", Encoding.UTF8.GetBytes("one")),
            ("readme.md", Encoding.UTF8.GetBytes("two")),
        ]);
        File.Copy(package.Path, Path.Combine(repository.Path, "packages/SmartPipe.Core.2.2.0.nupkg"));
        var graph = Graph();
        repository.Write("eng/package-graph.json", CanonicalJson.Serialize(graph, RepositoryChecksJsonContext.Default.PackageGraphDocument));

        var report = await new VerifyPackageMetadataCommand(new PackageGraphLoader(false)).ExecuteAsync(new(
            repository.Path, Path.Combine(repository.Path, "eng/package-graph.json"), Path.Combine(repository.Path, "packages"), PackageGraphMode.Current, null), TestContext.Current.CancellationToken);

        var violation = Assert.Single(report.Violations);
        Assert.Equal("SPMETA016", violation.Code);
        Assert.Contains("duplicate normalized paths", violation.Rule, StringComparison.OrdinalIgnoreCase);
    }

    private static PackageGraphDocument Graph()
    {
        var policy = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        return new()
        {
            SchemaVersion = 1,
            ReleaseVersion = "2.2.0",
            Packages = [new()
        {
            Id = "SmartPipe.Core", ProjectPath = "src/SmartPipe.Core/SmartPipe.Core.csproj", Lifecycle = PackageLifecycle.Active,
            ActivationEpic = "existing", ScaffoldKind = null, PublishOrder = 1, BaselineVersion = "2.1.2", AotContract = PackageAotContract.Full,
            CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [],
        }]
        };
    }
}
