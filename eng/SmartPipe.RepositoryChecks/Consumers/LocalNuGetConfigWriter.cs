using System.Security;
using System.Text;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed class LocalNuGetConfigWriter
{
    public async Task<string> WriteAsync(
        string workspace,
        string packageDirectory,
        CancellationToken ct,
        string? containmentRoot = null,
        IEnumerable<string>? externalPackagePatterns = null)
    {
        var root = Path.GetFullPath(containmentRoot ?? workspace);
        var fullWorkspace = Path.GetFullPath(workspace);
        EnsureContained(root, fullWorkspace);
        var feed = Path.GetFullPath(packageDirectory).Replace('\\', '/');
        var externalPatterns = (externalPackagePatterns ?? []).
            Where(static pattern => !string.IsNullOrWhiteSpace(pattern) && !pattern.StartsWith("SmartPipe.", StringComparison.OrdinalIgnoreCase)).
            Select(static pattern => pattern.Trim()).
            Distinct(StringComparer.OrdinalIgnoreCase).
            Order(StringComparer.Ordinal).
            ToArray();
        if (externalPatterns.Length == 0)
            throw new ConsumerScenarioException("SPCONS021", "Consumer source mapping requires explicit external package patterns.");

        var externalMapping = string.Join(Environment.NewLine, externalPatterns.Select(pattern =>
            $"    <package pattern=\"{SecurityElement.Escape(pattern)}\" />"));
        var text = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="smartpipe-local" value="{{SecurityElement.Escape(feed)}}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="smartpipe-local"><package pattern="SmartPipe.*" /></packageSource>
                <packageSource key="nuget.org">
            {{externalMapping}}
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        var path = Path.Combine(fullWorkspace, "NuGet.Config");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return path;
    }

    private static void EnsureContained(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ConsumerScenarioException("SPCONS009", "Consumer output path escapes its isolated workspace.");
    }
}
