using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Tests.Repository;

namespace SmartPipe.RepositoryChecks.Tests.Packaging;

[Trait("Category", "PackageInfrastructure")]
public sealed class NuGetAuditPolicyTests
{
    [Fact]
    public void ModerateVulnerabilityInDirectProductionDependency_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        WriteReport(fixture, "topLevelPackages", "Moderate");

        var result = new NuGetAuditPolicyValidator().Verify(fixture.Path, Path.Combine(fixture.Path, "artifacts/audit/vulnerable.json"));

        Assert.Contains(result.Errors, error => error.Contains("SPAUD003", StringComparison.Ordinal));
    }

    [Fact]
    public void ModerateVulnerabilityInTransitiveDependency_RemainsReviewable()
    {
        using var fixture = new RepositoryTestDirectory();
        WriteReport(fixture, "transitivePackages", "Moderate");

        var result = new NuGetAuditPolicyValidator().Verify(fixture.Path, Path.Combine(fixture.Path, "artifacts/audit/vulnerable.json"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void SuppressionWithoutRationaleAndExpiryIssue_IsRejected()
    {
        using var fixture = new RepositoryTestDirectory();
        fixture.Write("Directory.Build.props", """
            <Project><ItemGroup><NuGetAuditSuppress Include="https://github.com/advisories/GHSA-fixture" /></ItemGroup></Project>
            """);
        WriteReport(fixture, "topLevelPackages", null);

        var result = new NuGetAuditPolicyValidator().Verify(fixture.Path, Path.Combine(fixture.Path, "artifacts/audit/vulnerable.json"));

        Assert.Contains(result.Errors, error => error.Contains("SPAUD004", StringComparison.Ordinal));
    }

    private static void WriteReport(RepositoryTestDirectory fixture, string packageCollection, string? severity)
    {
        var packages = severity is null
            ? "[]"
            : $$"""[{ "id": "Fixture.Package", "vulnerabilities": [{ "severity": "{{severity}}" }] }]""";
        fixture.Write("artifacts/audit/vulnerable.json", $$"""
            {
              "version": 1,
              "projects": [
                {
                  "path": "src/Fixture/Fixture.csproj",
                  "frameworks": [
                    {
                      "framework": "net10.0",
                      "topLevelPackages": {{(packageCollection == "topLevelPackages" ? packages : "[]")}},
                      "transitivePackages": {{(packageCollection == "transitivePackages" ? packages : "[]")}}
                    }
                  ]
                }
              ]
            }
            """);
    }
}
