using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class PackPackagesCommand(IProcessRunner? processRunner = null)
{
    private readonly IProcessRunner _processRunner = processRunner ?? new ProcessRunner();

    public async Task<PackagePackManifest> ExecuteAsync(PackPackagesOptions options, CancellationToken ct)
    {
        var root = Path.GetFullPath(options.RepositoryRoot);
        var output = EnsureContained(root, options.OutputDirectory, "output");
        var manifestPath = EnsureContained(root, options.ManifestPath, "manifest");
        if (!Path.GetDirectoryName(manifestPath)!.Equals(output, PathComparison())) throw new PackagePackException("SPPACK001", "Pack manifest must be directly inside the package output directory.");
        if (File.Exists(manifestPath)) throw new PackagePackException("SPPACK002", "Refusing to overwrite an existing package manifest.");
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFiles(output, "*.nupkg").Any() || Directory.EnumerateFiles(output, "*.snupkg").Any()) throw new PackagePackException("SPPACK002", "Package output must not contain existing package artifacts.");

        var graph = await new PackageGraphLoader().LoadAsync(root, "eng/package-graph.json", ct).ConfigureAwait(false);
        if (options.Mode == PackageGraphMode.Release && graph.Packages.Any(x => x.Lifecycle == PackageLifecycle.Planned))
            throw new PackagePackException("SPPACK003", "Release packing requires every planned package to be activated.");
        var nodes = graph.Packages.Where(x => x.Lifecycle != PackageLifecycle.Planned).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var dependencies = nodes.Values.ToDictionary(x => x.Id, x => (options.Mode == PackageGraphMode.Current ? x.CurrentDependencies : x.ReleaseDependencies).RequiredSmartPipePackages.Where(nodes.ContainsKey).ToArray() as IReadOnlyList<string>, StringComparer.OrdinalIgnoreCase);
        var order = TopologicalPackageSorter.Sort(dependencies);
        var artifacts = new List<PackagePackArtifact>();
        var logs = Path.Combine(output, "logs"); Directory.CreateDirectory(logs);
        foreach (var id in order)
        {
            var node = nodes[id];
            var request = new ProcessRequest("dotnet",
            [
                "pack", Path.GetFullPath(node.ProjectPath, root), "--configuration", options.Configuration,
                "--no-build", "--no-restore", $"-p:PackageVersion={options.PackageVersion}",
                "-p:EnablePackageValidation=true", "--output", output,
            ], TimeSpan.FromMinutes(10), root, logs);
            var result = await _processRunner.RunAsync(request, ct).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new PackagePackException("SPPACK004", $"Packing {id} failed with exit code {result.ExitCode}: {result.StandardError}");
            var nupkg = Path.Combine(output, $"{id}.{options.PackageVersion}.nupkg");
            var snupkg = Path.Combine(output, $"{id}.{options.PackageVersion}.snupkg");
            if (!File.Exists(nupkg) || !File.Exists(snupkg)) throw new PackagePackException("SPPACK005", $"Packing {id} did not produce both nupkg and snupkg.");
            artifacts.Add(new()
            {
                Id = id,
                Version = options.PackageVersion,
                NupkgPath = Normalize(Path.GetRelativePath(root, nupkg)),
                NupkgSha256 = await Hashing.Sha256FileAsync(nupkg, ct).ConfigureAwait(false),
                SnupkgPath = Normalize(Path.GetRelativePath(root, snupkg)),
                SnupkgSha256 = await Hashing.Sha256FileAsync(snupkg, ct).ConfigureAwait(false),
                PublishOrder = node.PublishOrder,
            });
        }
        var manifest = new PackagePackManifest { SchemaVersion = 1, Mode = options.Mode.ToString().ToLowerInvariant(), Version = options.PackageVersion, Packages = artifacts };
        var json = JsonSerializer.Serialize(manifest, RepositoryChecksJsonContext.Default.PackagePackManifest).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        await File.WriteAllTextAsync(manifestPath, json, new UTF8Encoding(false), ct).ConfigureAwait(false);
        return manifest;
    }

    private static string EnsureContained(string root, string path, string name)
    {
        var full = Path.GetFullPath(path, root); var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new PackagePackException("SPPACK001", $"Pack {name} path escapes repository root.");
        return full;
    }
    private static string Normalize(string path) => path.Replace('\\', '/');
    private static StringComparison PathComparison() => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
