using System.Security;
using System.Text;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed class ConsumerCentralPackagesWriter
{
    public async Task<string> WriteAsync(
        string workspace,
        IReadOnlyList<string> packageIds,
        string version,
        IReadOnlyDictionary<string, string> externalPackageVersions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(externalPackageVersions);
        if (packageIds.Count == 0 || string.IsNullOrWhiteSpace(version)) throw new ConsumerScenarioException("SPCONS019", "Consumer CPM requires packages and version.");
        var versions = new Dictionary<string, string>(externalPackageVersions, StringComparer.OrdinalIgnoreCase);
        foreach (var packageId in packageIds)
        {
            versions[packageId] = version;
        }

        var entries = string.Join('\n', versions.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"    <PackageVersion Include=\"{SecurityElement.Escape(pair.Key)}\" Version=\"{SecurityElement.Escape(pair.Value)}\" />"));
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
