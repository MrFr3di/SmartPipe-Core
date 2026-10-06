using System.Xml.Linq;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

[Trait("Category", "PackageInfrastructure")]
public sealed class NuGetConfigurationTests
{
    [Fact]
    public void RootConfig_UsesHttpsV3SourcesAndExplicitAuditSourceWithoutCredentials()
    {
        var root = RepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "NuGet.Config"));
        var sources = document.Root!.Element("packageSources")!.Elements("add").ToArray();
        Assert.NotEmpty(sources);
        Assert.All(sources, source =>
        {
            var uri = new Uri((string)source.Attribute("value")!);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("3", (string?)source.Attribute("protocolVersion"));
            Assert.Empty(uri.UserInfo);
        });
        var audit = document.Root.Element("auditSources")!.Elements("add").Single();
        Assert.Equal("https", new Uri((string)audit.Attribute("value")!).Scheme);
        Assert.DoesNotContain("password", File.ReadAllText(Path.Combine(root, "NuGet.Config")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RootConfig_DoesNotUseWildcardSourceMappingOrHttpEndpoints()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "NuGet.Config"));
        Assert.DoesNotContain("http://", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<package pattern=\"*\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditPolicy_MakesHighCriticalErrorsAndLeavesModerateForDirectDependencyValidation()
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), "Directory.Build.props"));
        var properties = document.Root!.Element("PropertyGroup")!.Elements()
            .ToDictionary(element => element.Name.LocalName, element => element.Value.Trim(), StringComparer.Ordinal);

        Assert.Equal("true", properties["NuGetAudit"]);
        Assert.Equal("all", properties["NuGetAuditMode"]);
        Assert.Equal("moderate", properties["NuGetAuditLevel"]);
        Assert.Contains("NU1902", properties["WarningsNotAsErrors"].Split(';', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("NU1903", properties["WarningsAsErrors"].Split(';', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("NU1904", properties["WarningsAsErrors"].Split(';', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
}
