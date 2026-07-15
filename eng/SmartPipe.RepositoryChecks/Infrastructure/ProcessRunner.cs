using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SmartPipe.RepositoryChecks.Infrastructure;

internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal enum ProcessFailureKind
{
    StartFailure,
    Timeout,
    Canceled,
    TerminationFailure,
}

internal sealed class ProcessRunnerException : Exception
{
    public ProcessRunnerException(ProcessFailureKind failureKind, string message)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public ProcessRunnerException(ProcessFailureKind failureKind, string message, Exception innerException)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public ProcessFailureKind FailureKind { get; }
}

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

internal interface IProcessTerminator
{
    void Kill(Process process);
}

internal sealed record ProcessHostLaunch(string FileName, IReadOnlyList<string> PrefixArguments);

internal interface IProcessHostLocator
{
    ProcessHostLaunch Locate();
}

internal sealed class ProcessRunner : IProcessRunner
{
    private const int DefaultMaximumRetainedOutputCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultTerminationObservationTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessTerminator _terminator;
    private readonly TimeSpan _terminationObservationTimeout;
    private readonly int _maximumRetainedOutputCharacters;
    private readonly IProcessHostLocator _processHostLocator;

    public ProcessRunner(
        IProcessTerminator? terminator = null,
        TimeSpan? terminationObservationTimeout = null,
        int maximumRetainedOutputCharacters = DefaultMaximumRetainedOutputCharacters,
        IProcessHostLocator? processHostLocator = null)
    {
        if (terminationObservationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationObservationTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedOutputCharacters);
        _terminator = terminator ?? new SystemProcessTerminator();
        _terminationObservationTimeout = terminationObservationTimeout ?? DefaultTerminationObservationTimeout;
        _maximumRetainedOutputCharacters = maximumRetainedOutputCharacters;
        _processHostLocator = processHostLocator ?? new RepositoryCheckProcessHostLocator();
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);

        var startupToken = Guid.NewGuid().ToString("N");
        var hostLaunch = _processHostLocator.Locate();
        using var process = new Process { StartInfo = CreateStartInfo(request, hostLaunch, startupToken) };
        try
        {
            if (!process.Start())
            {
                throw new ProcessRunnerException(
                    ProcessFailureKind.StartFailure,
                    "External process could not be started.");
            }
        }
        catch (ProcessRunnerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new ProcessRunnerException(
                ProcessFailureKind.StartFailure,
                "External process could not be started.",
                exception);
        }

        var standardOutputTask = new BoundedRedactingOutputCollector(_maximumRetainedOutputCharacters)
            .CollectAsync(process.StandardOutput);
        var standardErrorTask = new BoundedRedactingOutputCollector(_maximumRetainedOutputCharacters)
            .CollectAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            var failureKind = cancellationToken.IsCancellationRequested
                ? ProcessFailureKind.Canceled
                : ProcessFailureKind.Timeout;
            Exception? terminationException = null;
            if (!HasExited(process))
            {
                try
                {
                    _terminator.Kill(process);
                }
                catch (Exception killException) when (killException is Win32Exception or InvalidOperationException)
                {
                    terminationException = killException;
                }
            }

            var exitObserved = await ObserveExitAsync(process).ConfigureAwait(false);
            if (!exitObserved)
            {
                throw new ProcessRunnerException(
                    ProcessFailureKind.TerminationFailure,
                    "External process could not be terminated and observed within the bounded shutdown interval.",
                    terminationException ?? exception);
            }

            var outputObserved = await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            if (!outputObserved)
            {
                await CloseInheritedOutputPipesAsync(process).ConfigureAwait(false);
                await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            }

            throw new ProcessRunnerException(
                failureKind,
                failureKind == ProcessFailureKind.Timeout
                    ? "External process timed out."
                    : "External process was canceled.",
                exception);
        }

        var normalOutputObserved = await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        if (!normalOutputObserved)
        {
            var terminationException = await CloseInheritedOutputPipesAsync(process).ConfigureAwait(false);
            await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw new ProcessRunnerException(
                ProcessFailureKind.TerminationFailure,
                "External process exited but inherited output pipes did not close within the bounded observation interval.",
                terminationException ?? new TimeoutException("Redirected output observation timed out."));
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode == RepositoryCheckProcessHost.TargetStartFailureExitCode
            && standardError.Contains(
                RepositoryCheckProcessHost.CreateTargetStartFailureMarker(startupToken),
                StringComparison.Ordinal))
        {
            throw new ProcessRunnerException(
                ProcessFailureKind.StartFailure,
                "External target process could not be started by the repository-check process host.");
        }

        if (process.ExitCode == RepositoryCheckProcessHost.OwnershipInitializationFailureExitCode
            && standardError.Contains(
                RepositoryCheckProcessHost.CreateOwnershipInitializationFailureMarker(startupToken),
                StringComparison.Ordinal))
        {
            throw new ProcessRunnerException(
                ProcessFailureKind.StartFailure,
                "Repository-check process-tree ownership could not be initialized.");
        }

        return new ProcessResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    internal static ProcessStartInfo CreateStartInfo(
        ProcessRequest request,
        ProcessHostLaunch hostLaunch,
        string startupToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = hostLaunch.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var prefixArgument in hostLaunch.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefixArgument);
        }

        startInfo.ArgumentList.Add(RepositoryCheckProcessHost.DispatchArgument);
        startInfo.ArgumentList.Add(startupToken);
        startInfo.ArgumentList.Add(request.FileName);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async Task<bool> ObserveExitAsync(Process process)
    {
        using var observation = new CancellationTokenSource(_terminationObservationTimeout);
        try
        {
            await process.WaitForExitAsync(observation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (observation.IsCancellationRequested)
        {
            return HasExited(process);
        }
        catch (InvalidOperationException)
        {
            return HasExited(process);
        }
    }

    private async Task<bool> ObserveOutputAsync(Task<string> standardOutput, Task<string> standardError)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(_terminationObservationTimeout)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<Exception?> CloseInheritedOutputPipesAsync(Process process)
    {
        Exception? terminationException = null;
        try
        {
            _terminator.Kill(process);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            terminationException = exception;
        }

        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        await Task.Yield();
        return terminationException;
    }

    private sealed class SystemProcessTerminator : IProcessTerminator
    {
        public void Kill(Process process)
        {
            ProcessTreeTerminator.Kill(process);
        }
    }
}

internal sealed class RepositoryCheckProcessHostLocator : IProcessHostLocator
{
    public ProcessHostLaunch Locate()
    {
        var assemblyPath = typeof(ProcessRunner).Assembly.Location;
        var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
        var appHostName = Path.GetFileNameWithoutExtension(assemblyPath)
            + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        var appHostPath = Path.Combine(assemblyDirectory, appHostName);
        if (File.Exists(appHostPath))
        {
            return new ProcessHostLaunch(appHostPath, []);
        }

        return new ProcessHostLaunch(LocateDotnetHost(), [assemblyPath]);
    }

    private static string LocateDotnetHost()
    {
        var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredHost))
        {
            return configuredHost;
        }

        var currentProcessPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentProcessPath)
            && string.Equals(
                Path.GetFileNameWithoutExtension(currentProcessPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            return currentProcessPath;
        }

        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            var rootedHost = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(rootedHost))
            {
                return rootedHost;
            }
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}

internal static class RepositoryCheckProcessHost
{
    public const string DispatchArgument = "--internal-process-host";
    public const int InvalidArgumentsExitCode = 64;
    public const int TargetStartFailureExitCode = 125;
    public const int OwnershipInitializationFailureExitCode = 126;

    private const string TargetStartFailurePrefix = "__SMARTPIPE_TARGET_START_FAILURE__";
    private const string OwnershipInitializationFailurePrefix = "__SMARTPIPE_OWNERSHIP_INITIALIZATION_FAILURE__";
    private static readonly object OwnershipInitializationLock = new();
    private static IDisposable? s_lifetimeOwnership;

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        return await RunAsync(
                arguments,
                new ProcessTreeOwnershipFactory(),
                Console.Error)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        IProcessTreeOwnershipFactory ownershipFactory,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(ownershipFactory);
        ArgumentNullException.ThrowIfNull(error);
        if (!TryParseArguments(arguments, out var startupToken, out var targetFileName, out var targetArguments))
        {
            return InvalidArgumentsExitCode;
        }

        try
        {
            lock (OwnershipInitializationLock)
            {
                if (s_lifetimeOwnership is not null)
                {
                    throw new InvalidOperationException(
                        "Process-tree ownership has already been initialized for this process host.");
                }

                // Do not dispose this resource during normal execution. On Windows the
                // host itself is a member of the kill-on-close job, so the OS must close
                // the last handle only after Main has established the host exit code.
                s_lifetimeOwnership = ownershipFactory.Initialize()
                    ?? throw new InvalidOperationException(
                        "Process-tree ownership initialization returned no lifetime resource.");
            }
        }
        catch (Exception)
        {
            await error.WriteLineAsync(CreateOwnershipInitializationFailureMarker(startupToken))
                .ConfigureAwait(false);
            return OwnershipInitializationFailureExitCode;
        }

        return await RunTargetAsync(startupToken, targetFileName, targetArguments, error)
            .ConfigureAwait(false);
    }

    private static async Task<int> RunTargetAsync(
        string startupToken,
        string targetFileName,
        IReadOnlyList<string> targetArguments,
        TextWriter error)
    {
        using var target = new Process
        {
            StartInfo = CreateTargetStartInfo(targetFileName, targetArguments),
        };
        try
        {
            if (!target.Start())
            {
                await error.WriteLineAsync(CreateTargetStartFailureMarker(startupToken)).ConfigureAwait(false);
                return TargetStartFailureExitCode;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await error.WriteLineAsync(CreateTargetStartFailureMarker(startupToken)).ConfigureAwait(false);
            return TargetStartFailureExitCode;
        }

        var forwardOutput = target.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var forwardError = target.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await Task.WhenAll(forwardOutput, forwardError).ConfigureAwait(false);
        await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return target.ExitCode;
    }

    public static string CreateTargetStartFailureMarker(string startupToken)
    {
        return TargetStartFailurePrefix + startupToken;
    }

    public static string CreateOwnershipInitializationFailureMarker(string startupToken)
    {
        return OwnershipInitializationFailurePrefix + startupToken;
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out string startupToken,
        out string targetFileName,
        out IReadOnlyList<string> targetArguments)
    {
        startupToken = string.Empty;
        targetFileName = string.Empty;
        targetArguments = [];
        if (arguments.Count < 3
            || !Guid.TryParseExact(arguments[0], "N", out _)
            || string.IsNullOrWhiteSpace(arguments[1])
            || !string.Equals(arguments[2], "--", StringComparison.Ordinal))
        {
            return false;
        }

        startupToken = arguments[0];
        targetFileName = arguments[1];
        targetArguments = arguments.Skip(3).ToArray();
        return true;
    }

    private static ProcessStartInfo CreateTargetStartInfo(
        string targetFileName,
        IReadOnlyList<string> targetArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = targetFileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in targetArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

internal sealed class BoundedRedactingOutputCollector
{
    private const string TruncatedMarker = "[output truncated]\n";
    private const string OversizedLineMarker = "[oversized output line redacted]";

    private readonly int _maximumRetainedCharacters;

    public BoundedRedactingOutputCollector(int maximumRetainedCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedCharacters);
        _maximumRetainedCharacters = maximumRetainedCharacters;
    }

    public async Task<string> CollectAsync(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var retained = new StringBuilder(_maximumRetainedCharacters);
        var pendingLine = new StringBuilder(Math.Min(_maximumRetainedCharacters, 4096));
        var buffer = new char[4096];
        var outputTruncated = false;
        var oversizedLine = false;
        int charactersRead;
        while ((charactersRead = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            for (var index = 0; index < charactersRead; index++)
            {
                var character = buffer[index];
                if (oversizedLine)
                {
                    if (character == '\n')
                    {
                        AppendBounded(retained, OversizedLineMarker + "\n", ref outputTruncated);
                        oversizedLine = false;
                    }

                    continue;
                }

                if (character == '\n')
                {
                    AppendBounded(
                        retained,
                        DiagnosticRedactor.Redact(pendingLine.ToString()),
                        ref outputTruncated);
                    AppendBounded(retained, "\n", ref outputTruncated);
                    pendingLine.Clear();
                }
                else if (pendingLine.Length == _maximumRetainedCharacters)
                {
                    pendingLine.Clear();
                    oversizedLine = true;
                }
                else
                {
                    pendingLine.Append(character);
                }
            }
        }

        if (oversizedLine)
        {
            AppendBounded(retained, OversizedLineMarker, ref outputTruncated);
        }
        else if (pendingLine.Length > 0)
        {
            AppendBounded(
                retained,
                DiagnosticRedactor.Redact(pendingLine.ToString()),
                ref outputTruncated);
        }

        return outputTruncated ? TruncatedMarker + retained : retained.ToString();
    }

    private void AppendBounded(StringBuilder retained, string value, ref bool outputTruncated)
    {
        retained.Append(value);
        if (retained.Length > _maximumRetainedCharacters)
        {
            retained.Remove(0, retained.Length - _maximumRetainedCharacters);
            outputTruncated = true;
        }
    }
}

internal static partial class DiagnosticRedactor
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = value;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(home))
        {
            var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                home,
                home.Replace('\\', '/'),
                home.Replace('/', '\\'),
            };
            if (Uri.TryCreate(home + Path.DirectorySeparatorChar, UriKind.Absolute, out var homeUri))
            {
                variants.Add(homeUri.AbsoluteUri.TrimEnd('/'));
            }

            foreach (var variant in variants.OrderByDescending(static item => item.Length))
            {
                redacted = ReplaceHomeVariantAtPathBoundaries(redacted, variant);
            }
        }

        return UriQueryRegex().Replace(redacted, "${url}?<redacted>");
    }

    [GeneratedRegex(@"(?<url>https?://[^\s?]+)\?[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriQueryRegex();

    private static string ReplaceHomeVariantAtPathBoundaries(string value, string variant)
    {
        const string replacement = "<home>";
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var match = value.IndexOf(variant, searchStart, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                break;
            }

            var boundaryIndex = match + variant.Length;
            if (boundaryIndex == value.Length || IsPathBoundary(value[boundaryIndex]))
            {
                value = string.Concat(value.AsSpan(0, match), replacement, value.AsSpan(boundaryIndex));
                searchStart = match + replacement.Length;
            }
            else
            {
                searchStart = boundaryIndex;
            }
        }

        return value;
    }

    private static bool IsPathBoundary(char character)
    {
        return !char.IsLetterOrDigit(character) && character is not '_' and not '-' and not '.';
    }
}
