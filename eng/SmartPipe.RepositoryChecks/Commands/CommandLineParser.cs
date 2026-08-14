using SmartPipe.RepositoryChecks.Repository;
using SmartPipe.RepositoryChecks.Packaging;
using SmartPipe.RepositoryChecks.PackageGraph;
using SmartPipe.RepositoryChecks.Ownership;
using SmartPipe.RepositoryChecks.Consumers;

namespace SmartPipe.RepositoryChecks.Commands;

internal abstract record RepositoryCheckCommand(string RepositoryRoot);

internal sealed record CaptureBaselineOptions(
    string RepositoryRoot,
    string Repository,
    string Commit,
    string TargetRelease,
    string BaselineVersion,
    string PackagesDirectory,
    string OutputDirectory,
    string WorkflowEvidencePath) : RepositoryCheckCommand(RepositoryRoot);

internal enum BaselineVerificationMode
{
    Full,
    Integrity,
}

internal sealed record VerifyBaselineOptions(
    string RepositoryRoot,
    string ManifestPath,
    string PackagesDirectory,
    bool Offline,
    BaselineVerificationMode Mode = BaselineVerificationMode.Full) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record ProvisionBaselineOptions(
    string RepositoryRoot,
    string ManifestPath,
    string PackagesDirectory) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record VerifySp220ScopeOptions(
    string RepositoryRoot,
    string BaseCommit) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record VerifyCentralPackagesOptions(
    string RepositoryRoot,
    CentralPackageValidationMode Mode) : RepositoryCheckCommand(RepositoryRoot);

internal enum ProfileOutputFormat
{
    Text,
    Jsonl,
    GitHub,
}

internal sealed record VerifyProfileOptions(
    string RepositoryRoot,
    string Profile,
    ProfileOutputFormat Format,
    bool FailuresOnly) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record AgentContextOptions(
    string RepositoryRoot,
    string Epic,
    string Task,
    ProfileOutputFormat Format) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record VerifyTaskOptions(
    string RepositoryRoot,
    string Epic,
    string Task,
    ProfileOutputFormat Format,
    bool FailuresOnly) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record AgentEvidenceOptions(
    string RepositoryRoot,
    string Epic,
    ProfileOutputFormat Format) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record VerifyPackageProjectsOptions(
    string RepositoryRoot) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyLockFilesOptions(string RepositoryRoot) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyNuGetAuditOptions(string RepositoryRoot, string ReportPath) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyPackageGraphOptions(string RepositoryRoot, string GraphPath, PackageGraphMode Mode, string? PackagesDirectory, bool SourceOnly) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record CanonicalizeJsonOptions(string RepositoryRoot, string InputPath, bool Check) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyPackageMetadataOptions(string RepositoryRoot, string GraphPath, string PackageDirectory, PackageGraphMode Mode, string? ReportPath) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyPackageOwnershipOptions(string RepositoryRoot, string BaselineDirectory, string PackageDirectory, PackageGraphMode Mode) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record VerifyReleaseVersionOptions(string RepositoryRoot, string Tag, PackageGraphMode Mode, string PackageDirectory) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record ScaffoldPackageOptions(string RepositoryRoot, string PackageId, bool DryRun, string? OutputReport) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record ListPackagesOptions(string RepositoryRoot, PackageLifecycle Lifecycle) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record RunConsumersCommandOptions(
    string RepositoryRoot,
    string Set,
    string PackageDirectory,
    string PackageVersion,
    string ManifestPath,
    string? Category) : RepositoryCheckCommand(RepositoryRoot);
internal sealed record PackPackagesOptions(string RepositoryRoot, PackageGraphMode Mode, string Configuration, string PackageVersion, string OutputDirectory, string ManifestPath) : RepositoryCheckCommand(RepositoryRoot);

internal sealed class CommandLineException(string message) : Exception(message);

internal static class CommandLineParser
{
    private static readonly HashSet<string> CaptureOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--repository", "--commit", "--target-release", "--baseline-version",
        "--packages-dir", "--output-dir", "--workflow-evidence",
    };
    private static readonly HashSet<string> VerifyOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--manifest", "--packages-dir", "--offline", "--mode",
    };
    private static readonly HashSet<string> ProvisionOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--manifest", "--packages-dir",
    };
    private static readonly HashSet<string> VerifySp220ScopeOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--base-commit",
    };
    private static readonly HashSet<string> VerifyCentralPackagesOptions = new(StringComparer.Ordinal)
    {
        "--repository-root", "--repo-root", "--mode",
    };
    private static readonly HashSet<string> VerifyPackageProjectsOptionNames = new(StringComparer.Ordinal)
    {
        "--repository-root", "--repo-root",
    };
    private static readonly HashSet<string> VerifyProfileOptionNames = new(StringComparer.Ordinal)
    {
        "--repo-root", "--profile", "--format", "--failures-only",
    };

    public static RepositoryCheckCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0)
        {
            throw new CommandLineException("A command is required.");
        }

        return args[0] switch
        {
            "capture-baseline" => ParseCapture(args.AsSpan(1)),
            "verify-baseline" => ParseVerify(args.AsSpan(1)),
            "provision-baseline" => ParseProvision(args.AsSpan(1)),
            "verify" => ParseVerifyProfile(args.AsSpan(1)),
            "agent-context" => ParseAgentContext(args.AsSpan(1)),
            "verify-task" => ParseVerifyTask(args.AsSpan(1)),
            "evidence" => ParseAgentEvidence(args.AsSpan(1)),
            "verify-sp220-scope" => ParseVerifySp220Scope(args.AsSpan(1)),
            "verify-central-packages" => ParseVerifyCentralPackages(args.AsSpan(1)),
            "verify-package-projects" => ParseVerifyPackageProjects(args.AsSpan(1)),
            "verify-lock-files" => ParseVerifyLockFiles(args.AsSpan(1)),
            "verify-nuget-audit" => ParseVerifyNuGetAudit(args.AsSpan(1)),
            "verify-package-graph" => ParseVerifyPackageGraph(args.AsSpan(1)),
            "canonicalize-json" => ParseCanonicalizeJson(args.AsSpan(1)),
            "verify-package-metadata" => ParseVerifyPackageMetadata(args.AsSpan(1)),
            "verify-package-ownership" => ParseVerifyPackageOwnership(args.AsSpan(1)),
            "verify-release-version" => ParseVerifyReleaseVersion(args.AsSpan(1)),
            "scaffold-package" => ParseScaffoldPackage(args.AsSpan(1)),
            "list-packages" => ParseListPackages(args.AsSpan(1)),
            "run-consumers" => ParseRunConsumers(args.AsSpan(1)),
            "pack-packages" => ParsePackPackages(args.AsSpan(1)),
            _ => throw new CommandLineException($"Unknown command '{args[0]}'."),
        };
    }

    private static PackPackagesOptions ParsePackPackages(ReadOnlySpan<string> args)
    {
        string? root = null; string? configuration = null; string? version = null; string? output = null; string? manifest = null; PackageGraphMode? mode = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new CommandLineException($"Option '{args[i]}' requires a value.");
            if (!seen.Add(args[i])) throw new CommandLineException($"Duplicate option '{args[i]}'.");
            switch (args[i])
            {
                case "--repo-root" or "--repository-root": root = args[i + 1]; break;
                case "--mode" when Enum.TryParse<PackageGraphMode>(args[i + 1], true, out var parsed): mode = parsed; break;
                case "--mode": throw new CommandLineException("Option '--mode' must be 'current' or 'release'.");
                case "--configuration": configuration = args[i + 1]; break;
                case "--package-version": version = args[i + 1]; break;
                case "--output": output = args[i + 1]; break;
                case "--manifest": manifest = args[i + 1]; break;
                default: throw new CommandLineException($"Unknown pack-packages option '{args[i]}'.");
            }
        }
        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root) || mode is null || configuration is not ("Release" or "Debug") || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(manifest))
            throw new CommandLineException("pack-packages requires valid '--mode', '--configuration', '--package-version', '--output', and '--manifest'.");
        return new(root, mode.Value, configuration, version, ResolveWithinRoot(root, output, "--output"), ResolveWithinRoot(root, manifest, "--manifest"));
    }

    private static RunConsumersCommandOptions ParseRunConsumers(ReadOnlySpan<string> args)
    {
        string? root = null; string? set = null; string? packages = null; string? version = null; string manifest = "eng/consumer-scenarios.json"; string? category = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new CommandLineException($"Option '{args[i]}' requires a value.");
            if (!seen.Add(args[i])) throw new CommandLineException($"Duplicate option '{args[i]}'.");
            switch (args[i])
            {
                case "--repo-root" or "--repository-root": root = args[i + 1]; break;
                case "--set": set = args[i + 1]; break;
                case "--package-directory": packages = args[i + 1]; break;
                case "--package-version": version = args[i + 1]; break;
                case "--manifest": manifest = args[i + 1]; break;
                case "--category": category = args[i + 1]; break;
                default: throw new CommandLineException($"Unknown run-consumers option '{args[i]}'.");
            }
        }
        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root)) throw new CommandLineException($"Repository root does not exist: {root}.");
        if (set != "current") throw new CommandLineException("Option '--set' must be 'current'.");
        if (string.IsNullOrWhiteSpace(packages) || string.IsNullOrWhiteSpace(version)) throw new CommandLineException("run-consumers requires '--package-directory' and '--package-version'.");
        var resolvedManifest = ResolveWithinRoot(root, manifest, "--manifest");
        if (category is not null
            && (category.Length == 0
                || category.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
            throw new CommandLineException("Option '--category' must contain lowercase letters, digits, or hyphens.");
        return new(root, set, ResolveWithinRoot(root, packages, "--package-directory"), version, Path.GetRelativePath(root, resolvedManifest).Replace('\\', '/'), category);
    }

    private static ScaffoldPackageOptions ParseScaffoldPackage(ReadOnlySpan<string> args)
    {
        string? id = null; string? root = null; string? report = null; var dryRun = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (!seen.Add(option)) throw new CommandLineException($"Duplicate option '{option}'.");
            if (option == "--dry-run") { dryRun = true; continue; }
            if (++i >= args.Length) throw new CommandLineException($"Option '{option}' requires a value.");
            switch (option)
            {
                case "--id": id = args[i]; break;
                case "--repo-root" or "--repository-root": root = args[i]; break;
                case "--output-report": report = args[i]; break;
                default: throw new CommandLineException($"Unknown scaffold-package option '{option}'.");
            }
        }
        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root)) throw new CommandLineException($"Repository root does not exist: {root}.");
        if (string.IsNullOrWhiteSpace(id)) throw new CommandLineException("Missing required option '--id'.");
        return new(root, id, dryRun, report is null ? null : ResolveWithinRoot(root, report, "--output-report"));
    }

    private static ListPackagesOptions ParseListPackages(ReadOnlySpan<string> args)
    {
        string? root = null; PackageLifecycle? lifecycle = null;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new CommandLineException($"Option '{args[i]}' requires a value.");
            switch (args[i])
            {
                case "--repo-root" or "--repository-root": root = args[i + 1]; break;
                case "--lifecycle" when Enum.TryParse<PackageLifecycle>(args[i + 1], true, out var parsed): lifecycle = parsed; break;
                case "--lifecycle": throw new CommandLineException("Option '--lifecycle' must be 'active', 'planned', or 'compatibility-facade'.");
                default: throw new CommandLineException($"Unknown list-packages option '{args[i]}'.");
            }
        }
        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root)) throw new CommandLineException($"Repository root does not exist: {root}.");
        return new(root, lifecycle ?? throw new CommandLineException("Missing required option '--lifecycle'."));
    }

    private static VerifyReleaseVersionOptions ParseVerifyReleaseVersion(ReadOnlySpan<string> args)
    {
        string? tag = null; string? packages = null; var mode = PackageGraphMode.Current;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new CommandLineException($"Option '{args[i]}' requires a value.");
            switch (args[i]) { case "--tag": tag = args[i + 1]; break; case "--package-directory" or "--packages": packages = args[i + 1]; break; case "--mode" when Enum.TryParse<PackageGraphMode>(args[i + 1], true, out var parsed): mode = parsed; break; default: throw new CommandLineException($"Unknown release-version option '{args[i]}'."); }
        }
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (tag is null || packages is null) throw new CommandLineException("Release version requires '--tag' and '--package-directory'.");
        return new(root, tag, mode, ResolveWithinRoot(root, packages, "--package-directory"));
    }

    private static VerifyPackageOwnershipOptions ParseVerifyPackageOwnership(ReadOnlySpan<string> args)
    {
        string? baseline = null; string? packages = null; var mode = PackageGraphMode.Current;
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new CommandLineException($"Option '{args[i]}' requires a value.");
            switch (args[i]) { case "--baseline": baseline = args[i + 1]; break; case "--packages": packages = args[i + 1]; break; case "--mode" when Enum.TryParse<PackageGraphMode>(args[i + 1], true, out var parsed): mode = parsed; break; default: throw new CommandLineException($"Unknown ownership option '{args[i]}'."); }
        }
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        if (baseline is null || packages is null) throw new CommandLineException("Ownership requires '--baseline' and '--packages'.");
        return new(root, ResolveWithinRoot(root, baseline, "--baseline"), ResolveWithinRoot(root, packages, "--packages"), mode);
    }

    private static VerifyPackageMetadataOptions ParseVerifyPackageMetadata(ReadOnlySpan<string> args)
    {
        string? root = null; string graph = "eng/package-graph.json"; string? packages = null; string? report = null; var mode = PackageGraphMode.Current;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i]; if (!seen.Add(option)) throw new CommandLineException($"Duplicate option '{option}'.");
            if (++i >= args.Length) throw new CommandLineException($"Option '{option}' requires a value.");
            var value = args[i];
            switch (option)
            {
                case "--repo-root" or "--repository-root": root = value; break;
                case "--graph": graph = value; break;
                case "--package-directory" or "--packages": packages = value; break;
                case "--report": report = value; break;
                case "--mode" when Enum.TryParse<PackageGraphMode>(value, true, out var parsed): mode = parsed; break;
                case "--mode": throw new CommandLineException("Option '--mode' must be 'current' or 'release'.");
                default: throw new CommandLineException($"Unknown option '{option}'.");
            }
        }
        root ??= Directory.GetCurrentDirectory(); root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new CommandLineException($"Repository root does not exist: {root}.");
        if (string.IsNullOrWhiteSpace(packages)) throw new CommandLineException("Missing required option '--package-directory'.");
        return new(root, ResolveWithinRoot(root, graph, "--graph"), ResolveWithinRoot(root, packages, "--package-directory"), mode,
            report is null ? null : ResolveWithinRoot(root, report, "--report"));
    }

    private static CanonicalizeJsonOptions ParseCanonicalizeJson(ReadOnlySpan<string> args)
    {
        string? input = null; var check = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--check") { if (check) throw new CommandLineException("Duplicate option '--check'."); check = true; continue; }
            if (args[i] != "--input" || ++i >= args.Length) throw new CommandLineException($"Unknown or incomplete canonicalize-json option '{args[Math.Min(i, args.Length - 1)]}'.");
            input = args[i];
        }
        if (string.IsNullOrWhiteSpace(input)) throw new CommandLineException("Missing required option '--input'.");
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        return new(root, ResolveWithinRoot(root, input, "--input"), check);
    }

    private static VerifyPackageGraphOptions ParseVerifyPackageGraph(ReadOnlySpan<string> args)
    {
        string? root = null; string graph = "eng/package-graph.json"; string? packages = null; var sourceOnly = false; var mode = PackageGraphMode.Current;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i]; if (!seen.Add(option)) throw new CommandLineException($"Duplicate option '{option}'.");
            if (option == "--source-only") { sourceOnly = true; continue; }
            if (++i >= args.Length) throw new CommandLineException($"Option '{option}' requires a value.");
            var value = args[i];
            switch (option)
            {
                case "--repo-root" or "--repository-root": root = value; break;
                case "--graph": graph = value; break;
                case "--packages": packages = value; break;
                case "--mode" when Enum.TryParse<PackageGraphMode>(value, true, out var parsed): mode = parsed; break;
                case "--mode": throw new CommandLineException("Option '--mode' must be 'current' or 'release'.");
                default: throw new CommandLineException($"Unknown option '{option}'.");
            }
        }
        root ??= Directory.GetCurrentDirectory(); root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new CommandLineException($"Repository root does not exist: {root}.");
        if (sourceOnly == (packages is not null)) throw new CommandLineException("Specify exactly one of '--source-only' or '--packages'.");
        return new(root, ResolveWithinRoot(root, graph, "--graph"), mode, packages is null ? null : ResolveWithinRoot(root, packages, "--packages"), sourceOnly);
    }

    private static VerifyPackageProjectsOptions ParseVerifyPackageProjects(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, VerifyPackageProjectsOptionNames);
        var root = values.TryGetValue("--repository-root", out var repositoryRoot)
            ? repositoryRoot
            : values.GetValueOrDefault("--repo-root");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new CommandLineException("Missing required option '--repository-root'.");
        }

        return new VerifyPackageProjectsOptions(RequireRoot(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["--repo-root"] = root,
        }));
    }

    private static VerifyLockFilesOptions ParseVerifyLockFiles(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, new HashSet<string>(["--repository-root", "--repo-root"], StringComparer.Ordinal));
        var root = values.TryGetValue("--repository-root", out var repositoryRoot)
            ? repositoryRoot
            : values.GetValueOrDefault("--repo-root");
        if (string.IsNullOrWhiteSpace(root))
            throw new CommandLineException("Missing required option '--repository-root'.");
        return new VerifyLockFilesOptions(RequireRoot(new Dictionary<string, string?>(StringComparer.Ordinal) { ["--repo-root"] = root }));
    }

    private static VerifyNuGetAuditOptions ParseVerifyNuGetAudit(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, new HashSet<string>(["--repository-root", "--repo-root", "--report"], StringComparer.Ordinal));
        var root = values.TryGetValue("--repository-root", out var repositoryRoot)
            ? repositoryRoot
            : values.GetValueOrDefault("--repo-root");
        if (string.IsNullOrWhiteSpace(root))
            throw new CommandLineException("Missing required option '--repository-root'.");

        var resolvedRoot = RequireRoot(new Dictionary<string, string?>(StringComparer.Ordinal) { ["--repo-root"] = root });
        return new VerifyNuGetAuditOptions(resolvedRoot, ResolveWithinRoot(resolvedRoot, Require(values, "--report"), "--report"));
    }

    private static VerifyCentralPackagesOptions ParseVerifyCentralPackages(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, VerifyCentralPackagesOptions);
        var root = values.TryGetValue("--repository-root", out var repositoryRoot)
            ? repositoryRoot
            : values.GetValueOrDefault("--repo-root");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new CommandLineException("Missing required option '--repository-root'.");
        }

        var mode = values.GetValueOrDefault("--mode") ?? "current";
        if (!Enum.TryParse<CentralPackageValidationMode>(mode, ignoreCase: true, out var parsedMode))
        {
            throw new CommandLineException("Option '--mode' must be 'current' or 'release'.");
        }

        return new VerifyCentralPackagesOptions(RequireRoot(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["--repo-root"] = root,
        }), parsedMode);
    }

    private static VerifySp220ScopeOptions ParseVerifySp220Scope(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, VerifySp220ScopeOptions);
        var commit = Require(values, "--base-commit");
        RequireLowercaseSha(commit, "--base-commit");
        return new VerifySp220ScopeOptions(RequireRoot(values), commit);
    }

    private static CaptureBaselineOptions ParseCapture(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, CaptureOptions);
        var root = RequireRoot(values);
        var output = Require(values, "--output-dir");
        _ = ResolveWithinRoot(root, output, "--output-dir");
        var commit = Require(values, "--commit");
        RequireLowercaseSha(commit, "--commit");
        var targetRelease = Require(values, "--target-release");
        var baselineVersion = Require(values, "--baseline-version");
        RequireVersion(targetRelease, "--target-release");
        RequireVersion(baselineVersion, "--baseline-version");

        return new CaptureBaselineOptions(
            root,
            Require(values, "--repository"),
            commit,
            targetRelease,
            baselineVersion,
            ResolveContainedInput(root, Require(values, "--packages-dir"), "--packages-dir"),
            output.Replace('\\', '/'),
            ResolveContainedInput(root, Require(values, "--workflow-evidence"), "--workflow-evidence"));
    }

    private static VerifyBaselineOptions ParseVerify(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, VerifyOptions);
        var root = RequireRoot(values);
        var manifest = ResolveContainedInput(root, Require(values, "--manifest"), "--manifest");
        var packages = ResolveContainedInput(root, Require(values, "--packages-dir"), "--packages-dir");
        var offline = values.ContainsKey("--offline");
        var modeName = values.GetValueOrDefault("--mode") ?? "full";
        if (!Enum.TryParse<BaselineVerificationMode>(modeName, ignoreCase: true, out var mode))
        {
            throw new CommandLineException("Option '--mode' must be 'full' or 'integrity'.");
        }
        return new VerifyBaselineOptions(root, manifest, packages, offline, mode);
    }

    private static ProvisionBaselineOptions ParseProvision(ReadOnlySpan<string> args)
    {
        var values = ParseOptions(args, ProvisionOptions);
        var root = RequireRoot(values);
        return new ProvisionBaselineOptions(
            root,
            ResolveContainedInput(root, Require(values, "--manifest"), "--manifest"),
            ResolveContainedInput(root, Require(values, "--packages-dir"), "--packages-dir"));
    }

    private static VerifyProfileOptions ParseVerifyProfile(ReadOnlySpan<string> args)
    {
        string? root = null;
        string? profile = null;
        var format = ProfileOutputFormat.Text;
        var failuresOnly = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!VerifyProfileOptionNames.Contains(option))
            {
                throw new CommandLineException($"Unknown option '{option}'.");
            }

            if (!seen.Add(option))
            {
                throw new CommandLineException($"Duplicate option '{option}'.");
            }

            if (option == "--failures-only")
            {
                failuresOnly = true;
                continue;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option '{option}' requires a value.");
            }

            var value = args[index];
            switch (option)
            {
                case "--repo-root":
                    root = value;
                    break;
                case "--profile":
                    profile = value;
                    break;
                case "--format":
                    format = value switch
                    {
                        "text" => ProfileOutputFormat.Text,
                        "jsonl" => ProfileOutputFormat.Jsonl,
                        "github" => ProfileOutputFormat.GitHub,
                        _ => throw new CommandLineException("Option '--format' must be 'text', 'jsonl', or 'github'."),
                    };
                    break;
                default:
                    throw new CommandLineException($"Unknown option '{option}'.");
            }
        }

        root = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            throw new CommandLineException($"Repository root does not exist: {root}.");
        }

        if (string.IsNullOrWhiteSpace(profile))
        {
            throw new CommandLineException("Missing required option '--profile'.");
        }

        return new VerifyProfileOptions(root, profile, format, failuresOnly);
    }

    private static AgentContextOptions ParseAgentContext(ReadOnlySpan<string> args)
    {
        var values = ParseAgentOptions(args, allowTask: true, allowFailuresOnly: false, formatRequired: true, jsonOnly: true);
        return new AgentContextOptions(
            RequireOptionalRoot(values.GetValueOrDefault("--repo-root")),
            RequireAgentEpic(Require(values, "--epic")),
            RequireAgentTask(Require(values, "--task")),
            ProfileOutputFormat.Jsonl);
    }

    private static VerifyTaskOptions ParseVerifyTask(ReadOnlySpan<string> args)
    {
        var values = ParseAgentOptions(args, allowTask: true, allowFailuresOnly: true, formatRequired: false, jsonOnly: false);
        return new VerifyTaskOptions(
            RequireOptionalRoot(values.GetValueOrDefault("--repo-root")),
            RequireAgentEpic(Require(values, "--epic")),
            RequireAgentTask(Require(values, "--task")),
            ParseProfileFormat(values.GetValueOrDefault("--format") ?? "text"),
            values.ContainsKey("--failures-only"));
    }

    private static AgentEvidenceOptions ParseAgentEvidence(ReadOnlySpan<string> args)
    {
        var values = ParseAgentOptions(args, allowTask: false, allowFailuresOnly: false, formatRequired: true, jsonOnly: true);
        return new AgentEvidenceOptions(
            RequireOptionalRoot(values.GetValueOrDefault("--repo-root")),
            RequireAgentEpic(Require(values, "--epic")),
            ProfileOutputFormat.Jsonl);
    }

    private static Dictionary<string, string?> ParseAgentOptions(
        ReadOnlySpan<string> args,
        bool allowTask,
        bool allowFailuresOnly,
        bool formatRequired,
        bool jsonOnly)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--epic", "--repo-root", "--format" };
        if (allowTask) allowed.Add("--task");
        if (allowFailuresOnly) allowed.Add("--failures-only");
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!allowed.Contains(option))
            {
                throw new CommandLineException($"Unknown option '{option}'.");
            }

            if (!values.TryAdd(option, null))
            {
                throw new CommandLineException($"Duplicate option '{option}'.");
            }

            if (option == "--failures-only")
            {
                continue;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option '{option}' requires a value.");
            }

            var value = args[index];
            if (option == "--format")
            {
                if (jsonOnly && value != "json")
                {
                    throw new CommandLineException("Option '--format' must be 'json'.");
                }

                if (!jsonOnly && value is not ("text" or "jsonl" or "github"))
                {
                    throw new CommandLineException("Option '--format' must be 'text', 'jsonl', or 'github'.");
                }
            }

            values[option] = value;
        }

        if (!values.ContainsKey("--epic"))
        {
            throw new CommandLineException("Missing required option '--epic'.");
        }

        if (allowTask && !values.ContainsKey("--task"))
        {
            throw new CommandLineException("Missing required option '--task'.");
        }

        if (formatRequired && !values.ContainsKey("--format"))
        {
            throw new CommandLineException("Missing required option '--format'.");
        }

        return values;
    }

    private static ProfileOutputFormat ParseProfileFormat(string value) => value switch
    {
        "text" => ProfileOutputFormat.Text,
        "jsonl" => ProfileOutputFormat.Jsonl,
        "github" => ProfileOutputFormat.GitHub,
        _ => throw new CommandLineException("Option '--format' must be 'text', 'jsonl', or 'github'."),
    };

    private static string RequireOptionalRoot(string? value)
    {
        var root = Path.GetFullPath(value ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(root))
        {
            throw new CommandLineException("Repository root does not exist.");
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string RequireAgentEpic(string value) =>
        value == "SP220-05"
            ? value
            : throw new CommandLineException("Option '--epic' must use the canonical 'SP220-05' identity.");

    private static string RequireAgentTask(string value)
    {
        if (value.Length < 2 || value[0] != 'T' || value[1] is not (>= '1' and <= '9') || !value[2..].All(char.IsAsciiDigit))
        {
            throw new CommandLineException("Option '--task' must use a canonical Txx identity.");
        }

        return value;
    }

    private static Dictionary<string, string?> ParseOptions(ReadOnlySpan<string> args, IReadOnlySet<string> allowed)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Unexpected argument '{option}'.");
            }

            if (!allowed.Contains(option))
            {
                throw new CommandLineException($"Unknown option '{option}'.");
            }

            if (!values.TryAdd(option, null))
            {
                throw new CommandLineException($"Duplicate option '{option}'.");
            }

            if (option == "--offline")
            {
                continue;
            }

            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Option '{option}' requires a value.");
            }

            values[option] = args[index];
        }

        return values;
    }

    private static string RequireRoot(Dictionary<string, string?> values)
    {
        var root = Path.GetFullPath(Require(values, "--repo-root"));
        if (!Directory.Exists(root))
        {
            throw new CommandLineException($"Repository root does not exist: {root}.");
        }

        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Require(Dictionary<string, string?> values, string option) =>
        values.TryGetValue(option, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CommandLineException($"Missing required option '{option}'.");

    private static string ResolvePath(string root, string path) =>
        Path.GetFullPath(path, root);

    private static string ResolveWithinRoot(string root, string path, string option)
    {
        var resolved = ResolvePath(root, path);
        var relative = Path.GetRelativePath(root, resolved);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new CommandLineException($"Option '{option}' must resolve inside the repository.");
        }

        return resolved;
    }

    private static string ResolveContainedInput(string root, string path, string option)
    {
        var resolved = ResolveWithinRoot(root, path, option);
        try
        {
            _ = RepositoryPaths.NormalizeContainedFullPath(root, resolved, option);
            return resolved;
        }
        catch (InvalidDataException exception)
        {
            throw new CommandLineException(exception.Message);
        }
    }

    private static void RequireLowercaseSha(string value, string option)
    {
        if (value.Length != 40 || value.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new CommandLineException($"Option '{option}' must be 40 lowercase hexadecimal characters.");
        }
    }

    private static void RequireVersion(string value, string option)
    {
        var parts = value.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
        {
            throw new CommandLineException($"Option '{option}' must be a three-part numeric version.");
        }
    }
}
