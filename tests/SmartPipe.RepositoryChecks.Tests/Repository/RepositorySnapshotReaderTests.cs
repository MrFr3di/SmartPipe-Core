using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Repository;

public sealed class RepositorySnapshotReaderTests
{
    [Fact]
    public async Task ReadPackableProjectsAsync_UsesStrictSlnxEnumeration_AndEvaluatedProperties()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("SmartPipe.Core.slnx", """
            <Solution><Folder Name="/src/">
              <Project Path="src/Z/Z.csproj" />
              <Project Path="src/A/A.csproj" />
            </Folder></Solution>
            """);
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/Z/Z.csproj", "<Project />");
        var runner = new FakeProcessRunner(
            SuccessProperties("A.Package", "2.1.2", "net10.0", true, "A"),
            SuccessProperties("Z.Tests", "2.1.2", "net10.0", false, "Z.Tests"));
        var reader = new RepositorySnapshotReader(runner, "dotnet-custom", TimeSpan.FromSeconds(19));

        var projects = await reader.ReadPackableProjectsAsync(
            repository.Path,
            "SmartPipe.Core.slnx",
            TestContext.Current.CancellationToken);

        var project = Assert.Single(projects);
        Assert.Equal("src/A/A.csproj", project.ProjectPath);
        Assert.Equal("A.Package", project.PackageId);
        Assert.Equal("2.1.2", project.Version);
        Assert.Equal("net10.0", project.TargetFramework);
        Assert.Equal("A", project.AssemblyName);
        Assert.Collection(
            runner.Requests,
            request => AssertMsbuildRequest(request, "dotnet-custom", Path.Combine(repository.Path, "src", "A", "A.csproj")),
            request => AssertMsbuildRequest(request, "dotnet-custom", Path.Combine(repository.Path, "src", "Z", "Z.csproj")));
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_OnlyEvaluatesProductionProjectsUnderSrc()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", """
            <Solution>
              <Folder Name="/benchmarks/"><Project Path="benchmarks/Bench/Bench.csproj" /></Folder>
              <Folder Name="/src/"><Project Path="src/A/A.csproj" /><Project Path="src/New/New.csproj" /></Folder>
            </Solution>
            """);
        repository.Write("benchmarks/Bench/Bench.csproj", "<Project />");
        repository.Write("src/A/A.csproj", "<Project />");
        repository.Write("src/New/New.csproj", "<Project />");
        var runner = new FakeProcessRunner(
            SuccessProperties("A", "1.0.0", "net10.0", true, "A"),
            SuccessProperties("New", "1.0.0", "net10.0", true, "New"));
        var reader = new RepositorySnapshotReader(runner, "dotnet");

        var projects = await reader.ReadPackableProjectsAsync(
            repository.Path,
            "Repo.slnx",
            TestContext.Current.CancellationToken);

        Assert.Equal(["src/A/A.csproj", "src/New/New.csproj"], projects.Select(static project => project.ProjectPath));
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsPhysicalSrcProjectOutsideSemanticSrcFolder()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/misc/\"><Project Path=\"src/A/A.csproj\" /></Folder></Solution>");
        repository.Write("src/A/A.csproj", "<Project />");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path,
            "Repo.slnx",
            TestContext.Current.CancellationToken));

        Assert.Contains("/src/", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsDuplicateProjectPaths()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A.csproj\"/><Project Path=\"src/A.csproj\"/></Folder></Solution>");
        repository.Write("src/A.csproj", "<Project />");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsProjectPathEscapingRepository()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"../escape.csproj\"/></Folder></Solution>");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("outside", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_FailsClosedForEmptyEvaluatedProperty()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A.csproj\"/></Folder></Solution>");
        repository.Write("src/A.csproj", "<Project />");
        var runner = new FakeProcessRunner(SuccessProperties(string.Empty, "2.1.2", "net10.0", true, "A"));
        var reader = new RepositorySnapshotReader(runner, "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("PackageId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_ReportsProcessFailureWithoutParsingOutput()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A.csproj\"/></Folder></Solution>");
        repository.Write("src/A.csproj", "<Project />");
        var runner = new FakeProcessRunner(new ProcessResult(1, "{}", "failed"));
        var reader = new RepositorySnapshotReader(runner, "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("MSBuild", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<Solution Extra=\"x\"><Project Path=\"src/A.csproj\" /></Solution>")]
    [InlineData("<Solution><Folder Name=\"/src/\" Extra=\"x\"><Project Path=\"src/A.csproj\" /></Folder></Solution>")]
    [InlineData("<Solution><Unknown /></Solution>")]
    [InlineData("<Solution xmlns:x=\"urn:test\"><x:Project Path=\"src/A.csproj\" /></Solution>")]
    [InlineData("<Solution><Project Path=\"src/A.csproj\"><Project Path=\"src/B.csproj\" /></Project></Solution>")]
    [InlineData("<Solution><Folder Name=\"/src/\"><Unknown /></Folder></Solution>")]
    public async Task ReadPackableProjectsAsync_RejectsSlnxOutsideStrictSchema(string slnx)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", slnx);
        repository.Write("src/A.csproj", "<Project />");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsProjectCountAboveBound()
    {
        using var repository = new RepositoryTestDirectory();
        var projects = string.Concat(Enumerable.Range(0, 257).Select(index => $"<Project Path=\"src/P{index}.csproj\" />"));
        repository.Write("Repo.slnx", $"<Solution><Folder Name=\"/src/\">{projects}</Folder></Solution>");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("256", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\outside\\A.csproj")]
    [InlineData("\\\\server\\share\\A.csproj")]
    public async Task ReadPackableProjectsAsync_RejectsPortableAbsoluteProjectPath(string path)
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", $"<Solution><Folder Name=\"/src/\"><Project Path=\"{path}\" /></Folder></Solution>");
        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsLinkedSolutionFile()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Actual.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A.csproj\" /></Folder></Solution>");
        repository.Write("src/A.csproj", "<Project />");
        if (!repository.TryCreateFileLink("Repo.slnx", "Actual.slnx"))
        {
            return;
        }

        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_RejectsLinkedProjectDirectoryComponent()
    {
        using var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"linked/A.csproj\" /></Folder></Solution>");
        repository.Write("actual/A.csproj", "<Project />");
        if (!repository.TryCreateDirectoryLink("linked", "actual"))
        {
            return;
        }

        var reader = new RepositorySnapshotReader(new FakeProcessRunner(), "dotnet");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));

        Assert.Contains("link", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_PropagatesCanceledProcessAsCancellation()
    {
        using var repository = CreateSingleProjectRepository();
        var runner = new FakeProcessRunner(new ProcessRunnerException(ProcessFailureKind.Canceled, "canceled"));
        var reader = new RepositorySnapshotReader(runner, "dotnet");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadPackableProjectsAsync_WrapsNonCancellationProcessFailure()
    {
        using var repository = CreateSingleProjectRepository();
        var runner = new FakeProcessRunner(new ProcessRunnerException(ProcessFailureKind.StartFailure, "failed"));
        var reader = new RepositorySnapshotReader(runner, "dotnet");

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadPackableProjectsAsync(
            repository.Path, "Repo.slnx", TestContext.Current.CancellationToken));
    }

    private static RepositoryTestDirectory CreateSingleProjectRepository()
    {
        var repository = new RepositoryTestDirectory();
        repository.Write("Repo.slnx", "<Solution><Folder Name=\"/src/\"><Project Path=\"src/A.csproj\" /></Folder></Solution>");
        repository.Write("src/A.csproj", "<Project />");
        return repository;
    }

    private static ProcessResult SuccessProperties(
        string packageId,
        string version,
        string targetFramework,
        bool isPackable,
        string assemblyName) => new(
            0,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                Properties = new
                {
                    PackageId = packageId,
                    Version = version,
                    TargetFramework = targetFramework,
                    IsPackable = isPackable.ToString().ToLowerInvariant(),
                    AssemblyName = assemblyName,
                },
            }),
            string.Empty);

    private static void AssertMsbuildRequest(ProcessRequest request, string dotnet, string projectPath)
    {
        Assert.Equal(dotnet, request.FileName);
        Assert.Equal(
            ["msbuild", projectPath, "-nologo", "-getProperty:PackageId", "-getProperty:Version", "-getProperty:TargetFramework", "-getProperty:IsPackable", "-getProperty:AssemblyName"],
            request.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(19), request.Timeout);
    }
}
