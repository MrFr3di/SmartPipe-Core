using System.Security.Cryptography;
using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.Repository;

namespace SmartPipe.RepositoryChecks.Agent;

internal sealed record AgentRepositoryState(
    string Head,
    string Branch,
    bool Clean,
    IReadOnlyList<string> ChangedPaths,
    string TreeFingerprint);

internal sealed class AgentRepositoryStateReader
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;
    private readonly string _gitPath;

    public AgentRepositoryStateReader(IProcessRunner? processRunner = null, string gitPath = "git")
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _gitPath = gitPath;
    }

    public async Task<AgentRepositoryState> CaptureAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        var head = await RunGitAsync(root, ["rev-parse", "--verify", "HEAD"], cancellationToken).ConfigureAwait(false);
        var branch = await RunGitAsync(root, ["branch", "--show-current"], cancellationToken).ConfigureAwait(false);
        var status = await RunGitAsync(root, ["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);

        var headSha = head.StandardOutput.Trim();
        if (headSha.Length != 40 || headSha.Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git returned an invalid HEAD.");
        }

        var currentBranch = branch.StandardOutput.Trim();
        if (currentBranch.Length == 0)
        {
            currentBranch = "HEAD";
        }

        if (currentBranch.Contains('\r') || currentBranch.Contains('\n'))
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git returned an invalid branch name.");
        }

        var entries = ParseStatus(root, status.StandardOutput);
        var changedPaths = entries
            .Select(static entry => entry.Path)
            .Distinct(RepositoryPaths.FileSystemPathComparer)
            .OrderBy(static path => path, RepositoryPaths.FileSystemPathComparer)
            .ToArray();
        var fingerprint = await FingerprintAsync(root, entries, cancellationToken).ConfigureAwait(false);
        return new(headSha, currentBranch, changedPaths.Length == 0, changedPaths, fingerprint);
    }

    private async Task<ProcessResult> RunGitAsync(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest(_gitPath, ["-C", root, .. arguments], GitTimeout), cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("git state capture was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git state capture failed.", exception);
        }

        if (result.ExitCode != 0)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git state capture failed.");
        }

        return result;
    }

    private static IReadOnlyList<GitStatusEntry> ParseStatus(string root, string output)
    {
        var entries = new List<GitStatusEntry>();
        foreach (var item in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length < 3 || item[2] != ' ')
            {
                throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git returned malformed porcelain status.");
            }

            var status = item[..2];
            if (status.Contains('R') || status.Contains('C'))
            {
                throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git rename and copy status is unsupported for exact-tree evidence.");
            }

            var path = item[3..].Replace('\\', '/');
            if (path.Contains('\r') || path.Contains('\n'))
            {
                throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git returned a changed path containing a line break.");
            }

            try
            {
                _ = RepositoryPaths.ResolveWithinRoot(root, path, "changed path");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new RepositoryCheckException(ExitCodes.RepositorySnapshotMismatch, "git reported a changed path outside the repository.", exception);
            }

            entries.Add(new GitStatusEntry(status, path));
        }

        return entries;
    }

    private static async Task<string> FingerprintAsync(
        string root,
        IReadOnlyList<GitStatusEntry> entries,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries
                     .OrderBy(static item => item.Path, RepositoryPaths.FileSystemPathComparer)
                     .ThenBy(static item => item.Status, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Status));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData([0]);

            var fullPath = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("<deleted>"));
            }
            else
            {
                if ((File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
                {
                    throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git reported a changed directory instead of a file.");
                }

                await using var stream = new FileStream(
                    fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }

            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record GitStatusEntry(string Status, string Path);
}

internal sealed record AgentPrerequisiteResult(
    string Epic,
    string Commit,
    string Status);

internal sealed class AgentPrerequisiteReader
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);
    private readonly IProcessRunner _processRunner;
    private readonly string _gitPath;

    public AgentPrerequisiteReader(IProcessRunner? processRunner = null, string gitPath = "git")
    {
        _processRunner = processRunner ?? new ProcessRunner();
        _gitPath = gitPath;
    }

    public async Task<AgentPrerequisiteResult> ReadAsync(
        string repositoryRoot,
        AgentPrerequisiteDefinition prerequisite,
        CancellationToken cancellationToken)
    {
        var root = RepositoryPaths.NormalizeRoot(repositoryRoot);
        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessRequest(_gitPath, ["-C", root, "merge-base", "--is-ancestor", prerequisite.Commit, "HEAD"], GitTimeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessRunnerException exception) when (exception.FailureKind == ProcessFailureKind.Canceled)
        {
            throw new OperationCanceledException("git prerequisite check was canceled.", exception, cancellationToken);
        }
        catch (ProcessRunnerException exception)
        {
            throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git prerequisite check failed.", exception);
        }

        return result.ExitCode switch
        {
            0 => new(prerequisite.Epic, prerequisite.Commit, "satisfied"),
            1 => new(prerequisite.Epic, prerequisite.Commit, "unsatisfied"),
            _ => throw new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, "git prerequisite check failed."),
        };
    }
}
