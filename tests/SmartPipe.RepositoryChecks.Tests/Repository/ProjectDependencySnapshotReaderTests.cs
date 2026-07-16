using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Repository;

public sealed class ProjectDependencySnapshotReaderTests
{
    [Fact]
    public void ReadDirect_PreservesConditionsMetadataAndRawCentralPropertyReferences()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup Condition="'$(TFM)' == 'net10.0'">
              <ProjectReference Include="../B/B.csproj" Condition="'$(UseB)' == 'true'" PrivateAssets="all" />
              <PackageReference Include="Example.Package" Version="$(ExampleVersion)">
                <PrivateAssets>all</PrivateAssets><IncludeAssets>runtime; build</IncludeAssets><ExcludeAssets>contentFiles</ExcludeAssets>
              </PackageReference>
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        var snapshot = Assert.Single(reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));

        var projectReference = Assert.Single(snapshot.ProjectReferences);
        Assert.Equal("../B/B.csproj", projectReference.Include);
        Assert.Equal("'$(TFM)' == 'net10.0' && '$(UseB)' == 'true'", projectReference.Condition);
        Assert.Equal("all", projectReference.PrivateAssets);
        var packageReference = Assert.Single(snapshot.PackageReferences);
        Assert.Equal("$(ExampleVersion)", packageReference.Version);
        Assert.Equal("all", packageReference.PrivateAssets);
        Assert.Equal("runtime; build", packageReference.IncludeAssets);
        Assert.Equal("contentFiles", packageReference.ExcludeAssets);
    }

    [Fact]
    public async Task ReadRestoredAsync_RemovesAbsolutePaths_AndCanonicalizesArrayOrder()
    {
        using var repository = new RepositoryTestDirectory();
        var first = PackageJson(repository.Path, reverse: false, resolvedVersion: "2.0.0");
        var second = PackageJson(repository.Path, reverse: true, resolvedVersion: "2.0.0");
        var firstRunner = new FakeProcessRunner(new ProcessResult(0, first, string.Empty));
        var secondRunner = new FakeProcessRunner(new ProcessResult(0, second, string.Empty));

        var a = await new ProjectDependencySnapshotReader(firstRunner, "dotnet", TimeSpan.FromSeconds(23)).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken);
        var b = await new ProjectDependencySnapshotReader(secondRunner, "dotnet", TimeSpan.FromSeconds(23)).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.CanonicalJson, b.CanonicalJson);
        Assert.DoesNotContain(repository.Path, a.CanonicalJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/A/A.csproj", a.CanonicalJson, StringComparison.Ordinal);
        var request = Assert.Single(firstRunner.Requests);
        Assert.Equal("dotnet", request.FileName);
        Assert.Equal(["package", "list", "--project", "SmartPipe.Core.slnx", "--include-transitive", "--format", "json", "--output-version", "1", "--no-restore"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(23), request.Timeout);
    }

    [Fact]
    public async Task ReadRestoredAsync_ResolvedVersionChangeChangesSnapshotHash()
    {
        using var repository = new RepositoryTestDirectory();
        var a = await ReadGraph(repository, PackageJson(repository.Path, reverse: false, resolvedVersion: "2.0.0"));
        var b = await ReadGraph(repository, PackageJson(repository.Path, reverse: false, resolvedVersion: "2.0.1"));

        Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsPathOutsideRepository()
    {
        using var repository = new RepositoryTestDirectory();
        var json = PackageJson(Path.GetPathRoot(repository.Path) + "outside/A.csproj", reverse: false, resolvedVersion: "2.0.0", pathIsProject: true);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(0, json, string.Empty)), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsDuplicatePackageIds()
    {
        using var repository = new RepositoryTestDirectory();
        var projectPath = Path.Combine(repository.Path, "src", "A", "A.csproj").Replace('\\', '/');
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            projects = new[]
            {
                new
                {
                    path = projectPath,
                    frameworks = new[]
                    {
                        new
                        {
                            framework = "net10.0",
                            topLevelPackages = new[]
                            {
                                new { id = "Dup", requestedVersion = "1", resolvedVersion = "1" },
                                new { id = "dup", requestedVersion = "1", resolvedVersion = "1" },
                            },
                        },
                    },
                },
            },
        });
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(0, json, string.Empty)), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsMalformedJsonAndProcessFailure()
    {
        using var repository = new RepositoryTestDirectory();
        var malformed = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(0, "{", string.Empty)), "dotnet");
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.ReadRestoredAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        var failed = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(7, "{}", "restore missing")), "dotnet");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => failed.ReadRestoredAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
        Assert.Contains("package list", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<RestoredDependencySnapshot> ReadGraph(RepositoryTestDirectory repository, string json) =>
        new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(0, json, string.Empty)), "dotnet")
            .ReadRestoredAsync(repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken);

    private static string PackageJson(string repositoryRootOrProjectPath, bool reverse, string resolvedVersion, bool pathIsProject = false)
    {
        var aPath = (pathIsProject ? repositoryRootOrProjectPath : Path.Combine(repositoryRootOrProjectPath, "src", "A", "A.csproj")).Replace('\\', '/');
        var zPath = Path.Combine(pathIsProject ? Path.GetDirectoryName(repositoryRootOrProjectPath)! : repositoryRootOrProjectPath, "src", "Z", "Z.csproj").Replace('\\', '/');
        var zeta = new { id = "Zeta", requestedVersion = "[2.0.0, )", resolvedVersion, autoReferenced = "true" };
        var alpha = new { id = "Alpha", requestedVersion = "1.0.0", resolvedVersion = "1.0.0", autoReferenced = "false" };
        var tail = new { id = "Tail", resolvedVersion = "3.0.0" };
        var @base = new { id = "Base", resolvedVersion = "1.0.0" };
        object a = new
        {
            path = aPath,
            frameworks = new[]
            {
                new
                {
                    framework = "net10.0",
                    topLevelPackages = reverse ? new[] { alpha, zeta } : new[] { zeta, alpha },
                    transitivePackages = reverse ? new[] { @base, tail } : new[] { tail, @base },
                },
            },
        };
        var net9 = new { framework = "net9.0" };
        var net8 = new { framework = "net8.0" };
        object z = new
        {
            path = zPath,
            frameworks = reverse ? new[] { net8, net9 } : new[] { net9, net8 },
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            parameters = "--include-transitive",
            projects = reverse ? new[] { a, z } : new[] { z, a },
        });
    }

    private static ProjectIdentitySnapshot Identity(string path) => new(path, "A", "1.0.0", "net10.0", "A");
}
