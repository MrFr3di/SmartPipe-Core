using System.Security;
using System.Text;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed class ConsumerCentralPackagesWriter
{
    public async Task<string> WriteAsync(string workspace, IReadOnlyList<string> packageIds, string version, CancellationToken ct)
    {
        if (packageIds.Count == 0 || string.IsNullOrWhiteSpace(version)) throw new ConsumerScenarioException("SPCONS019", "Consumer CPM requires packages and version.");
        var entries = string.Join('\n', packageIds.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .Select(id => $"    <PackageVersion Include=\"{SecurityElement.Escape(id)}\" Version=\"{SecurityElement.Escape(version)}\" />"));
        var text = $$"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
              </PropertyGroup>
              <ItemGroup>
            {{entries}}
              </ItemGroup>
            </Project>
            """.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        var path = Path.Combine(Path.GetFullPath(workspace), "Directory.Packages.props");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }
}
