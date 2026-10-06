using System.Security;
using SmartPipe.RepositoryChecks.PackageGraph;

namespace SmartPipe.RepositoryChecks.Scaffolding;

internal sealed class PackageTemplateRenderer
{
    private readonly string _repositoryRoot;
    private readonly string _templateRoot;

    public PackageTemplateRenderer(string repositoryRoot)
    {
        _repositoryRoot = Path.GetFullPath(repositoryRoot);
        _templateRoot = Path.Combine(_repositoryRoot, "eng", "templates", "package");
    }

    public PackageScaffoldPlan Render(PackageGraphDocument graph, PackageNode node)
    {
        if (node.Lifecycle != PackageLifecycle.Planned || node.ScaffoldKind is null)
            throw new ScaffoldException("SPSCAF002", $"Package '{node.Id}' is not a scaffoldable planned package.");

        var projectDirectory = Path.GetDirectoryName(node.ProjectPath)!.Replace('\\', '/');
        var testDirectory = $"tests/{node.Id}.Tests";
        var testProject = $"{testDirectory}/{node.Id}.Tests.csproj";
        var description = $"SmartPipe {KindName(node.ScaffoldKind.Value)} package for {node.Id}.";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PACKAGE_ID"] = Escape(node.Id),
            ["DESCRIPTION"] = Escape(description),
            ["TAGS"] = Escape("SmartPipe;pipeline;" + KindToken(node.ScaffoldKind.Value)),
            ["SCAFFOLD_KIND"] = KindToken(node.ScaffoldKind.Value),
            ["AOT_PROPERTIES"] = RenderAotProperties(node.AotContract),
            ["PROJECT_REFERENCES"] = RenderProjectReferences(graph, node),
            ["PACKAGE_REFERENCES"] = RenderPackageReferences(node),
            ["PROJECT_REFERENCE"] = Normalize(Path.GetRelativePath(testDirectory, node.ProjectPath)),
        };

        var files = new List<ScaffoldFile>
        {
            new(node.ProjectPath, RenderTemplate("Package.csproj.tmpl", values)),
            new($"{projectDirectory}/README.md", RenderTemplate("README.md.tmpl", values)),
            new($"{projectDirectory}/PublicAPI.Shipped.txt", ReadTemplate("PublicAPI.Shipped.txt")),
            new($"{projectDirectory}/PublicAPI.Unshipped.txt", ReadTemplate("PublicAPI.Unshipped.txt")),
            new(testProject, RenderTemplate("Package.Tests.csproj.tmpl", values)),
        };

        return new(node.Id, node.ScaffoldKind.Value, files,
        [
            $"dotnet sln SmartPipe.Core.slnx add {node.ProjectPath}",
            $"dotnet sln SmartPipe.Core.slnx add {testProject}",
            $"Change {node.Id} lifecycle in eng/package-graph.json only when {node.ActivationEpic} acceptance gates pass.",
        ]);
    }

    private string RenderProjectReferences(PackageGraphDocument graph, PackageNode node)
    {
        if (node.ReleaseDependencies.RequiredSmartPipePackages.Count == 0) return string.Empty;
        var directory = Path.GetDirectoryName(node.ProjectPath)!;
        var entries = node.ReleaseDependencies.RequiredSmartPipePackages.Select(id =>
        {
            var dependency = graph.Packages.Single(x => x.Id.Equals(id, StringComparison.Ordinal));
            return $"    <ProjectReference Include=\"{Escape(Normalize(Path.GetRelativePath(directory, dependency.ProjectPath)))}\" />";
        });
        return "  <ItemGroup>\n" + string.Join('\n', entries) + "\n  </ItemGroup>\n\n";
    }

    private static string RenderPackageReferences(PackageNode node)
    {
        if (node.ReleaseDependencies.AllowedExternalPackages.Count == 0) return string.Empty;
        var entries = node.ReleaseDependencies.AllowedExternalPackages.Select(id => $"    <PackageReference Include=\"{Escape(id)}\" />");
        return "  <ItemGroup>\n" + string.Join('\n', entries) + "\n  </ItemGroup>\n\n";
    }

    private static string RenderAotProperties(PackageAotContract contract) => contract switch
    {
        PackageAotContract.UnsupportedBlanket or PackageAotContract.NotRuntime or PackageAotContract.NoBlanket => string.Empty,
        _ => "    <IsAotCompatible>true</IsAotCompatible>\n    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>\n    <EnableAotAnalyzer>true</EnableAotAnalyzer>\n    <VerifyReferenceAotCompatibility>true</VerifyReferenceAotCompatibility>\n",
    };

    private string RenderTemplate(string name, IReadOnlyDictionary<string, string> values)
    {
        var text = ReadTemplate(name);
        foreach (var (token, value) in values) text = text.Replace("{{" + token + "}}", value, StringComparison.Ordinal);
        if (text.Contains("{{", StringComparison.Ordinal)) throw new ScaffoldException("SPSCAF005", $"Template '{name}' contains an unresolved token.");
        return NormalizeText(text);
    }

    private string ReadTemplate(string name) => NormalizeText(File.ReadAllText(Path.Combine(_templateRoot, name)));
    private static string NormalizeText(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n') + "\n";
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
    private static string KindToken(PackageScaffoldKind kind) => kind switch
    {
        PackageScaffoldKind.CoreLeaf => "core-leaf",
        PackageScaffoldKind.FrameworkIntegration => "framework-integration",
        PackageScaffoldKind.ComposedIntegration => "composed-integration",
        PackageScaffoldKind.HostIntegration => "host-integration",
        PackageScaffoldKind.Testing => "testing",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
    private static string KindName(PackageScaffoldKind kind) => KindToken(kind).Replace('-', ' ');
}
