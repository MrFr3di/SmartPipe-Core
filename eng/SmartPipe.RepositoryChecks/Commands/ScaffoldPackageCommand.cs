using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Serialization;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class ScaffoldPackageCommand
{
    public async Task<ScaffoldReport> ExecuteAsync(ScaffoldPackageOptions options, CancellationToken ct)
    {
        var graph = await new PackageGraphLoader().LoadAsync(options.RepositoryRoot, "eng/package-graph.json", ct).ConfigureAwait(false);
        var node = graph.Packages.SingleOrDefault(x => x.Id.Equals(options.PackageId, StringComparison.Ordinal));
        if (node is null) throw new ScaffoldException("SPSCAF001", $"Package '{options.PackageId}' is not in the package graph.");
        var plan = new PackageTemplateRenderer(options.RepositoryRoot).Render(graph, node);
        if (!options.DryRun) await new AtomicFileWriter().WriteAsync(options.RepositoryRoot, plan.Files, ct).ConfigureAwait(false);
        var report = new ScaffoldReport(true, plan.PackageId, plan.Kind, options.DryRun,
            plan.Files.Select(x => x.RelativePath).ToArray(), plan.RequiredSteps);
        if (options.OutputReport is not null) await WriteReportAsync(options.RepositoryRoot, options.OutputReport, report, ct).ConfigureAwait(false);
        return report;
    }

    private static async Task WriteReportAsync(string root, string reportPath, ScaffoldReport report, CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(reportPath, fullRoot);
        var relative = Path.GetRelativePath(fullRoot, fullPath).Replace('\\', '/');
        var json = JsonSerializer.Serialize(report, RepositoryChecksJsonContext.Default.ScaffoldReport).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";
        await new AtomicFileWriter().WriteAsync(fullRoot, [new(relative, json)], ct).ConfigureAwait(false);
    }
}
