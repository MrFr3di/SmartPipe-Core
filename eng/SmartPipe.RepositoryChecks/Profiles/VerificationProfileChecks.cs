using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Reporting;

namespace SmartPipe.RepositoryChecks.Profiles;

internal static class VerificationProfileChecks
{
    public static IReadOnlyDictionary<string, Func<CancellationToken, Task<CheckRun>>> Create(string repositoryRoot) =>
        VerificationProfileManifestLoader.SupportedCheckIds.ToDictionary(
            checkId => checkId,
            checkId => new Func<CancellationToken, Task<CheckRun>>(ct => RunAsync(checkId, repositoryRoot, ct)),
            StringComparer.Ordinal);

    private static async Task<CheckRun> RunAsync(string checkId, string repositoryRoot, CancellationToken cancellationToken) =>
        checkId switch
        {
            "verify-package-projects" => await VerifyPackageProjectsAsync(repositoryRoot, cancellationToken).ConfigureAwait(false),
            "verify-central-packages-current" => await VerifyCentralPackagesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false),
            "verify-package-graph-current-source" => await VerifyPackageGraphAsync(repositoryRoot, cancellationToken).ConfigureAwait(false),
            "verify-lock-files" => await VerifyLockFilesAsync(repositoryRoot, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unsupported profile check '{checkId}'."),
        };

    private static async Task<CheckRun> VerifyPackageProjectsAsync(string root, CancellationToken cancellationToken)
    {
        var result = await new OfficialPackageProjectVerifier().VerifyAsync(root, cancellationToken).ConfigureAwait(false);
        return new(
            "verify-package-projects", null, result.Success, result.Success ? ExitCodes.Success : ExitCodes.PackageProjectViolation,
            result.Errors.Select(error => new CheckDiagnostic(error.Code, Compact(error.Message), error.Path)).ToArray(),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["violations"] = result.Errors.Count });
    }

    private static async Task<CheckRun> VerifyCentralPackagesAsync(string root, CancellationToken cancellationToken)
    {
        var result = await new CentralPackageVersionReader().VerifyAsync(root, CentralPackageValidationMode.Current, cancellationToken).ConfigureAwait(false);
        return new(
            "verify-central-packages-current", null, result.Success, result.Success ? ExitCodes.Success : ExitCodes.CentralPackagePolicyViolation,
            result.Errors.Select(error => new CheckDiagnostic(error.Code, Compact(error.Message), error.Path)).ToArray(),
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["versions"] = result.Versions.Count,
                ["warnings"] = result.Warnings.Count,
                ["violations"] = result.Errors.Count,
            });
    }

    private static async Task<CheckRun> VerifyPackageGraphAsync(string root, CancellationToken cancellationToken)
    {
        var result = await new VerifyPackageGraphCommand().ExecuteAsync(
            new VerifyPackageGraphOptions(root, Path.Combine(root, "eng", "package-graph.json"), PackageGraphMode.Current, null, true),
            cancellationToken).ConfigureAwait(false);
        return new(
            "verify-package-graph-current-source", null, result.Success, result.Success ? ExitCodes.Success : ExitCodes.PackageProjectViolation,
            result.Violations.Select(violation => new CheckDiagnostic(
                violation.Code,
                Compact($"package={violation.PackageId} representation={violation.Representation} dependency={FormatDependency(root, violation.Dependency)} rule={violation.Rule}"),
                RelativePath(root, violation.Dependency))).ToArray(),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["violations"] = result.Violations.Count });
    }

    private static async Task<CheckRun> VerifyLockFilesAsync(string root, CancellationToken cancellationToken)
    {
        var result = await new VerifyLockFilesCommand().ExecuteAsync(root, cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Errors.Select(ParseLockDiagnostic).ToArray();
        return new(
            "verify-lock-files", null, result.Success, result.Success ? ExitCodes.Success : ExitCodes.CentralPackagePolicyViolation,
            diagnostics,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["violations"] = diagnostics.Length });
    }

    private static CheckDiagnostic ParseLockDiagnostic(string error)
    {
        var first = error.IndexOf(':');
        var second = first < 0 ? -1 : error.IndexOf(':', first + 1);
        if (first <= 0 || second <= first)
        {
            return new("SPLOCK000", "Lock-file verification failed.");
        }

        return new(error[..first], Compact(error[(second + 1)..]), error[(first + 1)..second]);
    }

    private static string FormatDependency(string root, string? dependency)
    {
        if (dependency is null)
        {
            return "-";
        }

        var relative = RelativePath(root, dependency);
        return relative ?? (Path.IsPathRooted(dependency) ? "[outside-repository]" : dependency.Replace('\r', ' ').Replace('\n', ' '));
    }

    private static string Compact(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 1_024 ? compact : compact[..1_021] + "...";
    }

    private static string? RelativePath(string root, string? path)
    {
        if (path is null || !Path.IsPathRooted(path))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
