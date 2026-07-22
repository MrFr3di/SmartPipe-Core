using System.Text.Json;
using System.Xml.Linq;

namespace SmartPipe.RepositoryChecks.Packaging;

internal sealed record NuGetAuditPolicyResult(IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

internal sealed class NuGetAuditPolicyValidator
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".agents", ".codebase-memory", ".codex", ".git", ".kilo", ".opencode", ".recovery", ".sonarqube", ".vscode",
        ".work", ".worktrees", "artifacts", "BenchmarkDotNet.Artifacts", "bin", "coverage", "Fixtures", "logs", "node_modules", "obj", "packages",
    };

    public NuGetAuditPolicyResult Verify(string repositoryRoot, string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        var root = Path.GetFullPath(repositoryRoot);
        var errors = new List<string>();
        ValidateReport(root, reportPath, errors);
        ValidateSuppressions(root, errors);
        return new(errors.OrderBy(static error => error, StringComparer.Ordinal).ToArray());
    }

    private static void ValidateReport(string root, string reportPath, List<string> errors)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != 1
                || !document.RootElement.TryGetProperty("projects", out var projects)
                || projects.ValueKind != JsonValueKind.Array)
            {
                errors.Add("SPAUD001 vulnerable package report must use dotnet package list JSON output version 1.");
                return;
            }

            foreach (var project in projects.EnumerateArray())
            {
                var projectPath = ReadString(project, "path");
                if (projectPath is null)
                {
                    errors.Add("SPAUD001 vulnerable package report project is missing path.");
                    continue;
                }

                var isProductionProject = IsProductionProject(root, projectPath);
                if (!project.TryGetProperty("frameworks", out var frameworks) || frameworks.ValueKind != JsonValueKind.Array)
                {
                    errors.Add($"SPAUD001 vulnerable package report project '{projectPath}' is missing frameworks.");
                    continue;
                }

                foreach (var framework in frameworks.EnumerateArray())
                {
                    ValidatePackages(projectPath, framework, "topLevelPackages", isProductionProject, errors);
                    ValidatePackages(projectPath, framework, "transitivePackages", false, errors);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            errors.Add($"SPAUD001 unable to read vulnerable package report: {exception.Message}");
        }
    }

    private static void ValidatePackages(
        string projectPath,
        JsonElement framework,
        string collectionName,
        bool rejectModerate,
        List<string> errors)
    {
        if (!framework.TryGetProperty(collectionName, out var packages) || packages.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var package in packages.EnumerateArray())
        {
            var packageId = ReadString(package, "id") ?? "<unknown>";
            if (!package.TryGetProperty("vulnerabilities", out var vulnerabilities) || vulnerabilities.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var vulnerability in vulnerabilities.EnumerateArray())
            {
                var severity = ReadString(vulnerability, "severity");
                if (severity is "High" or "Critical")
                {
                    errors.Add($"SPAUD002 {severity} vulnerability found in '{packageId}' for '{projectPath}'.");
                }
                else if (rejectModerate && severity == "Moderate")
                {
                    errors.Add($"SPAUD003 direct production dependency '{packageId}' has a moderate vulnerability in '{projectPath}'.");
                }
            }
        }
    }

    private static void ValidateSuppressions(string root, List<string> errors)
    {
        foreach (var path in EnumerateMsBuildFiles(root))
        {
            try
            {
                var document = XDocument.Load(path, LoadOptions.None);
                foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "NuGetAuditSuppress"))
                {
                    var include = (string?)item.Attribute("Include");
                    var rationale = (string?)item.Attribute("Rationale") ?? item.Elements().FirstOrDefault(element => element.Name.LocalName == "Rationale")?.Value;
                    var expiryIssue = (string?)item.Attribute("ExpiryIssue") ?? item.Elements().FirstOrDefault(element => element.Name.LocalName == "ExpiryIssue")?.Value;
                    if (!Uri.TryCreate(include, UriKind.Absolute, out var advisory)
                        || advisory.Scheme != Uri.UriSchemeHttps
                        || string.IsNullOrWhiteSpace(rationale)
                        || string.IsNullOrWhiteSpace(expiryIssue))
                    {
                        errors.Add($"SPAUD004 NuGetAuditSuppress in '{Path.GetRelativePath(root, path).Replace('\\', '/')}' requires an HTTPS advisory Include plus non-empty Rationale and ExpiryIssue.");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                errors.Add($"SPAUD005 unable to validate NuGetAuditSuppress entries in '{Path.GetRelativePath(root, path).Replace('\\', '/')}': {exception.Message}");
            }
        }
    }

    private static IEnumerable<string> EnumerateMsBuildFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (Path.GetExtension(path) is ".csproj" or ".props" or ".targets")
                {
                    yield return path;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!IgnoredDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsProductionProject(string root, string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath, root);
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return relative.StartsWith("src/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
