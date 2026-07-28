using System.Text.Json;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.PackageGraph;

internal sealed record EvaluatedPackageReference(string Id, string? PrivateAssets, string? IncludeAssets, string? ExcludeAssets);
internal sealed record EvaluatedProject(
    string ProjectPath, string PackageId, string Version, string PackageVersion,
    IReadOnlyList<string> TargetFrameworks, bool SmartPipePackage, bool IsPackable,
    IReadOnlyList<EvaluatedPackageReference> PackageReferences, IReadOnlyList<string> ProjectReferences,
    string? BaselineVersion, bool IsAotCompatible, string? Readme, string? ReadmeSource, string? Icon);

internal interface IEvaluatedProjectReader
{
    Task<EvaluatedProject> ReadAsync(string projectPath, CancellationToken ct);
}

internal sealed class EvaluatedProjectReader(IProcessRunner? runner = null, string dotnet = "dotnet") : IEvaluatedProjectReader
{
    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<EvaluatedProject> ReadAsync(string projectPath, CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var result = await _runner.RunAsync(new ProcessRequest(dotnet,
        [
            "msbuild", fullPath, "-nologo",
            "-getProperty:PackageId,Version,PackageVersion,TargetFramework,TargetFrameworks,SmartPipePackage,IsPackable,PackageValidationBaselineVersion,IsAotCompatible,PackageReadmeFile,SmartPipePackageReadmeSource,PackageIcon",
            "-getItem:PackageReference,ProjectReference",
        ], Timeout), ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new PackageGraphException("SPGRAPH030", $"MSBuild evaluation failed for {Path.GetFileName(fullPath)}: {result.StandardError}");
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
            var root = document.RootElement;
            var properties = root.GetProperty("Properties");
            string Property(string name) => properties.GetProperty(name).GetString() ?? string.Empty;
            var items = root.GetProperty("Items");
            var packageReferences = ReadItems(items, "PackageReference").Select(item => new EvaluatedPackageReference(
                item.GetProperty("Identity").GetString()!, Metadata(item, "PrivateAssets"), Metadata(item, "IncludeAssets"), Metadata(item, "ExcludeAssets"))).ToArray();
            var projectReferences = ReadItems(items, "ProjectReference").Select(item =>
                Path.GetFullPath(item.TryGetProperty("FullPath", out var path) ? path.GetString()! : item.GetProperty("Identity").GetString()!, Path.GetDirectoryName(fullPath)!)).ToArray();
            var frameworks = Property("TargetFrameworks").Length > 0 ? Property("TargetFrameworks").Split(';') : [Property("TargetFramework")];
            return new(fullPath, Property("PackageId"), Property("Version"), Property("PackageVersion"), frameworks,
                IsTrue(Property("SmartPipePackage")), IsTrue(Property("IsPackable")), packageReferences, projectReferences,
                EmptyToNull(Property("PackageValidationBaselineVersion")), IsTrue(Property("IsAotCompatible")),
                EmptyToNull(Property("PackageReadmeFile")), EmptyToNull(Property("SmartPipePackageReadmeSource")), EmptyToNull(Property("PackageIcon")));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new PackageGraphException("SPGRAPH031", "MSBuild evaluation output is malformed.", exception);
        }
    }

    private static IEnumerable<JsonElement> ReadItems(JsonElement items, string name) =>
        items.TryGetProperty(name, out var value) ? value.EnumerateArray() : [];
    private static string? Metadata(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase);
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;
}
