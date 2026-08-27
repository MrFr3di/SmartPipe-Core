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
    private static readonly string[] ManifestWorkflowNames =
    [
        "CI",
        "CodeQL",
        "Dependency Review",
    ];

    private static readonly (string Name, string Path, string[] Events)[] CurrentWorkflowPolicy =
    [
        ("CI", ".github/workflows/ci.yml", ["push", "pull_request"]),
        ("CodeQL", ".github/workflows/codeql.yml", ["push", "pull_request"]),
        ("Dependency Review", ".github/workflows/dependency-review.yml", ["pull_request"]),
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
        if (!options.Offline)
        {
            throw new InvalidOperationException("Baseline verification must be offline.");
        }

        var diagnostics = new List<BaselineDiagnostic>();
        var missingPackageFileNames = new HashSet<string>(StringComparer.Ordinal);
        BaselineManifest manifest;
        string baselineRoot;
        string packagesDirectory;
        try
        {
            var root = RepositoryPaths.NormalizeRoot(options.RepositoryRoot);
            packagesDirectory = Path.IsPathRooted(options.PackagesDirectory)
                ? Path.GetFullPath(options.PackagesDirectory)
                : Path.GetFullPath(options.PackagesDirectory.Replace('/', Path.DirectorySeparatorChar), root);
            _ = RepositoryPaths.NormalizeContainedFullPath(root, packagesDirectory, "packages directory");
            var manifestPath = ResolveInputWithinRoot(root, options.ManifestPath, "manifest");
            baselineRoot = snapshotRootOverride ?? Path.GetDirectoryName(manifestPath)!;
            RepositoryPaths.RequireExistingRegularFile(root, manifestPath, "manifest");
            manifest = BaselineManifestSerializer.Deserialize(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(manifest.TargetRelease, TargetRelease, StringComparison.Ordinal)
                || !string.Equals(manifest.BaselineName, "smartpipe-core-2.1.2", StringComparison.Ordinal))
            {
                throw new JsonException("Manifest must describe the fixed 2.1.2 to 2.2.0 baseline.");
            }

            if (!string.Equals(manifest.Repository.DefaultBranch, "main", StringComparison.Ordinal)
                || !string.Equals(manifest.Repository.SolutionPath, SolutionPath, StringComparison.Ordinal))
            {
                throw new JsonException("Manifest must use defaultBranch 'main' and solutionPath 'SmartPipe.Core.slnx'.");
            }

            var workflowNames = manifest.Repository.RequiredWorkflows
                .Select(static workflow => workflow.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (!workflowNames.SetEquals(ManifestWorkflowNames))
            {
                throw new JsonException(
                    "Manifest workflow evidence must contain exactly CI, CodeQL, and Dependency Review.");
            }

            // Resolve and de-alias every referenced path before any package, process, or repository work.
            var uniqueSnapshotPaths = new HashSet<string>(RepositoryPaths.FileSystemPathComparer);
            foreach (var snapshot in new[]
                     {
                         manifest.PublicApi, manifest.PackageAssets,
                         manifest.PackageDependencies, manifest.RepositoryDependencies,
                     })
            {
                var normalized = RepositoryPaths.ResolveWithinRoot(root, snapshot.Path, "snapshot");
                if (!uniqueSnapshotPaths.Add(normalized))
                {
                    throw new JsonException("Manifest snapshot paths must be unique after normalization.");
                }
            }

            foreach (var package in manifest.Packages.OrderBy(static package => package.Id, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(packagesDirectory, package.FileName)))
                {
                    continue;
                }

                missingPackageFileNames.Add(package.FileName);
                diagnostics.Add(new("SPB005", $"Required package is missing: {package.FileName}"));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result([new BaselineDiagnostic("SPB001", $"Unsafe or invalid baseline manifest: {exception.Message}")]);
        }

        if (!string.Equals(manifest.Repository.FullName, RepositoryName, StringComparison.Ordinal))
        {
            diagnostics.Add(new("SPB002", "Repository full name mismatch", RepositoryName, manifest.Repository.FullName));
        }

        try
        {
            _ = await RunOutputAsync(
                _gitPath,
                ["-C", options.RepositoryRoot, "merge-base", "--is-ancestor", manifest.Repository.CaptureCommitSha, "HEAD"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryCheckException exception)
        {
            diagnostics.Add(new("SPB003", $"Capture commit is not an ancestor of HEAD or is unavailable: {exception.Message}"));
        }

        if (options.Mode == BaselineVerificationMode.Full)
        {
            try
            {
                var actualSdk = ReadSdkVersion(options.RepositoryRoot);
                if (!string.Equals(actualSdk, manifest.Repository.SdkVersion, StringComparison.Ordinal))
                {
                    diagnostics.Add(new("SPB004", "global.json SDK mismatch", manifest.Repository.SdkVersion, actualSdk));
                }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException
                                               or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                diagnostics.Add(new("SPB004", $"global.json SDK could not be read: {exception.Message}"));
            }
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

            byte[] bytes;
            try
            {
                bytes = CanonicalText.ToUtf8Bytes(
                    await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(new("SPB006", $"Snapshot is not valid UTF-8 text: {reference.Path}"));
                continue;
            }

            snapshotBytes[reference.Path] = bytes;
            var hash = Hashing.Sha256Hex(bytes);
            if (!string.Equals(hash, reference.Sha256, StringComparison.Ordinal))
            {
                diagnostics.Add(new("SPB006", $"Snapshot hash mismatch: {reference.Path}", reference.Sha256, hash));
            }
        }

        var reportPath = Path.Combine(baselineRoot, "baseline-report.md");
        if (!File.Exists(reportPath))
        {
            diagnostics.Add(new("SPB005", "Required baseline report is missing: baseline-report.md"));
        }
        else
        {
            var expectedReport = BaselineReport.Create(manifest);
            try
            {
                var actualReport = CanonicalText.ToUtf8Bytes(
                    await File.ReadAllBytesAsync(reportPath, cancellationToken).ConfigureAwait(false));
                if (!actualReport.AsSpan().SequenceEqual(expectedReport))
                {
                    diagnostics.Add(new("SPB006", "Baseline report content mismatch",
                        Hashing.Sha256Hex(expectedReport), Hashing.Sha256Hex(actualReport)));
                }
            }
            catch (DecoderFallbackException)
            {
                diagnostics.Add(new("SPB006", "Baseline report is not valid UTF-8 text"));
            }
        }

        var packageSnapshots = new List<NuGetPackageSnapshot>();
        foreach (var package in manifest.Packages.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (missingPackageFileNames.Contains(package.FileName))
            {
                continue;
            }

            var packagePath = Path.Combine(packagesDirectory, package.FileName);
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

        if (options.Mode == BaselineVerificationMode.Full)
        {
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
        }

        var releaseBranch = $"release/{manifest.TargetRelease}";
        foreach (var workflow in CurrentWorkflowPolicy)
        {
            var path = RepositoryPaths.ResolveWithinRoot(options.RepositoryRoot, workflow.Path, "workflow");
            if (!WorkflowPolicyContainsBranch(path, workflow.Events, releaseBranch))
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
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest(executable, arguments, ProcessTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("Repository process was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(
                ExitCodes.ExternalSourceUnavailable,
                $"Process failed: {executable} {string.Join(' ', arguments)}",
                exception);
        }

        if (result.ExitCode != 0)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable,
                $"Process failed: {executable} {string.Join(' ', arguments)}");
        }

        return result.StandardOutput.Trim();
    }

    private static bool WorkflowPolicyContainsBranch(
        string path,
        IReadOnlyList<string> requiredEvents,
        string branch)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024)
        {
            return false;
        }

        var found = requiredEvents.ToDictionary(static item => item, static _ => false, StringComparer.Ordinal);
        string? currentEvent = null;
        var inOn = false;
        var inBranches = false;
        var lineCount = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            if (++lineCount > 10_000 || rawLine.Contains('\t', StringComparison.Ordinal))
            {
                return false;
            }

            var line = StripYamlComment(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var indent = line.Length - line.TrimStart().Length;
            var content = line[indent..];
            if (indent == 0)
            {
                inOn = content == "on:";
                currentEvent = null;
                inBranches = false;
                continue;
            }

            if (!inOn)
            {
                continue;
            }

            if (indent == 2 && content.EndsWith(':') && !content.StartsWith('-'))
            {
                currentEvent = content[..^1];
                inBranches = false;
                continue;
            }

            if (indent <= 2)
            {
                currentEvent = null;
                inBranches = false;
                continue;
            }

            if (currentEvent is null || !found.ContainsKey(currentEvent))
            {
                continue;
            }

            if (indent == 4 && content.StartsWith("branches:", StringComparison.Ordinal))
            {
                inBranches = true;
                var value = content["branches:".Length..].Trim();
                if (value.Length != 0 && ParseInlineBranches(value).Contains(branch, StringComparer.Ordinal))
                {
                    found[currentEvent] = true;
                }

                continue;
            }

            if (inBranches && indent == 6 && content.StartsWith("- ", StringComparison.Ordinal))
            {
                var value = Unquote(content[2..].Trim());
                if (string.Equals(value, branch, StringComparison.Ordinal))
                {
                    found[currentEvent] = true;
                }

                continue;
            }

            if (indent <= 4)
            {
                inBranches = false;
            }
        }

        return found.Values.All(static value => value);
    }

    private static IEnumerable<string> ParseInlineBranches(string value)
    {
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
        {
            return [];
        }

        return value[1..^1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Unquote);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"'
            ? value[1..^1]
            : value;

    private static string StripYamlComment(string line)
    {
        char quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character is '\'' or '"')
            {
                quote = quote == '\0' ? character : quote == character ? '\0' : quote;
            }
            else if (character == '#' && quote == '\0')
            {
                return line[..index];
            }
        }

        return line;
    }
}
