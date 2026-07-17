using System.Text;
using System.Text.Json;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed record BaselineDiagnostic(string Code, string Message, string? Expected = null, string? Actual = null);

internal sealed record BaselineVerificationResult(IReadOnlyList<BaselineDiagnostic> Diagnostics)
{
    public bool Success => Diagnostics.Count == 0;

    public string Format()
    {
        if (Success)
        {
            return "BASELINE VERIFICATION PASSED";
        }

        var builder = new StringBuilder("BASELINE VERIFICATION FAILED\n");
        foreach (var diagnostic in Diagnostics)
        {
            builder.Append('[').Append(diagnostic.Code).Append("] ").Append(diagnostic.Message).Append('\n');
            if (diagnostic.Expected is not null)
            {
                builder.Append("  expected: ").Append(diagnostic.Expected).Append('\n');
            }

            if (diagnostic.Actual is not null)
            {
                builder.Append("  actual:   ").Append(diagnostic.Actual).Append('\n');
            }
        }

        return builder.ToString().TrimEnd();
    }
}

internal sealed class BaselineVerificationService
{
    private const string RepositoryName = "MrFr3di/SmartPipe-Core";
    private const string TargetRelease = "2.2.0";
    private const string SolutionPath = "SmartPipe.Core.slnx";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly (string Name, string Path)[] RequiredWorkflowFiles =
    [
        ("CI", ".github/workflows/ci.yml"),
        ("CodeQL", ".github/workflows/codeql.yml"),
        ("Dependency Review", ".github/workflows/dependency-review.yml"),
    ];

    private readonly IProcessRunner _processRunner;
    private readonly string _gitPath;
    private readonly INuGetPackageSignatureVerifier _signatureVerifier;
    private readonly NuGetPackageReader _packageReader;
    private readonly BaselineRepositorySnapshotReader _repositoryReader;

    public BaselineVerificationService(
        IProcessRunner processRunner,
        string gitPath,
        INuGetPackageSignatureVerifier signatureVerifier,
        NuGetPackageReader packageReader,
        BaselineRepositorySnapshotReader repositoryReader)
    {
        _processRunner = processRunner;
        _gitPath = gitPath;
        _signatureVerifier = signatureVerifier;
        _packageReader = packageReader;
        _repositoryReader = repositoryReader;
    }

    public Task<BaselineVerificationResult> VerifyAsync(
        VerifyBaselineOptions options,
        CancellationToken cancellationToken) =>
        VerifyAsync(options, snapshotRootOverride: null, cancellationToken);

    internal async Task<BaselineVerificationResult> VerifyAsync(
        VerifyBaselineOptions options,
        string? snapshotRootOverride,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<BaselineDiagnostic>();
        BaselineManifest manifest;
        try
        {
            var root = RepositoryPaths.NormalizeRoot(options.RepositoryRoot);
            _ = RepositoryPaths.NormalizeContainedFullPath(
                root, Path.GetFullPath(options.PackagesDirectory), "packages directory");
            var manifestPath = ResolveInputWithinRoot(root, options.ManifestPath, "manifest");
            RepositoryPaths.RequireExistingRegularFile(root, manifestPath, "manifest");
            manifest = BaselineManifestSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(manifest.TargetRelease, TargetRelease, StringComparison.Ordinal)
                || !string.Equals(manifest.BaselineName, "smartpipe-core-2.1.2", StringComparison.Ordinal))
            {
                throw new JsonException("Manifest must describe the fixed 2.1.2 to 2.2.0 baseline.");
            }

            var workflowNames = manifest.Repository.RequiredWorkflows
                .Select(static workflow => workflow.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (RequiredWorkflowFiles.Any(workflow => !workflowNames.Contains(workflow.Name)))
            {
                throw new JsonException("Manifest workflow evidence must include CI, CodeQL, and Dependency Review.");
            }

            // Resolve every referenced path before any package, process, or repository work.
            _ = ResolveSnapshot(root, manifest.PublicApi.Path, snapshotRootOverride);
            _ = ResolveSnapshot(root, manifest.PackageAssets.Path, snapshotRootOverride);
            _ = ResolveSnapshot(root, manifest.PackageDependencies.Path, snapshotRootOverride);
            _ = ResolveSnapshot(root, manifest.RepositoryDependencies.Path, snapshotRootOverride);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result([new BaselineDiagnostic("SPB001", $"Unsafe or invalid baseline manifest: {exception.Message}")]);
        }

        if (!string.Equals(manifest.Repository.FullName, RepositoryName, StringComparison.Ordinal))
        {
            diagnostics.Add(new("SPB002", "Repository full name mismatch", RepositoryName, manifest.Repository.FullName));
        }

        var actualCommit = await RunOutputAsync(
            _gitPath, ["-C", options.RepositoryRoot, "rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualCommit, manifest.Repository.CommitSha, StringComparison.Ordinal))
        {
            diagnostics.Add(new("SPB003", "Repository commit mismatch", manifest.Repository.CommitSha, actualCommit));
        }

        var actualSdk = ReadSdkVersion(options.RepositoryRoot);
        if (!string.Equals(actualSdk, manifest.Repository.SdkVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new("SPB004", "global.json SDK mismatch", manifest.Repository.SdkVersion, actualSdk));
        }

        var snapshotFiles = new[]
        {
            (manifest.PublicApi, "public API"),
            (manifest.PackageAssets, "package assets"),
            (manifest.PackageDependencies, "package dependencies"),
            (manifest.RepositoryDependencies, "repository dependencies"),
        };
        var snapshotBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (reference, description) in snapshotFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveSnapshot(options.RepositoryRoot, reference.Path, snapshotRootOverride);
            if (!File.Exists(path))
            {
                diagnostics.Add(new("SPB005", $"Required {description} snapshot is missing: {reference.Path}"));
                continue;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            snapshotBytes[reference.Path] = bytes;
            var hash = Hashing.Sha256Hex(bytes);
            if (!string.Equals(hash, reference.Sha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new("SPB006", $"Snapshot hash mismatch: {reference.Path}", reference.Sha256, hash));
            }
        }

        var packageSnapshots = new List<NuGetPackageSnapshot>();
        foreach (var package in manifest.Packages.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var packagePath = Path.Combine(options.PackagesDirectory, package.FileName);
            if (!File.Exists(packagePath))
            {
                diagnostics.Add(new("SPB005", $"Required package is missing: {package.FileName}"));
                continue;
            }

            var hash = await Hashing.Sha256FileAsync(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, package.Sha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new("SPB007", $"Package hash mismatch: {package.Id}", package.Sha256, hash));
                continue;
            }

            try
            {
                await _signatureVerifier.VerifyAsync(packagePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new("SPB008", $"Package signature verification failed: {package.Id}: {exception.Message}"));
                continue;
            }

            try
            {
                packageSnapshots.Add(await _packageReader.ReadAsync(
                    packagePath, package.Id, package.Version, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new("SPB009", $"Package identity or archive snapshot failed: {package.Id}: {exception.Message}"));
            }
        }

        if (packageSnapshots.Count == manifest.Packages.Count)
        {
            CompareSnapshot(snapshotBytes, manifest.PackageAssets, BaselineSnapshotJson.Serialize(
                packageSnapshots.OrderBy(static item => item.Id, StringComparer.Ordinal).Select(static item => item.Assets).ToArray()),
                "SPB009", "Package asset snapshot mismatch", diagnostics);
            CompareSnapshot(snapshotBytes, manifest.PackageDependencies, BaselineSnapshotJson.Serialize(
                packageSnapshots.OrderBy(static item => item.Id, StringComparer.Ordinal).Select(static item => item.Dependencies).ToArray()),
                "SPB010", "Package dependency snapshot mismatch", diagnostics);
        }

        try
        {
            var current = await _repositoryReader.ReadAsync(
                options.RepositoryRoot, manifest.Repository.SolutionPath, cancellationToken).ConfigureAwait(false);
            CompareSnapshot(snapshotBytes, manifest.PublicApi, current.PublicApi,
                "SPB014", "Public API snapshot mismatch", diagnostics);
            CompareSnapshot(snapshotBytes, manifest.RepositoryDependencies, current.RepositoryDependencies,
                "SPB015", "Repository dependency snapshot mismatch", diagnostics);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new("SPB015", $"Repository snapshot could not be read: {exception.Message}"));
        }

        var releaseBranch = $"release/{manifest.TargetRelease}";
        foreach (var workflow in RequiredWorkflowFiles)
        {
            var path = RepositoryPaths.ResolveWithinRoot(options.RepositoryRoot, workflow.Path, "workflow");
            if (!File.Exists(path)
                || !File.ReadAllText(path).Contains(releaseBranch, StringComparison.Ordinal))
            {
                diagnostics.Add(new("SPB016", $"Workflow release branch policy mismatch: {workflow.Name}", releaseBranch, workflow.Path));
            }
        }

        return Result(diagnostics);
    }

    private static BaselineVerificationResult Result(IEnumerable<BaselineDiagnostic> diagnostics) => new(
        diagnostics.OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ThenBy(static item => item.Message, StringComparer.Ordinal)
            .ToArray());

    private static void CompareSnapshot(
        IReadOnlyDictionary<string, byte[]> expectedFiles,
        SnapshotReference reference,
        byte[] actual,
        string code,
        string message,
        ICollection<BaselineDiagnostic> diagnostics)
    {
        if (expectedFiles.TryGetValue(reference.Path, out var expected) && !expected.AsSpan().SequenceEqual(actual))
        {
            diagnostics.Add(new(code, message, reference.Path, Hashing.Sha256Hex(actual)));
        }
    }

    private static string ResolveSnapshot(string root, string manifestPath, string? snapshotRootOverride)
    {
        var safe = RepositoryPaths.ResolveWithinRoot(root, manifestPath, "snapshot");
        return snapshotRootOverride is null ? safe : Path.Combine(snapshotRootOverride, Path.GetFileName(safe));
    }

    private static string ResolveInputWithinRoot(string root, string path, string description) =>
        Path.IsPathRooted(path)
            ? Path.Combine(root, RepositoryPaths.NormalizeContainedFullPath(root, Path.GetFullPath(path), description).Replace('/', Path.DirectorySeparatorChar))
            : RepositoryPaths.ResolveWithinRoot(root, path, description);

    internal static string ReadSdkVersion(string repositoryRoot)
    {
        var path = RepositoryPaths.ResolveWithinRoot(repositoryRoot, "global.json", "global.json");
        RepositoryPaths.RequireExistingRegularFile(repositoryRoot, path, "global.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json sdk.version is missing.");
    }

    internal async Task<string> RunOutputAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRequest(executable, arguments, ProcessTimeout), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable,
                $"Process failed: {executable} {string.Join(' ', arguments)}");
        }

        return result.StandardOutput.Trim();
    }
}
