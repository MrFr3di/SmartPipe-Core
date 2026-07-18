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

    [Theory]
    [InlineData("<Project xmlns=\"urn:test\"><ItemGroup><PackageReference Include=\"A\" Version=\"1\" /></ItemGroup></Project>")]
    [InlineData("<Project><Target><ItemGroup><PackageReference Include=\"A\" Version=\"1\" /></ItemGroup></Target></Project>")]
    [InlineData("<Project xmlns:x=\"urn:test\"><ItemGroup><x:ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></Project>")]
    [InlineData("<Project><Choose><When Condition=\"true\"><ItemGroup><ProjectReference Include=\"../B/B.csproj\" /></ItemGroup></When></Choose></Project>")]
    public void ReadDirect_RejectsReferenceLikeXmlOutsideExactDirectSchema(string projectXml)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", projectXml);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        Assert.Throws<InvalidDataException>(() => reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));
    }

    [Theory]
    [InlineData("<PackageReference Include=\"A\" Version=\"1\"><Version>1</Version></PackageReference>")]
    [InlineData("<PackageReference Include=\"A\"><PrivateAssets>all</PrivateAssets><PrivateAssets>all</PrivateAssets></PackageReference>")]
    [InlineData("<PackageReference Include=\"A\" Unsupported=\"x\" />")]
    public void ReadDirect_RejectsDuplicateMetadataAndUnsupportedReferenceAttributes(string referenceXml)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", $"<Project><ItemGroup>{referenceXml}</ItemGroup></Project>");
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        Assert.Throws<InvalidDataException>(() => reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));
    }

    [Fact]
    public void ReadDirect_RejectsCaseInsensitiveSemanticDuplicatePackageReferences()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup>
              <PackageReference Include="Example.Package" Version="1" PrivateAssets="all" />
              <PackageReference Include="example.package" Version="1" PrivateAssets="all" />
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadDirect_RejectsExactSemanticDuplicateProjectReferences()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup>
              <ProjectReference Include="../B/B.csproj" Condition="true" />
              <ProjectReference Include="../B/B.csproj" Condition="true" />
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = Assert.Throws<InvalidDataException>(() => reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadDirect_UsesFileSystemCaseSemanticsForProjectReferenceIdentity()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup>
              <ProjectReference Include="../B/B.csproj" Condition="true" />
              <ProjectReference Include="../b/B.csproj" Condition="true" />
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        if (OperatingSystem.IsWindows())
        {
            var exception = Assert.Throws<InvalidDataException>(() => reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));
            Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            var references = Assert.Single(reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")])).ProjectReferences;
            Assert.Equal(["../B/B.csproj", "../b/B.csproj"], references.Select(reference => reference.Include));
        }
    }

    [Fact]
    public void ReadDirect_OrdersProjectReferenceIncludesOrdinally()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup>
              <ProjectReference Include="../a/a.csproj" />
              <ProjectReference Include="../Z/Z.csproj" />
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        var references = Assert.Single(reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")])).ProjectReferences;

        Assert.Equal(["../Z/Z.csproj", "../a/a.csproj"], references.Select(reference => reference.Include));
    }

    [Fact]
    public void ReadDirect_UsesTotalOrdinalOrderAcrossAllReferenceFields()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", """
            <Project><ItemGroup>
              <PackageReference Include="Same" Version="2" Condition="B" />
              <PackageReference Include="Same" Version="1" Condition="B" />
              <PackageReference Include="Same" Version="1" Condition="A" />
            </ItemGroup></Project>
            """);
        var reader = new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet");

        var references = Assert.Single(reader.ReadDirect(repository.Path, [Identity("src/A/A.csproj")])).PackageReferences;

        Assert.Equal(["A|1", "B|1", "B|2"], references.Select(reference => $"{reference.Condition}|{reference.Version}"));
    }

    [Fact]
    public async Task ReadRestoredAsync_RemovesAbsolutePaths_AndCanonicalizesArrayOrder()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
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
        Assert.Equal(["package", "list", "--project", Path.Combine(repository.Path, "SmartPipe.Core.slnx"), "--include-transitive", "--format", "json", "--output-version", "1", "--no-restore"], request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(23), request.Timeout);
    }

    [Fact]
    public async Task ReadRestoredAsync_RehydratesRedactedProjectPathWithinRepositoryRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(home, "SmartPipe.RepositoryChecks.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src", "A"));
            await File.WriteAllTextAsync(Path.Combine(root, "SmartPipe.Core.slnx"), "<Solution />", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "A", "A.csproj"), "<Project />", TestContext.Current.CancellationToken);
            var projectPath = Path.Combine(root, "src", "A", "A.csproj");
            var redactedPath = "<home>" + projectPath[home.Length..].Replace('\\', '/');
            var json = SingleProjectJson(redactedPath, [new Dictionary<string, object?> { ["framework"] = "net10.0" }]);

            var snapshot = await CreateRestoredReader(json).ReadRestoredAsync(
                root, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken);

            Assert.Equal("src/A/A.csproj", Assert.Single(snapshot.Projects).ProjectPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadRestoredAsync_ResolvedVersionChangeChangesSnapshotHash()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var a = await ReadGraph(repository, PackageJson(repository.Path, reverse: false, resolvedVersion: "2.0.0"));
        var b = await ReadGraph(repository, PackageJson(repository.Path, reverse: false, resolvedVersion: "2.0.1"));

        Assert.NotEqual(a.Sha256, b.Sha256);
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsPathOutsideRepository()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
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
        PrepareRestoredFiles(repository);
        var projectPath = Path.Combine(repository.Path, "src", "A", "A.csproj").Replace('\\', '/');
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            parameters = "--include-transitive",
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
        PrepareRestoredFiles(repository);
        var malformed = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(0, "{", string.Empty)), "dotnet");
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.ReadRestoredAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        var failed = new ProjectDependencySnapshotReader(new FakeProcessRunner(new ProcessResult(7, "{}", "restore missing")), "dotnet");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => failed.ReadRestoredAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
        Assert.Contains("package list", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--include-transitive --no-restore")]
    public async Task ReadRestoredAsync_RequiresExactIncludeTransitiveParameters(string? parameters)
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var root = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["projects"] = Array.Empty<object>(),
        };
        if (parameters is not null)
        {
            root["parameters"] = parameters;
        }

        var json = System.Text.Json.JsonSerializer.Serialize(root);
        var reader = CreateRestoredReader(json);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsEmptyProjectsAndEmptyFrameworks()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var emptyProjects = "{\"version\":1,\"parameters\":\"--include-transitive\",\"projects\":[]}";
        await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(emptyProjects).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));

        var emptyFrameworks = SingleProjectJson(Path.Combine(repository.Path, "src", "A", "A.csproj"), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(emptyFrameworks).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsProjectAndFrameworkCountsAboveBounds()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var projectPath = Path.Combine(repository.Path, "src", "A", "A.csproj");
        for (var index = 0; index < 257; index++)
        {
            repository.Write($"src/A/A{index}.csproj", "<Project />");
        }

        var tooManyProjects = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            parameters = "--include-transitive",
            projects = Enumerable.Range(0, 257).Select(index => new
            {
                path = projectPath.Replace("A.csproj", $"A{index}.csproj", StringComparison.Ordinal),
                frameworks = new[] { new { framework = "net10.0" } },
            }),
        });
        var projectCountException = await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(tooManyProjects).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
        Assert.Contains("256", projectCountException.Message, StringComparison.Ordinal);

        var frameworks = Enumerable.Range(0, 65).Select(index => new Dictionary<string, object?>
        {
            ["framework"] = $"net{index}.0",
        }).ToArray();
        var tooManyFrameworks = SingleProjectJson(projectPath, frameworks);
        await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(tooManyFrameworks).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsPackageCountAboveBoundBeforeStoringAllEntries()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var projectPath = Path.Combine(repository.Path, "src", "A", "A.csproj");
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            parameters = "--include-transitive",
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
                            transitivePackages = Enumerable.Range(0, 4097).Select(index => new
                            {
                                id = $"Package.{index}",
                                resolvedVersion = "1.0.0",
                            }),
                        },
                    },
                },
            },
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(json).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("4096", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\outside\\A.csproj")]
    [InlineData("\\\\server\\share\\A.csproj")]
    public async Task ReadRestoredAsync_RejectsPortableAbsoluteProjectPaths(string path)
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(
            SingleProjectJson(path, [new Dictionary<string, object?> { ["framework"] = "net10.0" }])).ReadRestoredAsync(
                repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("src/A")]
    [InlineData("src/A/Missing.csproj")]
    [InlineData("src/A/not-project.txt")]
    public async Task ReadRestoredAsync_RequiresExistingRegularCsprojFile(string relativePath)
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        repository.Write("src/A/not-project.txt", "x");
        var path = Path.Combine(repository.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var json = SingleProjectJson(path, [new Dictionary<string, object?> { ["framework"] = "net10.0" }]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(json).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRestoredAsync_RejectsLinkedProjectPathComponent()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        if (!repository.TryCreateDirectoryLink("linked", "src/A"))
        {
            return;
        }

        var linkedPath = Path.Combine(repository.Path, "linked", "A.csproj");
        var json = SingleProjectJson(linkedPath, [new Dictionary<string, object?> { ["framework"] = "net10.0" }]);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CreateRestoredReader(json).ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRestoredAsync_PropagatesCanceledProcessAsCancellation()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var reader = new ProjectDependencySnapshotReader(
            new FakeProcessRunner(new ProcessRunnerException(ProcessFailureKind.Canceled, "canceled")),
            "dotnet");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadRestoredAsync_WrapsNonCancellationProcessFailure()
    {
        using var repository = new RepositoryTestDirectory();
        PrepareRestoredFiles(repository);
        var reader = new ProjectDependencySnapshotReader(
            new FakeProcessRunner(new ProcessRunnerException(ProcessFailureKind.Timeout, "timeout")),
            "dotnet");

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadRestoredAsync(
            repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("C:\\outside\\B.csproj")]
    [InlineData("\\\\server\\share\\B.csproj")]
    public void ReadDirect_RejectsPortableAbsoluteProjectReference(string include)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("src/A/A.csproj", $"<Project><ItemGroup><ProjectReference Include=\"{include}\" /></ItemGroup></Project>");

        Assert.Throws<InvalidDataException>(() => new ProjectDependencySnapshotReader(new FakeProcessRunner(), "dotnet")
            .ReadDirect(repository.Path, [Identity("src/A/A.csproj")]));
    }

    private static Task<RestoredDependencySnapshot> ReadGraph(RepositoryTestDirectory repository, string json) =>
        CreateRestoredReader(json)
            .ReadRestoredAsync(repository.Path, "SmartPipe.Core.slnx", TestContext.Current.CancellationToken);

    private static ProjectDependencySnapshotReader CreateRestoredReader(string json) =>
        new(new FakeProcessRunner(new ProcessResult(0, json, string.Empty)), "dotnet");

    private static void PrepareRestoredFiles(RepositoryTestDirectory repository)
    {
        repository.Write("SmartPipe.Core.slnx", "<Solution />");
        repository.Write("Repo.slnx", "<Solution />");
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/Z/Z.csproj", "<Project />");
    }

    private static string SingleProjectJson(string path, IReadOnlyList<Dictionary<string, object?>> frameworks) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1,
            parameters = "--include-transitive",
            projects = new[] { new { path, frameworks } },
        });

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
