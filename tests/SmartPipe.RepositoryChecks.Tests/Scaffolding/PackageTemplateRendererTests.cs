using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Tests.Repository;
using System.Security.Cryptography;
using System.Text;

namespace SmartPipe.RepositoryChecks.Tests.Scaffolding;

public sealed class PackageTemplateRendererTests
{
    [Theory]
    [InlineData("SmartPipe.Extensions.Channels", "CoreLeaf", "1bc7c64f427265aa7849c734b3a8f9eceba03b6adb6d37d7e12cad08d0ad8a69")]
    [InlineData("SmartPipe.Extensions.Csv", "FrameworkIntegration", "177f4386223f16c8666b01f54ef34018f925f14704d85c04d2d812f065fa3fd5")]
    [InlineData("SmartPipe.Extensions.Http.Json", "ComposedIntegration", "4b28a61c212a2815d0b7830c846ba1b5e14daad2ffe1678ac528c482fae758d0")]
    [InlineData("SmartPipe.Extensions.Hosting", "HostIntegration", "a20a53fa2214012166fce26c0885b49a73a409dd871dcb0529dd40dc20e10e8d")]
    [InlineData("SmartPipe.Testing", "Testing", "7312db365c704bd43f5d0f2f8a364a5ca067a055a143be9ce1743f26283289e3")]
    public async Task Render_AllKindsAreDeterministicLfOnlySnapshots(string id, string kind, string expectedSnapshot)
    {
        var root = RepositoryRoot();
        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", TestContext.Current.CancellationToken);
        var node = graph.Packages.Single(x => x.Id == id);
        var first = new PackageTemplateRenderer(root).Render(graph, node);
        var second = new PackageTemplateRenderer(root).Render(graph, node);
        Assert.Equal(kind, first.Kind.ToString());
        Assert.Equal(first.Files, second.Files);
        var snapshot = string.Join("", first.Files.Select(file => $"=== {file.RelativePath} ===\n{file.Content}"));
        Assert.Equal(expectedSnapshot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot))).ToLowerInvariant());
        Assert.All(first.Files, file => { Assert.DoesNotContain("\r", file.Content); Assert.DoesNotContain("{{", file.Content); Assert.DoesNotContain(root, file.Content, StringComparison.OrdinalIgnoreCase); });
        Assert.Contains(first.Files, x => x.RelativePath == node.ProjectPath && x.Content.Contains("<SmartPipePackage>true</SmartPipePackage>", StringComparison.Ordinal));
        var project = first.Files.Single(x => x.RelativePath == node.ProjectPath).Content;
        Assert.Contains("<Import Project=\"$(SmartPipeRepositoryRoot)eng/SmartPipe.Package.props\" />", project);
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
        var plan = new PackageTemplateRenderer(root).Render(graph, graph.Packages.Single(x => x.Id == "SmartPipe.Extensions.Csv"));
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Directory.Build.props", File.ReadAllText(Path.Combine(root, "Directory.Build.props")));
        fixture.Write("Directory.Build.targets", File.ReadAllText(Path.Combine(root, "Directory.Build.targets")));
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
        Assert.Contains("SmartPipe.Extensions.Csv", result.StandardOutput);
        Assert.Equal(1, result.StandardOutput.Split("\"PackagePath\": \"README.md\"", StringSplitOptions.None).Length - 1);
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
