using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
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
    private static readonly TimeSpan DefaultProcessHostHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessTerminator _terminator;
    private readonly TimeSpan _terminationObservationTimeout;
    private readonly int _maximumRetainedOutputCharacters;
    private readonly IProcessHostLocator _processHostLocator;
    private readonly TimeSpan _processHostHandshakeTimeout;

    public ProcessRunner(
        IProcessTerminator? terminator = null,
        TimeSpan? terminationObservationTimeout = null,
        int maximumRetainedOutputCharacters = DefaultMaximumRetainedOutputCharacters,
        IProcessHostLocator? processHostLocator = null,
        TimeSpan? processHostHandshakeTimeout = null)
    {
        if (terminationObservationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationObservationTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedOutputCharacters);
        if (processHostHandshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(processHostHandshakeTimeout));
        }

        _terminator = terminator ?? new SystemProcessTerminator();
        _terminationObservationTimeout = terminationObservationTimeout ?? DefaultTerminationObservationTimeout;
        _maximumRetainedOutputCharacters = maximumRetainedOutputCharacters;
        _processHostLocator = processHostLocator ?? new RepositoryCheckProcessHostLocator();
        _processHostHandshakeTimeout = processHostHandshakeTimeout ?? DefaultProcessHostHandshakeTimeout;
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);
        if (cancellationToken.IsCancellationRequested)
        {
            throw new ProcessRunnerException(
                ProcessFailureKind.Canceled,
                "External process was canceled before its process host was started.");
        }

        using var hostSession = new ProcessHostSession(_processHostHandshakeTimeout);
        var hostLaunch = _processHostLocator.Locate();
        using var process = new Process
        {
            StartInfo = CreateStartInfo(
                request,
                hostLaunch,
                hostSession.PipeName,
                hostSession.Nonce),
        };
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
            await hostSession.WaitForReadyAsync(linkedCancellation.Token).ConfigureAwait(false);
            if (!await hostSession.SendStartAndWaitForResultAsync(linkedCancellation.Token).ConfigureAwait(false))
            {
                var startFailureTermination = await TerminateHostAsync(process, hostSession).ConfigureAwait(false);
                if (!await ObserveExitAsync(process).ConfigureAwait(false))
                {
                    throw new ProcessRunnerException(
                        ProcessFailureKind.TerminationFailure,
                        "Target-start failure was reported but the process host could not be terminated.",
                        startFailureTermination ?? new TimeoutException("Process-host termination timed out."));
                }

                await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
                throw new ProcessRunnerException(
                    ProcessFailureKind.StartFailure,
                    "External target process could not be started by the repository-check process host.");
            }

            var targetExitCode = await hostSession.ReadExitCodeAsync(linkedCancellation.Token)
                .ConfigureAwait(false);

            Exception? teardownException = null;
            try
            {
                await hostSession.SendTeardownAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ProcessHostProtocolException)
            {
                teardownException = exception;
            }

            var terminationException = await TerminateHostAsync(process, hostSession).ConfigureAwait(false);
            if (!await ObserveExitAsync(process).ConfigureAwait(false))
            {
                throw new ProcessRunnerException(
                    ProcessFailureKind.TerminationFailure,
                    "Target exited but its owned process group could not be torn down and observed.",
                    terminationException
                    ?? teardownException
                    ?? new TimeoutException("Owned process-group teardown timed out."));
            }

            if (!await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false))
            {
                await CloseInheritedOutputPipesAsync(process).ConfigureAwait(false);
                if (!await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false))
                {
                    throw new ProcessRunnerException(
                        ProcessFailureKind.TerminationFailure,
                        "Owned process group exited but redirected output could not be observed.");
                }
            }

            return new ProcessResult(
                targetExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException exception)
        {
            var failureKind = cancellationToken.IsCancellationRequested
                ? ProcessFailureKind.Canceled
                : ProcessFailureKind.Timeout;
            var terminationException = await TerminateHostAsync(process, hostSession)
                .ConfigureAwait(false);

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
        catch (Exception exception) when (exception is IOException or ProcessHostProtocolException)
        {
            var terminationException = await TerminateHostAsync(process, hostSession)
                .ConfigureAwait(false);
            var exitObserved = await ObserveExitAsync(process).ConfigureAwait(false);
            if (!exitObserved)
            {
                throw new ProcessRunnerException(
                    ProcessFailureKind.TerminationFailure,
                    "Invalid process host could not be terminated and observed.",
                    terminationException ?? exception);
            }

            await ObserveOutputAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw new ProcessRunnerException(
                ProcessFailureKind.StartFailure,
                "Repository-check process host did not complete its authenticated control protocol.",
                exception);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        ProcessRequest request,
        ProcessHostLaunch hostLaunch,
        string pipeName,
        string nonce)
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
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add(nonce);
        startInfo.ArgumentList.Add(request.FileName);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task<Exception?> TerminateHostAsync(
        Process process,
        ProcessHostSession hostSession)
    {
        var terminationException = await hostSession.TrySendCancelAsync().ConfigureAwait(false);

        if (hostSession.StartCommitted || !HasExited(process))
        {
            try
            {
                if (hostSession.StartCommitted)
                {
                    _terminator.Kill(process);
                }
                else
                {
                    process.Kill();
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                terminationException = exception;
            }
        }

        return terminationException;
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
        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        await Task.Yield();
        return null;
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
    private static readonly TimeSpan ControlConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ControlOperationTimeout = TimeSpan.FromSeconds(5);
    private static IDisposable? s_lifetimeOwnership;

    public static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        return await RunAsync(
                arguments,
                new ProcessTreeOwnershipFactory(),
                holdOwnershipForProcessLifetime: true)
            .ConfigureAwait(false);
    }

    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        IProcessTreeOwnershipFactory ownershipFactory,
        bool holdOwnershipForProcessLifetime = false,
        CancellationToken hostLifetimeCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(ownershipFactory);
        if (!TryParseArguments(
                arguments,
                out var pipeName,
                out var nonce,
                out var targetFileName,
                out var targetArguments))
        {
            return InvalidArgumentsExitCode;
        }

        using var control = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using var connectTimeout = new CancellationTokenSource(ControlConnectTimeout);
            await control.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or TimeoutException)
        {
            return InvalidArgumentsExitCode;
        }

        using var initializationCancellation = new CancellationTokenSource();
        Task<IDisposable> ownershipTask;
        try
        {
            ownershipTask = ownershipFactory.InitializeAsync(initializationCancellation.Token).AsTask();
        }
        catch (Exception)
        {
            return InvalidArgumentsExitCode;
        }

        var commandTask = ReadControlAsync(control, nonce, initializationCancellation.Token);
        var firstCompleted = await Task.WhenAny(ownershipTask, commandTask).ConfigureAwait(false);
        if (firstCompleted == commandTask)
        {
            ProcessHostControlMessage earlyCommand;
            try
            {
                earlyCommand = await commandTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException
                                               or OperationCanceledException
                                               or ProcessHostProtocolException)
            {
                initializationCancellation.Cancel();
                return InvalidArgumentsExitCode;
            }

            initializationCancellation.Cancel();
            await ObserveCanceledInitializationAsync(ownershipTask).ConfigureAwait(false);
            return earlyCommand.Kind == ProcessHostControlMessageKind.Cancel && earlyCommand.Detail is null
                ? 0
                : InvalidArgumentsExitCode;
        }

        IDisposable ownership;
        try
        {
            ownership = await ownershipTask.ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Process-tree ownership initialization returned no lifetime resource.");
        }
        catch (Exception)
        {
            initializationCancellation.Cancel();
            return InvalidArgumentsExitCode;
        }

        if (holdOwnershipForProcessLifetime)
        {
            // A Windows kill-on-close job contains this host. Keep its non-inheritable
            // handle rooted until OS process teardown, after Main establishes exit code.
            s_lifetimeOwnership = ownership;
        }

        var startAccepted = false;
        try
        {
            await WriteControlAsync(
                    control,
                    nonce,
                    new ProcessHostControlMessage(ProcessHostControlMessageKind.Ready))
                .ConfigureAwait(false);
            var command = await commandTask.ConfigureAwait(false);
            if (command.Kind == ProcessHostControlMessageKind.Cancel && command.Detail is null)
            {
                return 0;
            }

            if (command.Kind != ProcessHostControlMessageKind.Start || command.Detail is not null)
            {
                return InvalidArgumentsExitCode;
            }

            startAccepted = true;
            return await RunTargetAsync(
                    control,
                    nonce,
                    targetFileName,
                    targetArguments,
                    hostLifetimeCancellation)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or ProcessHostProtocolException)
        {
            if (startAccepted)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, hostLifetimeCancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (hostLifetimeCancellation.IsCancellationRequested)
                {
                }
            }

            return InvalidArgumentsExitCode;
        }
        finally
        {
            if (!holdOwnershipForProcessLifetime)
            {
                ownership.Dispose();
            }
        }
    }

    private static async Task<int> RunTargetAsync(
        Stream control,
        string nonce,
        string targetFileName,
        IReadOnlyList<string> targetArguments,
        CancellationToken hostLifetimeCancellation)
    {
        using var target = new Process
        {
            StartInfo = CreateTargetStartInfo(targetFileName, targetArguments),
        };
        try
        {
            if (!target.Start())
            {
                await TryWriteFailureAsync(control, nonce, "target-start").ConfigureAwait(false);
                return 0;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            await TryWriteFailureAsync(control, nonce, "target-start").ConfigureAwait(false);
            return 0;
        }

        await WriteControlAsync(
                control,
                nonce,
                new ProcessHostControlMessage(ProcessHostControlMessageKind.Started))
            .ConfigureAwait(false);
        var forwardOutput = target.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var forwardError = target.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        await Task.WhenAll(forwardOutput, forwardError).ConfigureAwait(false);
        await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await WriteControlAsync(
                control,
                nonce,
                new ProcessHostControlMessage(
                    ProcessHostControlMessageKind.Exit,
                    target.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ConfigureAwait(false);
        await WaitForMandatoryTeardownAsync(control, nonce, hostLifetimeCancellation).ConfigureAwait(false);
        return InvalidArgumentsExitCode;
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> arguments,
        out string pipeName,
        out string nonce,
        out string targetFileName,
        out IReadOnlyList<string> targetArguments)
    {
        pipeName = string.Empty;
        nonce = string.Empty;
        targetFileName = string.Empty;
        targetArguments = [];
        if (arguments.Count < 4
            || string.IsNullOrWhiteSpace(arguments[0])
            || arguments[0].Length > 128
            || !Guid.TryParseExact(arguments[1], "N", out _)
            || string.IsNullOrWhiteSpace(arguments[2])
            || !string.Equals(arguments[3], "--", StringComparison.Ordinal))
        {
            return false;
        }

        pipeName = arguments[0];
        nonce = arguments[1];
        targetFileName = arguments[2];
        targetArguments = arguments.Skip(4).ToArray();
        return true;
    }

    private static async Task TryWriteFailureAsync(Stream control, string nonce, string safeDetail)
    {
        try
        {
            await WriteControlAsync(
                    control,
                    nonce,
                    new ProcessHostControlMessage(
                        ProcessHostControlMessageKind.StartFailed,
                        safeDetail))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or ProcessHostProtocolException)
        {
            // The controller treats a missing failure frame as a start failure too.
        }
    }

    private static async Task WriteControlAsync(
        Stream control,
        string nonce,
        ProcessHostControlMessage message)
    {
        using var deadline = new CancellationTokenSource(ControlOperationTimeout);
        await ProcessHostControlProtocol.WriteAsync(control, nonce, message, deadline.Token)
            .ConfigureAwait(false);
    }

    private static async Task<ProcessHostControlMessage> ReadControlAsync(
        Stream control,
        string nonce,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(ControlOperationTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        return await ProcessHostControlProtocol.ReadAsync(control, nonce, bounded.Token)
            .ConfigureAwait(false);
    }

    private static async Task ObserveCanceledInitializationAsync(Task<IDisposable> ownershipTask)
    {
        try
        {
            var ownership = await ownershipTask.WaitAsync(ControlOperationTimeout).ConfigureAwait(false);
            ownership?.Dispose();
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }
        catch (Exception)
        {
            // The controller has already canceled startup; no target can be created.
        }
    }

    private static async Task WaitForMandatoryTeardownAsync(
        Stream control,
        string nonce,
        CancellationToken hostLifetimeCancellation)
    {
        try
        {
            var teardown = await ReadControlAsync(control, nonce, CancellationToken.None).ConfigureAwait(false);
            if (teardown.Kind != ProcessHostControlMessageKind.Teardown || teardown.Detail is not null)
            {
                // Invalid teardown never permits this ownership leader to exit normally.
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or OperationCanceledException
                                           or ProcessHostProtocolException)
        {
            // Missing or malformed teardown falls back to controller-owned termination.
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, hostLifetimeCancellation).ConfigureAwait(false);
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
