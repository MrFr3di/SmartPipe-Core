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
    private readonly Action<string> _deleteBackup;

    public BaselineCaptureService(
        IProcessRunner processRunner,
        string gitPath,
        string dotnetPath,
        INuGetPackageFetcher fetcher,
        INuGetPackageSignatureVerifier signatureVerifier,
        NuGetPackageReader packageReader,
        BaselineRepositorySnapshotReader repositoryReader,
        BaselineVerificationService verification,
        Action<string>? deleteBackup = null)
    {
        _processRunner = processRunner;
        _gitPath = gitPath;
        _dotnetPath = dotnetPath;
        _fetcher = fetcher;
        _signatureVerifier = signatureVerifier;
        _packageReader = packageReader;
        _repositoryReader = repositoryReader;
        _verification = verification;
        _deleteBackup = deleteBackup ?? (static path => Directory.Delete(path, recursive: true));
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
            var workflows = await ReadWorkflowEvidenceAsync(
                workflowEvidence, options.Commit, cancellationToken).ConfigureAwait(false);
            var manifest = new BaselineManifest
            {
                SchemaVersion = 1,
                BaselineName = "smartpipe-core-2.1.2",
                TargetRelease = options.TargetRelease,
                Repository = new RepositoryBaseline
                {
                    FullName = options.Repository,
                    DefaultBranch = "main",
                    CaptureCommitSha = options.Commit,
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

            await File.WriteAllBytesAsync(
                Path.Combine(temporary, "baseline-report.md"),
                BaselineReport.Create(manifest),
                cancellationToken).ConfigureAwait(false);

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
                try
                {
                    _deleteBackup(backup);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Publication already succeeded. A stale private backup is safer than a false capture failure.
                }
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
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest(executable, arguments, TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
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
        string expectedCommit,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        try
        {
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow },
                cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Workflow evidence must be the raw gh run list JSON array.");
            }

            var runs = new List<WorkflowRunEvidence>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (runs.Count == 1000)
                {
                    throw new InvalidDataException("Workflow evidence exceeds 1000 runs.");
                }

                ValidateWorkflowProperties(item);
                var headSha = RequiredString(item, "headSha");
                if (!string.Equals(headSha, expectedCommit, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Every workflow evidence run must match the requested commit SHA.");
                }

                var createdAt = item.GetProperty("createdAt");
                if (createdAt.ValueKind != JsonValueKind.String || !createdAt.TryGetDateTimeOffset(out _))
                {
                    throw new InvalidDataException("Workflow evidence createdAt must be an ISO-8601 timestamp.");
                }

                var databaseId = item.GetProperty("databaseId");
                if (databaseId.ValueKind != JsonValueKind.Number
                    || !databaseId.TryGetInt64(out var runId)
                    || runId <= 0)
                {
                    throw new InvalidDataException("Workflow evidence databaseId must be positive.");
                }

                var status = RequiredString(item, "status");
                var conclusion = StringValue(item, "conclusion");
                if (!string.Equals(status, "completed", StringComparison.Ordinal)
                    || !string.Equals(conclusion, "success", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Every workflow evidence run must be completed successfully.");
                }

                runs.Add(new WorkflowRunEvidence(
                    runId,
                    RequiredString(item, "workflowName"),
                    headSha,
                    status,
                    conclusion,
                    new Uri(RequiredString(item, "url"), UriKind.Absolute),
                    RequiredString(item, "event")));
            }

            var workflows = new List<WorkflowBaseline>(3);
            foreach (var requiredName in new[] { "CI", "CodeQL", "Dependency Review" })
            {
                var successful = runs.Where(run =>
                    string.Equals(run.WorkflowName, requiredName, StringComparison.Ordinal)
                    && string.Equals(run.Status, "completed", StringComparison.Ordinal)
                    && string.Equals(run.Conclusion, "success", StringComparison.Ordinal)).ToArray();
                if (successful.Length != 1)
                {
                    throw new InvalidDataException(
                        $"Workflow evidence must contain exactly one completed successful {requiredName} run.");
                }

                var run = successful[0];
                workflows.Add(new WorkflowBaseline
                {
                    Name = run.WorkflowName,
                    RunId = run.DatabaseId,
                    HeadSha = run.HeadSha,
                    Url = run.Url,
                    Conclusion = run.Conclusion,
                });
            }

            return workflows;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Workflow evidence JSON is malformed.", exception);
        }
    }

    private static void ValidateWorkflowProperties(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Workflow evidence entries must be objects.");
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "databaseId", "workflowName", "headSha", "status", "conclusion", "url", "event", "createdAt",
        };
        foreach (var property in item.EnumerateObject())
        {
            if (!allowed.Remove(property.Name))
            {
                throw new InvalidDataException($"Workflow evidence contains duplicate or unknown property '{property.Name}'.");
            }
        }

        if (allowed.Count != 0)
        {
            throw new InvalidDataException($"Workflow evidence is missing property '{allowed.Order().First()}'.");
        }
    }

    private static string RequiredString(JsonElement item, string name)
    {
        var value = StringValue(item, name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"Workflow evidence {name} must be nonempty.")
            : value;
    }

    private static string StringValue(JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Workflow evidence {name} must be a string.");
    }

    private sealed record WorkflowRunEvidence(
        long DatabaseId,
        string WorkflowName,
        string HeadSha,
        string Status,
        string Conclusion,
        Uri Url,
        string Event);

    private static void ValidateFixedBaseline(CaptureBaselineOptions options)
    {
        if (!string.Equals(options.BaselineVersion, "2.1.2", StringComparison.Ordinal)
            || !string.Equals(options.TargetRelease, "2.2.0", StringComparison.Ordinal))
        {
            throw new CommandLineException("Only the fixed 2.1.2 to 2.2.0 baseline is supported.");
        }
    }
}
