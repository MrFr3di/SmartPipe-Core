using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Consumers;

[Trait("Category", "PackageInfrastructure")]
public sealed class LocalNuGetConfigWriterTests
{
    [Fact]
    public async Task CentralPackagesWriter_EmitsCanonicalLfExactDirectVersions()
    {
        using var fixture = new RepositoryTestDirectory();
        var path = await new ConsumerCentralPackagesWriter().WriteAsync(fixture.Path,
            ["SmartPipe.Extensions.Json", "SmartPipe.Core"],
            "2.2.0",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Microsoft.Extensions.DependencyInjection"] = "10.0.8",
            },
            TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain((byte)'\r', bytes);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>", text);
        Assert.Contains("<CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>", text);
        Assert.Equal(3, text.Split("<PackageVersion Include=", StringSplitOptions.None).Length - 1);
        Assert.Contains("<PackageVersion Include=\"Microsoft.Extensions.DependencyInjection\" Version=\"10.0.8\" />", text);
        Assert.True(text.IndexOf("SmartPipe.Core", StringComparison.Ordinal) < text.IndexOf("SmartPipe.Extensions.Json", StringComparison.Ordinal));
    }

    [Fact]
    public void TrackedScenarioProjects_AreVersionlessCpmConsumers()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var projects = Directory.EnumerateFiles(Path.Combine(root, "tests", "Consumers", "Scenarios"), "*.csproj", SearchOption.AllDirectories).ToArray();
        Assert.Equal(14, projects.Length);
        Assert.All(projects, project => Assert.DoesNotContain(" Version=", File.ReadAllText(project), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Write_UsesContainedFeedAndStrictSourceMapping()
    {
        using var fixture = new RepositoryTestDirectory();
        var feed = Path.Combine(fixture.Path, "feed"); Directory.CreateDirectory(feed);
        var workspace = Path.Combine(fixture.Path, "workspace"); Directory.CreateDirectory(workspace);
        var path = await new LocalNuGetConfigWriter().WriteAsync(
            workspace,
            feed,
            TestContext.Current.CancellationToken,
            externalPackagePatterns: ["Microsoft.Extensions.Logging.Abstractions", "CsvHelper"]);
        var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.Contains("<clear />", text);
        Assert.Contains("<package pattern=\"SmartPipe.*\" />", text);
        Assert.Contains("<package pattern=\"CsvHelper\" />", text);
        Assert.Contains("<package pattern=\"Microsoft.Extensions.Logging.Abstractions\" />", text);
        Assert.DoesNotContain("<package pattern=\"*\" />", text);
        Assert.DoesNotContain("\\", text);
    }

    [Fact]
    public async Task Write_RejectsOutputOutsideWorkspace()
    {
        using var fixture = new RepositoryTestDirectory();
        var feed = Path.Combine(fixture.Path, "feed"); Directory.CreateDirectory(feed);
        var error = await Assert.ThrowsAsync<ConsumerScenarioException>(() => new LocalNuGetConfigWriter().WriteAsync(
            Path.Combine(fixture.Path, "..", "outside"),
            feed,
            TestContext.Current.CancellationToken,
            fixture.Path,
            ["Microsoft.Extensions.Logging.Abstractions"]));
        Assert.Equal("SPCONS009", error.Code);
    }
}
