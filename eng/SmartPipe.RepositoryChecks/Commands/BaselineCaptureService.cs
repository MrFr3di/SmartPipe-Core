using System.Text.Json;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Commands;

internal sealed class BaselineCaptureService
{
    private static readonly string[] PackageIds =
    [
        "SmartPipe.Core",
        "SmartPipe.Extensions",
        "SmartPipe.Extensions.Json",
    ];

    private readonly IProcessRunner _processRunner;
    private readonly string _gitPath;
    private readonly string _dotnetPath;
    private readonly INuGetPackageFetcher _fetcher;
    private readonly INuGetPackageSignatureVerifier _signatureVerifier;
    private readonly NuGetPackageReader _packageReader;
    private readonly BaselineRepositorySnapshotReader _repositoryReader;
    private readonly BaselineVerificationService _verification;

    public BaselineCaptureService(
        IProcessRunner processRunner,
        string gitPath,
        string dotnetPath,
        INuGetPackageFetcher fetcher,
        INuGetPackageSignatureVerifier signatureVerifier,
        NuGetPackageReader packageReader,
        BaselineRepositorySnapshotReader repositoryReader,
        BaselineVerificationService verification)
    {
        _processRunner = processRunner;
        _gitPath = gitPath;
        _dotnetPath = dotnetPath;
        _fetcher = fetcher;
        _signatureVerifier = signatureVerifier;
        _packageReader = packageReader;
        _repositoryReader = repositoryReader;
        _verification = verification;
    }

    public async Task CaptureAsync(CaptureBaselineOptions options, CancellationToken cancellationToken)
    {
        ValidateFixedBaseline(options);
        var root = RepositoryPaths.NormalizeRoot(options.RepositoryRoot);
        _ = RepositoryPaths.NormalizeContainedFullPath(
            root, Path.GetFullPath(options.PackagesDirectory), "packages directory");
        var workflowEvidence = RepositoryPaths.ResolveWithinRoot(root,
            RepositoryPaths.ToRelativePath(root, Path.GetFullPath(options.WorkflowEvidencePath)), "workflow evidence");
        RepositoryPaths.RequireExistingRegularFile(root, workflowEvidence, "workflow evidence");
        var target = RepositoryPaths.ResolveWithinRoot(root, options.OutputDirectory, "baseline output");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{options.BaselineVersion}.capture-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".{options.BaselineVersion}.backup-{Guid.NewGuid():N}");
        var published = false;
        Directory.CreateDirectory(temporary);
        try
        {
            await ValidateRepositoryAsync(options, cancellationToken).ConfigureAwait(false);
            var sdkVersion = BaselineVerificationService.ReadSdkVersion(root);
            var actualSdk = await RunOutputAsync(_dotnetPath, ["--version"], cancellationToken).ConfigureAwait(false);
            if (!string.Equals(sdkVersion, actualSdk, StringComparison.Ordinal))
            {
                throw new RepositoryCheckException(ExitCodes.UsageOrConfigurationError,
                    $"dotnet SDK mismatch: global.json requires {sdkVersion}, actual {actualSdk}.");
            }

            var packageFiles = new List<(string Id, string Path, string Hash)>();
            foreach (var packageId in PackageIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fetched = await _fetcher.FetchAsync(
                    packageId, options.BaselineVersion, options.PackagesDirectory, cancellationToken).ConfigureAwait(false);
                var canonicalPath = Path.Combine(options.PackagesDirectory, $"{packageId}.{options.BaselineVersion}.nupkg");
                if (!RepositoryPaths.FileSystemPathComparer.Equals(Path.GetFullPath(fetched), Path.GetFullPath(canonicalPath)))
                {
                    File.Copy(fetched, canonicalPath, overwrite: true);
                }

                await _signatureVerifier.VerifyAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
                packageFiles.Add((packageId, canonicalPath,
                    await Hashing.Sha256FileAsync(canonicalPath, cancellationToken).ConfigureAwait(false)));
            }

            var packageSnapshots = new List<NuGetPackageSnapshot>(packageFiles.Count);
            foreach (var package in packageFiles)
            {
                packageSnapshots.Add(await _packageReader.ReadAsync(
                    package.Path, package.Id, options.BaselineVersion, cancellationToken).ConfigureAwait(false));
            }

            var repositorySnapshots = await _repositoryReader.ReadAsync(
                root, "SmartPipe.Core.slnx", cancellationToken).ConfigureAwait(false);
            var orderedPackages = packageSnapshots.OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["public-api.json"] = repositorySnapshots.PublicApi,
                ["package-assets.json"] = BaselineSnapshotJson.Serialize(orderedPackages.Select(static item => item.Assets).ToArray()),
                ["package-dependencies.json"] = BaselineSnapshotJson.Serialize(orderedPackages.Select(static item => item.Dependencies).ToArray()),
                ["repository-dependencies.json"] = repositorySnapshots.RepositoryDependencies,
            };
            foreach (var file in files)
            {
                await File.WriteAllBytesAsync(Path.Combine(temporary, file.Key), file.Value, cancellationToken).ConfigureAwait(false);
            }

            var outputRelative = RepositoryPaths.ToRelativePath(root, target);
            var workflows = await ReadWorkflowEvidenceAsync(workflowEvidence, cancellationToken).ConfigureAwait(false);
            var manifest = new BaselineManifest
            {
                SchemaVersion = 1,
                BaselineName = "smartpipe-core-2.1.2",
                TargetRelease = options.TargetRelease,
                Repository = new RepositoryBaseline
                {
                    FullName = options.Repository,
                    DefaultBranch = "main",
                    CommitSha = options.Commit,
                    SdkVersion = sdkVersion,
                    SolutionPath = "SmartPipe.Core.slnx",
                    RequiredWorkflows = workflows,
                },
                Packages = packageFiles.Select(package => new PackageBaseline
                {
                    Id = package.Id,
                    Version = options.BaselineVersion,
                    Source = new Uri("https://api.nuget.org/v3/index.json"),
                    FileName = $"{package.Id}.{options.BaselineVersion}.nupkg",
                    Sha256 = package.Hash,
                    RequireRepositorySignature = true,
                }).ToArray(),
                PublicApi = Snapshot(outputRelative, "public-api.json", files),
                PackageAssets = Snapshot(outputRelative, "package-assets.json", files),
                PackageDependencies = Snapshot(outputRelative, "package-dependencies.json", files),
                RepositoryDependencies = Snapshot(outputRelative, "repository-dependencies.json", files),
            };

            // Manifest is intentionally the final write in the temporary output.
            var temporaryManifest = Path.Combine(temporary, "manifest.json");
            await BaselineManifestSerializer.WriteAsync(temporaryManifest, manifest, cancellationToken).ConfigureAwait(false);
            var selfVerification = await _verification.VerifyAsync(
                new VerifyBaselineOptions(root, temporaryManifest, options.PackagesDirectory, Offline: true),
                temporary,
                cancellationToken).ConfigureAwait(false);
            if (!selfVerification.Success)
            {
                throw new RepositoryCheckException(ExitCodes.RepositorySnapshotMismatch, selfVerification.Format());
            }

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(temporary, target);
                published = true;
            }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }
        }
        finally
        {
            if (!published && Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private async Task ValidateRepositoryAsync(CaptureBaselineOptions options, CancellationToken cancellationToken)
    {
        var status = await RunOutputAsync(
            _gitPath, ["-C", options.RepositoryRoot, "status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (status.Length != 0)
        {
            throw new RepositoryCheckException(ExitCodes.UsageOrConfigurationError, "Repository must be clean before baseline capture.");
        }

        var commit = await RunOutputAsync(
            _gitPath, ["-C", options.RepositoryRoot, "rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false);
        if (!string.Equals(commit, options.Commit, StringComparison.Ordinal))
        {
            throw new RepositoryCheckException(ExitCodes.UsageOrConfigurationError,
                $"Repository commit mismatch: expected {options.Commit}, actual {commit}.");
        }
    }

    private async Task<string> RunOutputAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            new ProcessRequest(executable, arguments, TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable,
                $"Process failed: {executable} {string.Join(' ', arguments)}");
        }

        return result.StandardOutput.Trim();
    }

    private static SnapshotReference Snapshot(
        string outputRelative,
        string fileName,
        IReadOnlyDictionary<string, byte[]> files) => new()
        {
            Path = $"{outputRelative}/{fileName}",
            Sha256 = Hashing.Sha256Hex(files[fileName]),
        };

    private static async Task<IReadOnlyList<WorkflowBaseline>> ReadWorkflowEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var workflows = document.RootElement.GetProperty("workflows").EnumerateArray().Select(item => new WorkflowBaseline
        {
            Name = item.GetProperty("name").GetString()!,
            RunId = item.GetProperty("runId").GetInt64(),
            Url = new Uri(item.GetProperty("url").GetString()!, UriKind.Absolute),
            Conclusion = item.GetProperty("conclusion").GetString()!,
        }).ToArray();
        return workflows;
    }

    private static void ValidateFixedBaseline(CaptureBaselineOptions options)
    {
        if (!string.Equals(options.BaselineVersion, "2.1.2", StringComparison.Ordinal)
            || !string.Equals(options.TargetRelease, "2.2.0", StringComparison.Ordinal))
        {
            throw new CommandLineException("Only the fixed 2.1.2 to 2.2.0 baseline is supported.");
        }
    }
}
