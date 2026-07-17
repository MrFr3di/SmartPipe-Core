using System.Text.Json;

namespace SmartPipe.RepositoryChecks.Baselines;

internal static class BaselineManifestValidator
{
    private const string BaselinePackageVersion = "2.1.2";

    private static readonly IReadOnlyDictionary<string, string> BaselinePackageIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SmartPipe.Core"] = "SmartPipe.Core",
            ["SmartPipe.Extensions"] = "SmartPipe.Extensions",
            ["SmartPipe.Extensions.Json"] = "SmartPipe.Extensions.Json",
        };

    public static void Validate(BaselineManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (manifest.SchemaVersion != 1)
        {
            throw Invalid($"Unsupported baseline schema version '{manifest.SchemaVersion}'.");
        }

        RequireText(manifest.BaselineName, "baselineName");
        RequireVersion(manifest.TargetRelease, "targetRelease");

        var repository = manifest.Repository ?? throw Invalid("repository is required.");
        ValidateRepository(repository);
        ValidatePackages(manifest.Packages);
        ValidateSnapshot(manifest.PublicApi, "publicApi");
        ValidateSnapshot(manifest.PackageAssets, "packageAssets");
        ValidateSnapshot(manifest.PackageDependencies, "packageDependencies");
        ValidateSnapshot(manifest.RepositoryDependencies, "repositoryDependencies");
    }

    private static void ValidateRepository(RepositoryBaseline repository)
    {
        RequireText(repository.FullName, "repository.fullName");
        RequireText(repository.DefaultBranch, "repository.defaultBranch");
        RequireLowercaseHex(repository.CaptureCommitSha, 40, "repository.captureCommitSha");
        RequireText(repository.SdkVersion, "repository.sdkVersion");
        RequireSafeRelativePath(repository.SolutionPath, "repository.solutionPath");

        var workflows = repository.RequiredWorkflows
            ?? throw Invalid("repository.requiredWorkflows is required.");
        if (workflows.Count == 0)
        {
            throw Invalid("repository.requiredWorkflows cannot be empty.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in workflows)
        {
            if (workflow is null)
            {
                throw Invalid("repository.requiredWorkflows cannot contain null entries.");
            }

            RequireText(workflow.Name, "workflow.name");
            if (!names.Add(workflow.Name))
            {
                throw Invalid($"Duplicate workflow identity '{workflow.Name}'.");
            }

            if (workflow.RunId <= 0)
            {
                throw Invalid("workflow.runId must be positive.");
            }

            RequireLowercaseHex(workflow.HeadSha, 40, "workflow.headSha");
            if (!string.Equals(workflow.HeadSha, repository.CaptureCommitSha, StringComparison.Ordinal))
            {
                throw Invalid("workflow.headSha must equal repository.captureCommitSha.");
            }

            RequireHttpsUri(workflow.Url, "workflow.url");
            if (!string.Equals(workflow.Conclusion, "success", StringComparison.Ordinal))
            {
                throw Invalid("workflow.conclusion must be 'success'.");
            }
        }
    }

    private static void ValidatePackages(IReadOnlyList<PackageBaseline>? packages)
    {
        if (packages is null || packages.Count != BaselinePackageIds.Count)
        {
            throw Invalid($"packages must contain exactly {BaselinePackageIds.Count} entries.");
        }

        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in packages)
        {
            if (package is null)
            {
                throw Invalid("packages cannot contain null entries.");
            }

            RequireText(package.Id, "package.id");
            if (!identities.Add(package.Id))
            {
                throw Invalid($"Duplicate package identity '{package.Id}'.");
            }

            if (!BaselinePackageIds.TryGetValue(package.Id, out var canonicalId)
                || !string.Equals(package.Id, canonicalId, StringComparison.Ordinal))
            {
                throw Invalid($"Package ID '{package.Id}' is not a canonical baseline package ID.");
            }

            if (!string.Equals(package.Version, BaselinePackageVersion, StringComparison.Ordinal))
            {
                throw Invalid($"Package '{package.Id}' must use baseline version '{BaselinePackageVersion}'.");
            }

            RequireHttpsUri(package.Source, $"package '{package.Id}' source");
            var expectedFileName = $"{package.Id}.{BaselinePackageVersion}.nupkg";
            if (!string.Equals(package.FileName, expectedFileName, StringComparison.Ordinal))
            {
                throw Invalid($"Package '{package.Id}' fileName must be '{expectedFileName}'.");
            }

            RequireLowercaseHex(package.Sha256, 64, $"package '{package.Id}' sha256");
            if (!package.RequireRepositorySignature)
            {
                throw Invalid($"Package '{package.Id}' must require a repository signature.");
            }
        }

        if (identities.Count != BaselinePackageIds.Count)
        {
            throw Invalid("packages do not contain the exact baseline package set.");
        }
    }

    private static void ValidateSnapshot(SnapshotReference? snapshot, string name)
    {
        if (snapshot is null)
        {
            throw Invalid($"{name} is required.");
        }

        RequireSafeRelativePath(snapshot.Path, $"{name}.path");
        RequireLowercaseHex(snapshot.Sha256, 64, $"{name}.sha256");
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Invalid($"{name} must be nonempty.");
        }
    }

    private static void RequireVersion(string? value, string name)
    {
        RequireText(value, name);
        var parts = value!.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            throw Invalid($"{name} must be a three-part numeric version.");
        }
    }

    private static void RequireLowercaseHex(string? value, int length, string name)
    {
        if (value is null || value.Length != length || value.Any(character =>
                !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw Invalid($"{name} must be {length} lowercase hexadecimal characters.");
        }
    }

    private static void RequireHttpsUri(Uri? value, string name)
    {
        if (value is null
            || !value.IsAbsoluteUri
            || !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !value.OriginalString.StartsWith("https://", StringComparison.Ordinal)
            || string.IsNullOrEmpty(value.Host))
        {
            throw Invalid($"{name} must be an absolute HTTPS URL.");
        }
    }

    private static void RequireSafeRelativePath(string? value, string name)
    {
        RequireText(value, name);
        if (value![0] == '/'
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid($"{name} must be a slash-normalized relative path.");
        }

        var segments = value.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw Invalid($"{name} must not contain empty, current, or parent path segments.");
        }
    }

    private static JsonException Invalid(string message) => new(message);
}
