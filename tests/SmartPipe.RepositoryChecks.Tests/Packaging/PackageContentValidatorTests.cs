using System.Text;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

public sealed class PackageContentValidatorTests
{
    [Theory]
    [InlineData("identity", "SPMETA001")]
    [InlineData("description", "SPMETA002")]
    [InlineData("authors", "SPMETA003")]
    [InlineData("repository", "SPMETA004")]
    [InlineData("readme", "SPMETA005")]
    [InlineData("release-notes", "SPMETA006")]
    [InlineData("required-content", "SPMETA007")]
    [InlineData("source-content", "SPMETA008")]
    [InlineData("unexpected-tfm", "SPMETA009")]
    [InlineData("missing-symbols", "SPMETA010")]
    public async Task MetadataMutationProducesStableDiagnostic(string mutation, string code)
    {
        var metadata = Metadata();
        var mode = PackageGraphMode.Current;
        var symbolPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.snupkg");
        metadata = mutation switch
        {
            "identity" => metadata with { Snapshot = metadata.Snapshot with { Id = "Wrong" } },
            "description" => metadata with { Description = "short" },
            "authors" => metadata with { Authors = "Other" },
            "repository" => metadata with { RepositoryCommit = "bad" },
            "readme" => metadata with { Readme = "other.md" },
            "release-notes" => metadata with { ReleaseNotes = null },
            "source-content" => WithFiles(metadata, "src/Foo.cs"),
            "unexpected-tfm" => WithFiles(metadata, "lib/net9.0/SmartPipe.Core.dll"),
            _ => metadata,
        };
        if (mutation == "release-notes") mode = PackageGraphMode.Release;
        var errors = await new PackageContentValidator().ValidateAsync(Node(), "2.2.0", metadata, symbolPath, mode, TestContext.Current.CancellationToken);
        Assert.Contains(errors, x => x.Code == code);
    }

    [Fact]
    public async Task SymbolNuspecIdentityIsParsedStructurally()
    {
        using var symbols = SyntheticNuGetPackage.Create(packageId: "symbols", version: "1.0.0", nuspecPath: "SmartPipe.Core.nuspec",
            nuspec: Nuspec("Wrong.Id", "2.2.0", "0000000000000000000000000000000000000000") + "<!-- <id>SmartPipe.Core</id> -->",
            entries: [("lib/net10.0/SmartPipe.Core.pdb", Encoding.UTF8.GetBytes("not-portable"))]);
        var errors = await new PackageContentValidator().ValidateAsync(Node(), "2.2.0", Metadata(), symbols.Path, PackageGraphMode.Current, TestContext.Current.CancellationToken);
        Assert.Contains(errors, x => x.Code == "SPMETA011");
    }

    private static PackageMetadata Metadata() => new(
        "unused.nupkg",
        new NuGetPackageSnapshot
        {
            Id = "SmartPipe.Core",
            Version = "2.2.0",
            Assets = new PackageAssetSnapshot { PackageId = "SmartPipe.Core", Version = "2.2.0", Files = [], Assemblies = [] },
            Dependencies = new PackageDependencySnapshot { PackageId = "SmartPipe.Core", Version = "2.2.0", Groups = [] },
        },
        "A package-specific description for SmartPipe Core.", "SmartPipe", "Copyright SmartPipe 2026", "MIT",
        "https://github.com/MrFr3di/SmartPipe-Core", "git", "0000000000000000000000000000000000000000",
        "README.md", "icon.png", "smartpipe core", "notes");

    private static PackageMetadata WithFiles(PackageMetadata metadata, params string[] files) => metadata with
    {
        Snapshot = metadata.Snapshot with { Assets = metadata.Snapshot.Assets with { Files = files.Select(path => new PackageFileSnapshot { Path = path, UncompressedLength = 1, Sha256 = new string('0', 64), Category = "other" }).ToArray() } },
    };

    private static PackageNode Node()
    {
        var policy = new DependencyPolicy { RequiredSmartPipePackages = [], AllowedSmartPipePackages = [], AllowedExternalPackages = [], ForbiddenPackagePatterns = [] };
        return new() { Id = "SmartPipe.Core", ProjectPath = "src/SmartPipe.Core/SmartPipe.Core.csproj", Lifecycle = PackageLifecycle.Active, ActivationEpic = "existing", ScaffoldKind = null, PublishOrder = 1, BaselineVersion = "2.1.2", AotContract = PackageAotContract.Full, CurrentDependencies = policy, ReleaseDependencies = policy, TemporaryAllowances = [], ConsumerScenarios = [] };
    }

    private static string Nuspec(string id, string version, string commit) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
        <id>{id}</id><version>{version}</version><authors>SmartPipe</authors><description>symbols</description>
        <repository type="git" url="https://github.com/MrFr3di/SmartPipe-Core" commit="{commit}" />
        </metadata></package>
        """;
}
