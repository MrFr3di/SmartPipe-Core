using SmartPipe.RepositoryChecks.Repository;

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

internal sealed record VerifyBaselineOptions(
    string RepositoryRoot,
    string ManifestPath,
    string PackagesDirectory,
    bool Offline) : RepositoryCheckCommand(RepositoryRoot);

internal sealed record VerifySp220ScopeOptions(
    string RepositoryRoot,
    string BaseCommit) : RepositoryCheckCommand(RepositoryRoot);

internal sealed class CommandLineException(string message) : Exception(message);

internal static class CommandLineParser
{
    private static readonly string[] PackageIds =
    [
        "SmartPipe.Core",
        "SmartPipe.Extensions",
        "SmartPipe.Extensions.Json",
    ];
    private static readonly HashSet<string> CaptureOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--repository", "--commit", "--target-release", "--baseline-version",
        "--packages-dir", "--output-dir", "--workflow-evidence",
    };
    private static readonly HashSet<string> VerifyOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--manifest", "--packages-dir", "--offline",
    };
    private static readonly HashSet<string> VerifySp220ScopeOptions = new(StringComparer.Ordinal)
    {
        "--repo-root", "--base-commit",
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
            "verify-sp220-scope" => ParseVerifySp220Scope(args.AsSpan(1)),
            _ => throw new CommandLineException($"Unknown command '{args[0]}'."),
        };
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
        if (offline)
        {
            foreach (var packageId in PackageIds)
            {
                var fileName = $"{packageId}.2.1.2.nupkg";
                if (!File.Exists(Path.Combine(packages, fileName)))
                {
                    throw new CommandLineException($"Offline package is missing: {fileName}.");
                }
            }
        }

        return new VerifyBaselineOptions(root, manifest, packages, offline);
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
