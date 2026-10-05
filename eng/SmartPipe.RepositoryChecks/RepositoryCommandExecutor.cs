using SmartPipe.RepositoryChecks.Agent;
using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Consumers;
using SmartPipe.RepositoryChecks.Documentation;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.Profiles;
using SmartPipe.RepositoryChecks.Release;
using SmartPipe.RepositoryChecks.Reporting;
using SmartPipe.RepositoryChecks.Scaffolding;
using System.Text;
using System.Text.Json;

namespace SmartPipe.RepositoryChecks;

internal static class RepositoryCommandExecutor
{
    private const string DotNetExecutable = DotNetExecutable;
    private const string GitExecutable = GitExecutable;
    internal static async Task<int> ExecuteAsync(
        RepositoryCheckCommand command,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        return command switch
        {
            CaptureBaselineOptions options => await CaptureBaselineAsync(options, runner, cancellationToken).ConfigureAwait(false),
            VerifyBaselineOptions options => await VerifyBaselineAsync(options, runner, cancellationToken).ConfigureAwait(false),
            ProvisionBaselineOptions options => await ProvisionBaselineAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyProfileOptions options => await VerifyProfileAsync(options, cancellationToken).ConfigureAwait(false),
            AgentContextOptions options => await AgentContextAsync(options, runner, cancellationToken).ConfigureAwait(false),
            VerifyTaskOptions options => await VerifyTaskAsync(options, runner, cancellationToken).ConfigureAwait(false),
            AgentEvidenceOptions options => await AgentEvidenceAsync(options, runner, cancellationToken).ConfigureAwait(false),
            VerifySp220ScopeOptions options => await VerifySp220ScopeAsync(options, runner, cancellationToken).ConfigureAwait(false),
            VerifyCentralPackagesOptions options => await VerifyCentralPackagesAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyPackageProjectsOptions options => await VerifyPackageProjectsAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyLockFilesOptions options => await VerifyLockFilesAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyDocumentationOptions options => await VerifyDocumentationAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyNuGetAuditOptions options => await VerifyNuGetAuditAsync(options).ConfigureAwait(false),
            CanonicalizeJsonOptions options => await CanonicalizeJsonAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyPackageGraphOptions options => await VerifyPackageGraphAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyPackageMetadataOptions options => await VerifyPackageMetadataAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyPackageOwnershipOptions options => await VerifyPackageOwnershipAsync(options, cancellationToken).ConfigureAwait(false),
            VerifyReleaseVersionOptions options => await VerifyReleaseVersionAsync(options, cancellationToken).ConfigureAwait(false),
            PrepareReleaseNotesOptions options => await PrepareReleaseNotesAsync(options, cancellationToken).ConfigureAwait(false),
            ScaffoldPackageOptions options => await ScaffoldPackageAsync(options, cancellationToken).ConfigureAwait(false),
            ListPackagesOptions options => await ListPackagesAsync(options, cancellationToken).ConfigureAwait(false),
            RunConsumersCommandOptions options => await RunConsumersAsync(options, cancellationToken).ConfigureAwait(false),
            PackPackagesOptions options => await PackPackagesAsync(options, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Unsupported command type."),
        };
    }

    private static async Task<int> CaptureBaselineAsync(
        CaptureBaselineOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var fetcher = new NuGetPackageFetcher(httpClient, new NuGetServiceIndexClient(httpClient));
        var packageReader = new NuGetPackageReader();
        var signatureVerifier = new NuGetPackageSignatureVerifier(runner, DotNetExecutable);
        var repositoryReader = new BaselineRepositorySnapshotReader(runner, DotNetExecutable);
        var verification = new BaselineVerificationService(
            runner, GitExecutable, signatureVerifier, packageReader, repositoryReader);
        await new BaselineCaptureService(
            runner, GitExecutable, DotNetExecutable, fetcher, signatureVerifier, packageReader,
            repositoryReader, verification)
            .CaptureAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await Console.Out.WriteLineAsync("BASELINE CAPTURED");
        return ExitCodes.Success;
    }

    private static async Task<int> VerifyBaselineAsync(
        VerifyBaselineOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var packageReader = new NuGetPackageReader();
        var signatureVerifier = new NuGetPackageSignatureVerifier(runner, DotNetExecutable);
        var repositoryReader = new BaselineRepositorySnapshotReader(runner, DotNetExecutable);
        var verification = new BaselineVerificationService(
            runner, GitExecutable, signatureVerifier, packageReader, repositoryReader);
        var result = await BaselineCommandOrchestrator.VerifyAsync(
            options,
            async (provision, ct) =>
            {
                using var httpClient = new HttpClient();
                var fetcher = new NuGetPackageFetcher(httpClient, new NuGetServiceIndexClient(httpClient));
                await new BaselinePackageProvisioner(fetcher)
                    .ProvisionAsync(provision, ct)
                    .ConfigureAwait(false);
            },
            (offline, ct) => verification.VerifyAsync(offline, ct),
            cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(result.Format());
        return result.Success ? ExitCodes.Success : ExitCodes.RepositorySnapshotMismatch;
    }

    private static async Task<int> ProvisionBaselineAsync(
        ProvisionBaselineOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = new HttpClient();
            var fetcher = new NuGetPackageFetcher(httpClient, new NuGetServiceIndexClient(httpClient));
            var count = await new BaselinePackageProvisioner(fetcher)
                .ProvisionAsync(options, cancellationToken)
                .ConfigureAwait(false);
            await Console.Out.WriteLineAsync($"BASELINE PACKAGES PROVISIONED packages={count}");
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync("[SPB001] Baseline acquisition input is invalid.");
            return ExitCodes.SchemaOrManifestInvalid;
        }
    }

    private static async Task<int> VerifyProfileAsync(
        VerifyProfileOptions options,
        CancellationToken cancellationToken)
    {
        VerificationProfile selected;
        try
        {
            var manifest = await VerificationProfileManifestLoader
                .LoadAsync(options.RepositoryRoot, cancellationToken)
                .ConfigureAwait(false);
            selected = manifest.Profiles.FirstOrDefault(item =>
                string.Equals(item.Name, options.Profile, StringComparison.Ordinal))
                ?? throw new JsonException($"Profile '{options.Profile}' is not defined.");
        }
        catch (JsonException)
        {
            await RenderProfileRunAsync(
                cancellationToken,
                new CheckRun(
                    "verify-profile",
                    options.Profile,
                    false,
                    ExitCodes.SchemaOrManifestInvalid,
                    [new CheckDiagnostic("SPPROFILE001", "Verification profile manifest is invalid.")]),
                options.Format,
                options.FailuresOnly).ConfigureAwait(false);
            return ExitCodes.SchemaOrManifestInvalid;
        }
        catch (IOException)
        {
            await RenderProfileRunAsync(
                cancellationToken,
                new CheckRun(
                    "verify-profile",
                    options.Profile,
                    false,
                    ExitCodes.SchemaOrManifestInvalid,
                    [new CheckDiagnostic("SPPROFILE001", "Verification profile manifest could not be read.")]),
                options.Format,
                options.FailuresOnly).ConfigureAwait(false);
            return ExitCodes.SchemaOrManifestInvalid;
        }

        var result = await new VerificationProfileRunner(
            VerificationProfileChecks.Create(options.RepositoryRoot))
            .RunAsync(selected, cancellationToken)
            .ConfigureAwait(false);
        foreach (var run in result.CheckRuns)
        {
            await RenderProfileRunAsync(
                cancellationToken,
                run,
                options.Format,
                options.FailuresOnly).ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    private static async Task<int> AgentContextAsync(
        AgentContextOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var context = await new AgentContextBuilder(runner)
            .BuildAsync(
                options.RepositoryRoot,
                options.Epic,
                options.Task,
                cancellationToken)
            .ConfigureAwait(false);
        var serialized = AgentJsonSerializer.Serialize(context.Context);
        await Console.Out.WriteAsync(serialized.AsMemory(), cancellationToken);
        return ExitCodes.Success;
    }

    private static async Task<int> VerifyTaskAsync(
        VerifyTaskOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var result = await new AgentTaskVerifier(new AgentContextBuilder(runner))
            .VerifyAsync(
                options.RepositoryRoot,
                options.Epic,
                options.Task,
                options.Format,
                options.FailuresOnly,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var run in result.CheckRuns)
        {
            await RenderProfileRunAsync(
                cancellationToken,
                run,
                options.Format,
                options.FailuresOnly).ConfigureAwait(false);
        }

        return result.ExitCode;
    }

    private static async Task<int> AgentEvidenceAsync(
        AgentEvidenceOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var result = await new AgentEvidenceService(new AgentContextBuilder(runner))
            .CollectAsync(options.RepositoryRoot, options.Epic, cancellationToken)
            .ConfigureAwait(false);
        var serialized = AgentJsonSerializer.Serialize(result.Evidence);
        await Console.Out.WriteAsync(serialized.AsMemory(), cancellationToken);
        return result.ExitCode;
    }

    private static async Task<int> VerifySp220ScopeAsync(
        VerifySp220ScopeOptions options,
        ProcessRunner runner,
        CancellationToken cancellationToken)
    {
        var result = await new Sp220ScopeVerificationService(runner, GitExecutable)
            .VerifyAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await Console.Out.WriteLineAsync(result.Format());
        return result.Success ? ExitCodes.Success : ExitCodes.RepositorySnapshotMismatch;
    }

    private static async Task<int> VerifyCentralPackagesAsync(
        VerifyCentralPackagesOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new CentralPackageVersionReader()
            .VerifyAsync(options.RepositoryRoot, options.Mode, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Errors.Concat(result.Warnings))
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] {violation.Message} ({violation.Path})");
        }

        var modeName = options.Mode.ToString().ToLowerInvariant();
        await Console.Out.WriteLineAsync(result.Success
            ? $"SP220_CPM_OK packages={result.Versions.Count} warnings={result.Warnings.Count} mode={modeName}"
            : $"SP220_CPM_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={result.Errors.Count} mode={modeName}");
        return result.Success
            ? ExitCodes.Success
            : ExitCodes.CentralPackagePolicyViolation;
    }

    private static async Task<int> VerifyPackageProjectsAsync(
        VerifyPackageProjectsOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new OfficialPackageProjectVerifier()
            .VerifyAsync(options.RepositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Errors)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] {violation.Message} ({violation.Path})");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? "SP220_PACKAGE_PROJECTS_OK projects=3"
            : $"SP220_PACKAGE_PROJECTS_FAILED code={ExitCodes.PackageProjectViolation} violations={result.Errors.Count}");
        return result.Success
            ? ExitCodes.Success
            : ExitCodes.PackageProjectViolation;
    }

    private static async Task<int> VerifyLockFilesAsync(
        VerifyLockFilesOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new VerifyLockFilesCommand()
            .ExecuteAsync(options.RepositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        foreach (var error in result.Errors)
        {
            await Console.Error.WriteLineAsync($"[{error}]");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? "SP220_LOCK_FILES_OK"
            : $"SP220_LOCK_FILES_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={result.Errors.Count}");
        return result.Success
            ? ExitCodes.Success
            : ExitCodes.CentralPackagePolicyViolation;
    }

    private static async Task<int> VerifyDocumentationAsync(
        VerifyDocumentationOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new DocumentationVerificationService()
            .VerifyAsync(options.RepositoryRoot, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Violations)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] path={violation.Path} rule={violation.Rule}");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? "SP220_DOCS_OK"
            : $"SP220_DOCS_FAILED code={ExitCodes.DocumentationViolation} violations={result.Violations.Count}");
        return result.Success ? ExitCodes.Success : ExitCodes.DocumentationViolation;
    }

    private static async Task<int> VerifyNuGetAuditAsync(VerifyNuGetAuditOptions options)
    {
        var result = new NuGetAuditPolicyValidator()
            .Verify(options.RepositoryRoot, options.ReportPath);
        foreach (var error in result.Errors)
        {
            await Console.Error.WriteLineAsync($"[{error}]");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? "SP220_NUGET_AUDIT_OK"
            : $"SP220_NUGET_AUDIT_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={result.Errors.Count}");
        return result.Success
            ? ExitCodes.Success
            : ExitCodes.CentralPackagePolicyViolation;
    }

    private static async Task<int> CanonicalizeJsonAsync(
        CanonicalizeJsonOptions options,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(options.InputPath)
            .Equals("package-ownership.json", StringComparison.OrdinalIgnoreCase))
        {
            if (options.Check)
            {
                var graph = await new PackageGraphLoader()
                    .LoadAsync(
                        options.RepositoryRoot,
                        "eng/package-graph.json",
                        cancellationToken)
                    .ConfigureAwait(false);
                _ = await new OwnershipLoader()
                    .LoadAsync(
                        options.RepositoryRoot,
                        options.InputPath,
                        graph,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await new OwnershipLoader()
                    .CanonicalizeAsync(
                        options.RepositoryRoot,
                        options.InputPath,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            await new PackageGraphLoader()
                .CanonicalizeAsync(
                    options.RepositoryRoot,
                    options.InputPath,
                    options.Check,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync("SP220_CANONICAL_JSON_OK");
        return ExitCodes.Success;
    }

    private static async Task<int> VerifyPackageGraphAsync(
        VerifyPackageGraphOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new VerifyPackageGraphCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Violations)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] package={violation.PackageId} representation={violation.Representation} dependency={violation.Dependency ?? "-"} rule={violation.Rule}");
        }

        var graph = await new PackageGraphLoader()
            .LoadAsync(
                options.RepositoryRoot,
                options.GraphPath,
                cancellationToken)
            .ConfigureAwait(false);
        var active = graph.Packages.Count(package => package.Lifecycle != PackageLifecycle.Planned);
        var planned = graph.Packages.Count - active;
        await Console.Out.WriteLineAsync(result.Success
            ? $"SP220_PACKAGE_GRAPH_OK packages={graph.Packages.Count} active={active} planned={planned} mode={options.Mode.ToString().ToLowerInvariant()}"
            : $"SP220_PACKAGE_GRAPH_FAILED code={ExitCodes.PackageProjectViolation} violations={result.Violations.Count}");
        return result.Success ? ExitCodes.Success : ExitCodes.PackageProjectViolation;
    }

    private static async Task<int> VerifyPackageMetadataAsync(
        VerifyPackageMetadataOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new VerifyPackageMetadataCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Violations)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] package={violation.PackageId} path={violation.Path ?? "-"} rule={violation.Rule}");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? $"SP220_PACKAGE_METADATA_OK packages={result.Packages} mode={result.Mode}"
            : $"SP220_PACKAGE_METADATA_FAILED code={ExitCodes.PackedPackageViolation} violations={result.Violations.Count}");
        return result.Success ? ExitCodes.Success : ExitCodes.PackedPackageViolation;
    }

    private static async Task<int> VerifyPackageOwnershipAsync(
        VerifyPackageOwnershipOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new VerifyPackageOwnershipCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Violations)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] type={violation.Type} rule={violation.Rule}");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? $"SP220_PACKAGE_OWNERSHIP_OK types={result.BaselineTypes} mode={options.Mode.ToString().ToLowerInvariant()}"
            : $"SP220_PACKAGE_OWNERSHIP_FAILED code={ExitCodes.OwnershipViolation} violations={result.Violations.Count}");
        return result.Success ? ExitCodes.Success : ExitCodes.OwnershipViolation;
    }

    private static async Task<int> VerifyReleaseVersionAsync(
        VerifyReleaseVersionOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new VerifyReleaseVersionCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var violation in result.Violations)
        {
            await Console.Error.WriteLineAsync(
                $"[{violation.Code}] package={violation.PackageId} path={violation.Path ?? "-"} rule={violation.Rule}");
        }

        await Console.Out.WriteLineAsync(result.Success
            ? $"SP220_RELEASE_VERSION_OK version={result.PackageVersion} mode={options.Mode.ToString().ToLowerInvariant()}"
            : $"SP220_RELEASE_VERSION_FAILED code={ExitCodes.ReleaseVersionMismatch} violations={result.Violations.Count}");
        return result.Success ? ExitCodes.Success : ExitCodes.ReleaseVersionMismatch;
    }

    private static async Task<int> PrepareReleaseNotesAsync(
        PrepareReleaseNotesOptions options,
        CancellationToken cancellationToken)
    {
        var changelog = await File
            .ReadAllTextAsync(options.ChangelogPath, cancellationToken)
            .ConfigureAwait(false);
        var notes = ReleaseNotesExtractor.Extract(changelog, options.Version);
        var outputDirectory = Path.GetDirectoryName(options.OutputPath)
            ?? throw new IOException("Release notes output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            options.OutputPath,
            notes,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"SP220_RELEASE_NOTES_OK version={options.Version} path={Path.GetRelativePath(options.RepositoryRoot, options.OutputPath).Replace('\\', '/')}");
        return ExitCodes.Success;
    }

    private static async Task<int> ScaffoldPackageAsync(
        ScaffoldPackageOptions options,
        CancellationToken cancellationToken)
    {
        var result = await new ScaffoldPackageCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        foreach (var step in result.RequiredSteps)
        {
            await Console.Out.WriteLineAsync($"NEXT {step}");
        }

        await Console.Out.WriteLineAsync(
            $"SP220_SCAFFOLD_OK package={result.PackageId} kind={result.Kind} files={result.Files.Count} dryRun={result.DryRun.ToString().ToLowerInvariant()}");
        return ExitCodes.Success;
    }

    private static async Task<int> ListPackagesAsync(
        ListPackagesOptions options,
        CancellationToken cancellationToken)
    {
        var graph = await new PackageGraphLoader().LoadAsync(
            options.RepositoryRoot,
            "eng/package-graph.json",
            cancellationToken).ConfigureAwait(false);
        foreach (var package in graph.Packages.Where(package => package.Lifecycle == options.Lifecycle))
        {
            await Console.Out.WriteLineAsync(package.Id);
        }

        return ExitCodes.Success;
    }

    private static async Task<int> RunConsumersAsync(
        RunConsumersCommandOptions options,
        CancellationToken cancellationToken)
    {
        var results = await new ConsumerScenarioRunner()
            .RunAsync(
                new(
                    options.RepositoryRoot,
                    options.Set,
                    options.PackageDirectory,
                    options.PackageVersion,
                    options.ManifestPath,
                    options.Category,
                    options.Scenario,
                    options.ExcludeCategory,
                    options.MaxParallelism),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var result in results)
        {
            await Console.Out.WriteLineAsync(
                $"SP220_CONSUMER_OK scenario={result.Scenario} durationMs={result.DurationMs} dependencies={result.ObservedSmartPipeDependencies.Count}");
        }

        await Console.Out.WriteLineAsync(
            $"SP220_CONSUMERS_OK scenarios={results.Count} set={options.Set}");
        return ExitCodes.Success;
    }

    private static async Task<int> PackPackagesAsync(
        PackPackagesOptions options,
        CancellationToken cancellationToken)
    {
        var manifest = await new PackPackagesCommand()
            .ExecuteAsync(options, cancellationToken)
            .ConfigureAwait(false);
        await Console.Out.WriteLineAsync(
            $"SP220_PACKAGES_OK packages={manifest.Packages.Count} version={manifest.Version} mode={manifest.Mode}");
        return ExitCodes.Success;
    }

    private static async Task RenderProfileRunAsync(
        CancellationToken cancellationToken,
        CheckRun run,
        ProfileOutputFormat format,
        bool failuresOnly)
    {
        var output = format switch
        {
            ProfileOutputFormat.Text => CheckRunTextRenderer.Render(run, failuresOnly),
            ProfileOutputFormat.Jsonl => CheckRunJsonlRenderer.Render(run, failuresOnly),
            ProfileOutputFormat.GitHub => CheckRunGitHubRenderer.Render(run, failuresOnly),
            _ => throw new InvalidOperationException(
                $"Unsupported profile output format '{format}'."),
        };
        await Console.Out.WriteAsync(output.AsMemory(), cancellationToken);
    }
}
