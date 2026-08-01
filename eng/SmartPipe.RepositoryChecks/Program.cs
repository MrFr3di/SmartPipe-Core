using SmartPipe.RepositoryChecks.Baselines;
using SmartPipe.RepositoryChecks.Commands;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.Scaffolding;
using SmartPipe.RepositoryChecks.Consumers;

namespace SmartPipe.RepositoryChecks;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0
            && string.Equals(args[0], Infrastructure.RepositoryCheckProcessHost.DispatchArgument, StringComparison.Ordinal))
        {
            return await Infrastructure.RepositoryCheckProcessHost
                .RunAsync(args.AsMemory(1).ToArray())
                .ConfigureAwait(false);
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var command = CommandLineParser.Parse(args);
            var runner = new ProcessRunner();
            using var httpClient = new HttpClient();
            var fetcher = new NuGetPackageFetcher(httpClient, new NuGetServiceIndexClient(httpClient));
            var packageReader = new NuGetPackageReader();
            var signatureVerifier = new NuGetPackageSignatureVerifier(runner, "dotnet");
            var repositoryReader = new BaselineRepositorySnapshotReader(runner, "dotnet");
            var verification = new BaselineVerificationService(
                runner, "git", signatureVerifier, packageReader, repositoryReader);

            switch (command)
            {
                case CaptureBaselineOptions capture:
                    await new BaselineCaptureService(
                        runner, "git", "dotnet", fetcher, signatureVerifier, packageReader,
                        repositoryReader, verification).CaptureAsync(capture, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("BASELINE CAPTURED");
                    return ExitCodes.Success;

                case VerifyBaselineOptions verify:
                    if (!verify.Offline)
                    {
                        await new BaselinePackageProvisioner(fetcher)
                            .ProvisionAsync(verify, cancellation.Token).ConfigureAwait(false);
                    }

                    var result = await verification.VerifyAsync(
                        verify with { Offline = true }, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(result.Format());
                    return result.Success ? ExitCodes.Success : ExitCodes.RepositorySnapshotMismatch;

                case VerifySp220ScopeOptions verifyScope:
                    var scopeResult = await new Sp220ScopeVerificationService(runner, "git")
                        .VerifyAsync(verifyScope, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine(scopeResult.Format());
                    return scopeResult.Success ? ExitCodes.Success : ExitCodes.RepositorySnapshotMismatch;

                case VerifyCentralPackagesOptions verifyCentral:
                    var centralResult = await new CentralPackageVersionReader().VerifyAsync(
                        verifyCentral.RepositoryRoot, verifyCentral.Mode, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in centralResult.Errors.Concat(centralResult.Warnings))
                    {
                        Console.Error.WriteLine($"[{violation.Code}] {violation.Message} ({violation.Path})");
                    }

                    var modeName = verifyCentral.Mode.ToString().ToLowerInvariant();
                    Console.WriteLine(centralResult.Success
                        ? $"SP220_CPM_OK packages={centralResult.Versions.Count} warnings={centralResult.Warnings.Count} mode={modeName}"
                        : $"SP220_CPM_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={centralResult.Errors.Count} mode={modeName}");
                    return centralResult.Success ? ExitCodes.Success : ExitCodes.CentralPackagePolicyViolation;

                case VerifyPackageProjectsOptions verifyProjects:
                    var packageProjectResult = await new OfficialPackageProjectVerifier()
                        .VerifyAsync(verifyProjects.RepositoryRoot, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in packageProjectResult.Errors)
                    {
                        Console.Error.WriteLine($"[{violation.Code}] {violation.Message} ({violation.Path})");
                    }

                    Console.WriteLine(packageProjectResult.Success
                        ? $"SP220_PACKAGE_PROJECTS_OK projects=3"
                        : $"SP220_PACKAGE_PROJECTS_FAILED code={ExitCodes.PackageProjectViolation} violations={packageProjectResult.Errors.Count}");
                    return packageProjectResult.Success ? ExitCodes.Success : ExitCodes.PackageProjectViolation;

                case VerifyLockFilesOptions verifyLocks:
                    var lockResult = await new VerifyLockFilesCommand().ExecuteAsync(verifyLocks.RepositoryRoot, cancellation.Token).ConfigureAwait(false);
                    foreach (var error in lockResult.Errors) Console.Error.WriteLine($"[{error}]");
                    Console.WriteLine(lockResult.Success
                        ? "SP220_LOCK_FILES_OK"
                        : $"SP220_LOCK_FILES_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={lockResult.Errors.Count}");
                    return lockResult.Success ? ExitCodes.Success : ExitCodes.CentralPackagePolicyViolation;

                case VerifyNuGetAuditOptions verifyAudit:
                    var auditResult = new NuGetAuditPolicyValidator().Verify(verifyAudit.RepositoryRoot, verifyAudit.ReportPath);
                    foreach (var error in auditResult.Errors) Console.Error.WriteLine($"[{error}]");
                    Console.WriteLine(auditResult.Success
                        ? "SP220_NUGET_AUDIT_OK"
                        : $"SP220_NUGET_AUDIT_FAILED code={ExitCodes.CentralPackagePolicyViolation} violations={auditResult.Errors.Count}");
                    return auditResult.Success ? ExitCodes.Success : ExitCodes.CentralPackagePolicyViolation;

                case CanonicalizeJsonOptions canonicalize:
                    if (Path.GetFileName(canonicalize.InputPath).Equals("package-ownership.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (canonicalize.Check)
                        {
                            var graph = await new PackageGraphLoader().LoadAsync(canonicalize.RepositoryRoot, "eng/package-graph.json", cancellation.Token).ConfigureAwait(false);
                            _ = await new OwnershipLoader().LoadAsync(canonicalize.RepositoryRoot, canonicalize.InputPath, graph, cancellation.Token).ConfigureAwait(false);
                        }
                        else await new OwnershipLoader().CanonicalizeAsync(canonicalize.RepositoryRoot, canonicalize.InputPath, cancellation.Token).ConfigureAwait(false);
                    }
                    else await new PackageGraphLoader().CanonicalizeAsync(canonicalize.RepositoryRoot, canonicalize.InputPath, canonicalize.Check, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("SP220_CANONICAL_JSON_OK");
                    return ExitCodes.Success;

                case VerifyPackageGraphOptions verifyGraph:
                    var graphResult = await new VerifyPackageGraphCommand().ExecuteAsync(verifyGraph, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in graphResult.Violations)
                        Console.Error.WriteLine($"[{violation.Code}] package={violation.PackageId} representation={violation.Representation} dependency={violation.Dependency ?? "-"} rule={violation.Rule}");
                    var loadedGraph = await new PackageGraphLoader().LoadAsync(verifyGraph.RepositoryRoot, verifyGraph.GraphPath, cancellation.Token).ConfigureAwait(false);
                    var active = loadedGraph.Packages.Count(x => x.Lifecycle != PackageLifecycle.Planned);
                    var planned = loadedGraph.Packages.Count - active;
                    Console.WriteLine(graphResult.Success
                        ? $"SP220_PACKAGE_GRAPH_OK packages={loadedGraph.Packages.Count} active={active} planned={planned} mode={verifyGraph.Mode.ToString().ToLowerInvariant()}"
                        : $"SP220_PACKAGE_GRAPH_FAILED code={ExitCodes.PackageProjectViolation} violations={graphResult.Violations.Count}");
                    return graphResult.Success ? ExitCodes.Success : ExitCodes.PackageProjectViolation;

                case VerifyPackageMetadataOptions verifyMetadata:
                    var metadataResult = await new VerifyPackageMetadataCommand().ExecuteAsync(verifyMetadata, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in metadataResult.Violations)
                        Console.Error.WriteLine($"[{violation.Code}] package={violation.PackageId} path={violation.Path ?? "-"} rule={violation.Rule}");
                    Console.WriteLine(metadataResult.Success
                        ? $"SP220_PACKAGE_METADATA_OK packages={metadataResult.Packages} mode={metadataResult.Mode}"
                        : $"SP220_PACKAGE_METADATA_FAILED code={ExitCodes.PackedPackageViolation} violations={metadataResult.Violations.Count}");
                    return metadataResult.Success ? ExitCodes.Success : ExitCodes.PackedPackageViolation;

                case VerifyPackageOwnershipOptions verifyOwnership:
                    var ownershipResult = await new VerifyPackageOwnershipCommand().ExecuteAsync(verifyOwnership, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in ownershipResult.Violations) Console.Error.WriteLine($"[{violation.Code}] type={violation.Type} rule={violation.Rule}");
                    Console.WriteLine(ownershipResult.Success
                        ? $"SP220_PACKAGE_OWNERSHIP_OK types={ownershipResult.BaselineTypes} mode={verifyOwnership.Mode.ToString().ToLowerInvariant()}"
                        : $"SP220_PACKAGE_OWNERSHIP_FAILED code={ExitCodes.OwnershipViolation} violations={ownershipResult.Violations.Count}");
                    return ownershipResult.Success ? ExitCodes.Success : ExitCodes.OwnershipViolation;

                case VerifyReleaseVersionOptions verifyVersion:
                    var versionResult = await new VerifyReleaseVersionCommand().ExecuteAsync(verifyVersion, cancellation.Token).ConfigureAwait(false);
                    foreach (var violation in versionResult.Violations) Console.Error.WriteLine($"[{violation.Code}] package={violation.PackageId} path={violation.Path ?? "-"} rule={violation.Rule}");
                    Console.WriteLine(versionResult.Success ? $"SP220_RELEASE_VERSION_OK version={versionResult.PackageVersion} mode={verifyVersion.Mode.ToString().ToLowerInvariant()}" : $"SP220_RELEASE_VERSION_FAILED code={ExitCodes.ReleaseVersionMismatch} violations={versionResult.Violations.Count}");
                    return versionResult.Success ? ExitCodes.Success : ExitCodes.ReleaseVersionMismatch;

                case ScaffoldPackageOptions scaffold:
                    var scaffoldResult = await new ScaffoldPackageCommand().ExecuteAsync(scaffold, cancellation.Token).ConfigureAwait(false);
                    foreach (var step in scaffoldResult.RequiredSteps) Console.WriteLine($"NEXT {step}");
                    Console.WriteLine($"SP220_SCAFFOLD_OK package={scaffoldResult.PackageId} kind={scaffoldResult.Kind} files={scaffoldResult.Files.Count} dryRun={scaffoldResult.DryRun.ToString().ToLowerInvariant()}");
                    return ExitCodes.Success;

                case ListPackagesOptions list:
                    var packageGraph = await new PackageGraphLoader().LoadAsync(list.RepositoryRoot, "eng/package-graph.json", cancellation.Token).ConfigureAwait(false);
                    foreach (var package in packageGraph.Packages.Where(x => x.Lifecycle == list.Lifecycle)) Console.WriteLine(package.Id);
                    return ExitCodes.Success;

                case RunConsumersCommandOptions consumers:
                    var consumerResults = await new ConsumerScenarioRunner().RunAsync(new(consumers.RepositoryRoot, consumers.Set, consumers.PackageDirectory, consumers.PackageVersion, consumers.ManifestPath, consumers.Category), cancellation.Token).ConfigureAwait(false);
                    foreach (var consumer in consumerResults) Console.WriteLine($"SP220_CONSUMER_OK scenario={consumer.Scenario} durationMs={consumer.DurationMs} dependencies={consumer.ObservedSmartPipeDependencies.Count}");
                    Console.WriteLine($"SP220_CONSUMERS_OK scenarios={consumerResults.Count} set={consumers.Set}");
                    return ExitCodes.Success;

                case PackPackagesOptions pack:
                    var packManifest = await new PackPackagesCommand().ExecuteAsync(pack, cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine($"SP220_PACKAGES_OK packages={packManifest.Packages.Count} version={packManifest.Version} mode={packManifest.Mode}");
                    return ExitCodes.Success;

                default:
                    throw new InvalidOperationException("Unsupported command type.");
            }
        }
        catch (CommandLineException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (RepositoryCheckException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return exception.ExitCode;
        }
        catch (PackageGraphException exception)
        {
            Console.Error.WriteLine($"[{exception.Code}] {exception.Message}");
            return ExitCodes.SchemaOrManifestInvalid;
        }
        catch (ScaffoldException exception)
        {
            Console.Error.WriteLine($"[{exception.Code}] {exception.Message}");
            return ExitCodes.ScaffoldCollisionOrRefusedOverwrite;
        }
        catch (ConsumerScenarioException exception)
        {
            Console.Error.WriteLine($"[{exception.Code}] {exception.Message}");
            return ExitCodes.ConsumerScenarioFailure;
        }
        catch (PackagePackException exception)
        {
            Console.Error.WriteLine($"[{exception.Code}] {exception.Message}");
            return ExitCodes.PackagePackFailure;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation canceled.");
            return ExitCodes.UsageOrConfigurationError;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Unexpected failure: {exception.Message}");
            return ExitCodes.UnexpectedInternalFailure;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
