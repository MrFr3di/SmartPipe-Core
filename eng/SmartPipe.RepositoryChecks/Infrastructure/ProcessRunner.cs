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

internal sealed class ProcessRunner : IProcessRunner
{
    private const int DefaultMaximumRetainedOutputCharacters = 64 * 1024;
    private static readonly TimeSpan DefaultTerminationObservationTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessTerminator _terminator;
    private readonly TimeSpan _terminationObservationTimeout;
    private readonly int _maximumRetainedOutputCharacters;

    public ProcessRunner(
        IProcessTerminator? terminator = null,
        TimeSpan? terminationObservationTimeout = null,
        int maximumRetainedOutputCharacters = DefaultMaximumRetainedOutputCharacters)
    {
        if (terminationObservationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationObservationTimeout));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedOutputCharacters);
        _terminator = terminator ?? new SystemProcessTerminator();
        _terminationObservationTimeout = terminationObservationTimeout ?? DefaultTerminationObservationTimeout;
        _maximumRetainedOutputCharacters = maximumRetainedOutputCharacters;
    }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Timeout, TimeSpan.Zero);

        using var process = new Process { StartInfo = CreateStartInfo(request) };
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
                throw new ProcessRunnerException(
                    ProcessFailureKind.TerminationFailure,
                    "External process output streams did not close within the bounded shutdown interval.",
                    terminationException ?? exception);
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
        return new ProcessResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    internal static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

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
            process.Kill(entireProcessTree: true);
        }
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
