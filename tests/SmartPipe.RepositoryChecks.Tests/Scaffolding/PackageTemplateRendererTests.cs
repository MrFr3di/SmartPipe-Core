using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Tests.Repository;
using System.Security.Cryptography;
using System.Text;

namespace SmartPipe.RepositoryChecks.Tests.Scaffolding;

[Collection(ExternalProcessCollection.Name)]
public sealed class PackageTemplateRendererTests
{
    [Theory]
    [InlineData("SmartPipe.Extensions.Channels", "CoreLeaf", "c859635605ac30a30e1941812f2b0176f6f62e1d90c1b85280c7675494efa945")]
    [InlineData("SmartPipe.Extensions.Polly", "FrameworkIntegration", "f0e33fa5d69c7ee994ba8722a6cf57658d332e6151991c08f7163e6bc88c1ad7")]
    [InlineData("SmartPipe.Testing", "Testing", "0eb49150fbeefb036650dff14852916d603482482b21ba6b34041f02577d2424")]
    public async Task Render_AllKindsAreDeterministicLfOnlySnapshots(string id, string kind, string expectedSnapshot)
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var node = graph.Packages.Single(x => x.Id == id);
        // Active leaves keep renderer coverage for their kind by being re-planned in memory.
        var activeKind = id switch
        {
            "SmartPipe.Extensions.Channels" => PackageScaffoldKind.CoreLeaf,
            "SmartPipe.Extensions.Polly" => PackageScaffoldKind.FrameworkIntegration,
            "SmartPipe.Testing" => PackageScaffoldKind.Testing,
            _ => (PackageScaffoldKind?)null,
        };
        if (activeKind is { } scaffoldKind)
        {
            node = node with { Lifecycle = PackageLifecycle.Planned, ScaffoldKind = scaffoldKind };
            graph = graph with
            {
                Packages = graph.Packages.Select(item => item.Id == id ? node : item).ToArray(),
            };
        }
        var first = new PackageTemplateRenderer(root).Render(graph, node);
        var second = new PackageTemplateRenderer(root).Render(graph, node);
        Assert.Equal(kind, first.Kind.ToString());
        Assert.Equal(first.Files, second.Files);
        var snapshot = string.Join("", first.Files.Select(file => $"=== {file.RelativePath} ===\n{file.Content}"));
        var actualSnapshot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant();
        Assert.True(expectedSnapshot == actualSnapshot, $"Expected snapshot {expectedSnapshot}; actual {actualSnapshot}.");
        Assert.All(first.Files, file => { Assert.DoesNotContain("\r", file.Content); Assert.DoesNotContain("{{", file.Content); Assert.DoesNotContain(root, file.Content, StringComparison.OrdinalIgnoreCase); });
        Assert.Contains(first.Files, x => x.RelativePath == node.ProjectPath && x.Content.Contains("<SmartPipePackage>true</SmartPipePackage>", StringComparison.Ordinal));
        var project = first.Files.Single(x => x.RelativePath == node.ProjectPath).Content;
        Assert.Contains("<Import Project=\"$(SmartPipeRepositoryRoot)eng/SmartPipe.Package.props\" />", project);
        Assert.Contains("<SmartPipePackageBaselinePolicy>none</SmartPipePackageBaselinePolicy>", project);
        Assert.Contains("<SmartPipePackageReadmeSource>$(MSBuildProjectDirectory)/README.md</SmartPipePackageReadmeSource>", project);
        Assert.DoesNotContain("<None Include=\"README.md\"", project);
        var testProject = first.Files.Single(x => x.RelativePath.EndsWith(".Tests.csproj", StringComparison.Ordinal)).Content;
        Assert.Contains("xunit.v3.mtp-v2", testProject);
        Assert.Contains("<TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>", testProject);
        Assert.Contains("<UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>", testProject);
        Assert.Contains("<Using Include=\"Xunit\" />", testProject);
        Assert.DoesNotContain("EnableMSTestRunner", testProject);
    }

    [Fact]
    public async Task Render_FacadeIsNotScaffoldable()
    {
        var root = RepositoryRoot(); var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var error = Assert.Throws<ScaffoldException>(() => new PackageTemplateRenderer(root).Render(graph, graph.Packages.Single(x => x.Lifecycle == PackageLifecycle.CompatibilityFacade)));
        Assert.Equal("SPSCAF002", error.Code);
    }

    [Fact]
    public async Task GeneratedProject_MsBuildEvaluationImportsOfficialPackageContractWithoutDuplicateReadmeItems()
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var node = graph.Packages.Single(x => x.Id == "SmartPipe.Testing") with
        {
            Lifecycle = PackageLifecycle.Planned,
            ScaffoldKind = PackageScaffoldKind.Testing,
        };
        graph = graph with { Packages = graph.Packages.Select(item => item.Id == node.Id ? node : item).ToArray() };
        var plan = new PackageTemplateRenderer(root).Render(graph, node);
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Directory.Build.props", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        fixture.Write("Directory.Build.targets", File.ReadAllText(Path.Combine(root, "Directory.Build.targets")));
        fixture.Write("eng/SmartPipe.Versions.props", File.ReadAllText(Path.Combine(root, "eng/SmartPipe.Versions.props")));
        fixture.Write("eng/SmartPipe.Package.props", File.ReadAllText(Path.Combine(root, "eng/SmartPipe.Package.props")));
        fixture.Write("eng/SmartPipe.Package.targets", File.ReadAllText(Path.Combine(root, "eng/SmartPipe.Package.targets")));
        fixture.Write("assets/nuget/icon.png", "fixture");
        foreach (var file in plan.Files) fixture.Write(file.RelativePath, file.Content);

        var projectPath = Path.Combine(fixture.Path, plan.Files[0].RelativePath);
        var result = await new ProcessRunner().RunAsync(new("dotnet",
            ["msbuild", projectPath, "-getProperty:IsPackable,SmartPipePackage,PackageId,SmartPipePackageReadmeSource", "-getItem:None"],
            TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"IsPackable\": \"true\"", result.StandardOutput);
        Assert.Contains("\"SmartPipePackage\": \"true\"", result.StandardOutput);
        Assert.Contains("SmartPipe.Testing", result.StandardOutput);
        Assert.Equal(1, result.StandardOutput.Split("\"PackagePath\": \"README.md\"", StringSplitOptions.None).Length - 1);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
