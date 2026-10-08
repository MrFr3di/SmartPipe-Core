using System.Text.Json;
using System.Xml.Linq;

namespace SmartPipe.RepositoryChecks.Tests.PackageGraph;

public sealed class RepositoryDependencyAlignmentTests
{
    private static readonly string[] TelemetryPackageIds =
    [
        "OpenTelemetry",
        "OpenTelemetry.Api.ProviderBuilderExtensions",
        "OpenTelemetry.Exporter.InMemory",
        "OpenTelemetry.Exporter.OpenTelemetryProtocol",
        "OpenTelemetry.Extensions.Hosting",
    ];

    [Fact]
    public void OpenTelemetryFamily_UsesOneCentralVersionAcrossAllCurrentLocks()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var central = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var declarations = central.Descendants("PackageVersion")
            .Where(item => ((string?)item.Attribute("Include"))?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(
            TelemetryPackageIds.OrderBy(x => x, StringComparer.Ordinal),
            declarations.Select(item => (string)item.Attribute("Include")!).OrderBy(x => x, StringComparer.Ordinal));
        var expected = Assert.Single(declarations.Select(item => (string)item.Attribute("Version")!)
            .Distinct(StringComparer.Ordinal));

        var inspected = 0;
        foreach (var directory in new[] { "src", "benchmarks", "tests" })
        {
            var baseDirectory = Path.Combine(root, directory);
            foreach (var file in Directory.EnumerateFiles(baseDirectory, "packages.lock.json", SearchOption.AllDirectories))
            {
                // Consumer scenario snapshots intentionally exercise the published 2.2.0 baseline.
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.StartsWith("tests/Consumers/", StringComparison.Ordinal))
                    continue;

                using var document = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var target in document.RootElement.GetProperty("dependencies").EnumerateObject())
                {
                    foreach (var package in target.Value.EnumerateObject())
                    {
                        if (!package.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
                            continue;

                        var resolved = package.Value.GetProperty("resolved").GetString();
                        Assert.True(
                            string.Equals(resolved, expected, StringComparison.Ordinal),
                            $"Locked OpenTelemetry version mismatch in {relative}: {package.Name}={resolved}; central={expected}.");
                        inspected++;
                    }
                }
            }
        }

        Assert.True(inspected > 0, "At least one OpenTelemetry lockfile entry must be checked.");
    }

    [Fact]
    public void CodeCoverage_LockedDependencyMatchesCentralDeclaration()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var central = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        var expected = Assert.Single(central.Descendants("PackageVersion")
            .Where(item => (string?)item.Attribute("Include") == "Microsoft.Testing.Extensions.CodeCoverage"))
            .Attribute("Version")!.Value;

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "tests/SmartPipe.Core.Tests/packages.lock.json")));
        var coverage = document.RootElement.GetProperty("dependencies").GetProperty("net10.0")
            .GetProperty("Microsoft.Testing.Extensions.CodeCoverage");
        Assert.Equal(expected, coverage.GetProperty("resolved").GetString());
        Assert.Equal($"[{expected}, )", coverage.GetProperty("requested").GetString());

        // CodeCoverage 18.12.0 uses the public Mono.Cecil assembly instead of the
        // Microsoft.DotNet.Cecil fork: loading both in one process is unsupported.
        var graph = document.RootElement.GetProperty("dependencies").GetProperty("net10.0");
        Assert.Equal("0.11.6", coverage.GetProperty("dependencies").GetProperty("Mono.Cecil").GetString());
        Assert.True(graph.TryGetProperty("Mono.Cecil", out _));
        Assert.False(graph.TryGetProperty("Microsoft.DotNet.Cecil", out _));
        Assert.Equal("2.5.0", graph.GetProperty("Microsoft.Testing.Platform").GetProperty("resolved").GetString());
    }
}
