using System.ComponentModel;
using System.Diagnostics;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_DrainsLargeOutputWithoutDeadlock_AndBoundsRetainedText()
    {
        const int maximumRetainedCharacters = 4096;
        var runner = new ProcessRunner(maximumRetainedOutputCharacters: maximumRetainedCharacters);

        var result = await runner.RunAsync(
            CreateFixtureRequest("pressure", TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.InRange(result.StandardOutput.Length, 1, maximumRetainedCharacters + 64);
        Assert.InRange(result.StandardError.Length, 1, maximumRetainedCharacters + 64);
        Assert.Contains("output truncated", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("output truncated", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("STDOUT-END", result.StandardOutput, StringComparison.Ordinal);
        Assert.EndsWith("STDERR-END", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RedactsOversizedUnterminatedQueryBeforeRetention()
    {
        var runner = new ProcessRunner(maximumRetainedOutputCharacters: 4096);

        var result = await runner.RunAsync(
            CreateFixtureRequest("long-query", TimeSpan.FromSeconds(10)),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain("super-secret-token", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("oversized output line redacted", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputCollector_RedactsQuerySplitAcrossReaderChunksBeforeRetention()
    {
        var reader = new ChunkedTextReader(
            "prefix https://example.test/package?to",
            "ken=split-secret",
            "\nnext-line");
        var collector = new BoundedRedactingOutputCollector(4096);

        var result = await collector.CollectAsync(reader);

        Assert.DoesNotContain("split-secret", result, StringComparison.Ordinal);
        Assert.Contains("https://example.test/package?<redacted>", result, StringComparison.Ordinal);
        Assert.EndsWith("next-line", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_BoundsNormalExitPipeObservation_AndDoesNotLeakDescendant()
    {
        var processIdPath = Path.Combine(Path.GetTempPath(), $"smartpipe-descendant-{Guid.NewGuid():N}.pid");
        int? descendantProcessId = null;
        try
        {
            var runner = new ProcessRunner(terminationObservationTimeout: TimeSpan.FromMilliseconds(250));

            var exception = await Assert.ThrowsAsync<ProcessRunnerException>(
                () => runner.RunAsync(
                    CreateFixtureRequest(
                        ["spawn-descendant", processIdPath],
                        TimeSpan.FromSeconds(10)),
                    TestContext.Current.CancellationToken));

            Assert.Equal(ProcessFailureKind.TerminationFailure, exception.FailureKind);
            descendantProcessId = int.Parse(
                await File.ReadAllTextAsync(processIdPath, TestContext.Current.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(IsProcessRunning(descendantProcessId.Value));
        }
        finally
        {
            await KillFixtureIfRunningAsync(descendantProcessId);
            File.Delete(processIdPath);
        }
    }

    [Fact]
    public async Task RunAsync_TimesOutBlockedChild_AndObservesItsExit()
    {
        var terminator = new RecordingTerminator();
        var runner = new ProcessRunner(terminator, TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<ProcessRunnerException>(
            () => runner.RunAsync(
                CreateFixtureRequest("wait", TimeSpan.FromMilliseconds(250)),
                TestContext.Current.CancellationToken));

        Assert.Equal(ProcessFailureKind.Timeout, exception.FailureKind);
        Assert.NotNull(terminator.ProcessId);
        Assert.False(IsProcessRunning(terminator.ProcessId.Value));
    }

    [Fact]
    public async Task RunAsync_CancelsBlockedChild_KillsItAndObservesItsExit()
    {
        var terminator = new RecordingTerminator();
        var runner = new ProcessRunner(terminator, TimeSpan.FromSeconds(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var runTask = runner.RunAsync(
            CreateFixtureRequest("wait", TimeSpan.FromSeconds(30)),
            cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<ProcessRunnerException>(() => runTask);

        Assert.Equal(ProcessFailureKind.Canceled, exception.FailureKind);
        Assert.NotNull(terminator.ProcessId);
        Assert.False(IsProcessRunning(terminator.ProcessId.Value));
    }

    [Fact]
    public async Task RunAsync_ClassifiesTerminationFailureWithoutWaitingIndefinitely()
    {
        var terminator = new ThrowingTerminator(killBeforeThrow: false);
        var runner = new ProcessRunner(terminator, TimeSpan.FromMilliseconds(250));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var runTask = runner.RunAsync(
                CreateFixtureRequest("wait", TimeSpan.FromSeconds(30)),
                cancellation.Token);
            cancellation.Cancel();
            var exception = await Assert.ThrowsAsync<ProcessRunnerException>(() => runTask);

            Assert.Equal(ProcessFailureKind.TerminationFailure, exception.FailureKind);
            Assert.NotNull(terminator.ProcessId);
            Assert.True(IsProcessRunning(terminator.ProcessId.Value));
        }
        finally
        {
            await KillFixtureIfRunningAsync(terminator.ProcessId);
        }
    }

    [Fact]
    public async Task RunAsync_TreatsKillExceptionAfterExitAsCancellationRace()
    {
        var terminator = new ThrowingTerminator(killBeforeThrow: true);
        var runner = new ProcessRunner(terminator, TimeSpan.FromSeconds(2));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var runTask = runner.RunAsync(
            CreateFixtureRequest("wait", TimeSpan.FromSeconds(30)),
            cancellation.Token);
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<ProcessRunnerException>(() => runTask);

        Assert.Equal(ProcessFailureKind.Canceled, exception.FailureKind);
        Assert.NotNull(terminator.ProcessId);
        Assert.False(IsProcessRunning(terminator.ProcessId.Value));
    }

    [Fact]
    public void DiagnosticRedactor_RedactsHomePathVariantsAndFileUri()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        Assert.NotEmpty(home);
        var slashNormalizedHome = home.Replace('\\', '/');
        var homeWithTrailingSeparator = home + Path.DirectorySeparatorChar;
        var homeFileUri = new Uri(homeWithTrailingSeparator).AbsoluteUri.TrimEnd('/');
        var diagnostic = $"{home}|{slashNormalizedHome}|{homeWithTrailingSeparator}|{homeFileUri}";

        var result = DiagnosticRedactor.Redact(diagnostic);

        Assert.DoesNotContain(home, result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(slashNormalizedHome, result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(homeFileUri, result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, result.Split("<home>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void DiagnosticRedactor_DoesNotRedactSiblingWithHomePrefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        var sibling = home + "2" + Path.DirectorySeparatorChar + "diagnostic.txt";

        var result = DiagnosticRedactor.Redact(sibling);

        Assert.Equal(sibling, result);
    }

    private static ProcessRequest CreateFixtureRequest(string mode, TimeSpan timeout)
    {
        return CreateFixtureRequest([mode], timeout);
    }

    private static ProcessRequest CreateFixtureRequest(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        return new ProcessRequest(GetFixtureExecutablePath(), arguments, timeout);
    }

    private static string GetFixtureExecutablePath()
    {
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testOutputDirectory.Parent!.Name;
        var repositoryRoot = testOutputDirectory.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var executableName = "SmartPipe.RepositoryChecks.ProcessFixture"
            + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        return Path.Combine(
            repositoryRoot,
            "tests",
            "SmartPipe.RepositoryChecks.ProcessFixture",
            "bin",
            configuration,
            "net10.0",
            executableName);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task KillFixtureIfRunningAsync(int? processId)
    {
        if (!processId.HasValue)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
        catch (ArgumentException)
        {
            // The process already exited.
        }
    }

    private sealed class RecordingTerminator : IProcessTerminator
    {
        public int? ProcessId { get; private set; }

        public void Kill(Process process)
        {
            ProcessId = process.Id;
            process.Kill(entireProcessTree: true);
        }
    }

    private sealed class ThrowingTerminator(bool killBeforeThrow) : IProcessTerminator
    {
        public int? ProcessId { get; private set; }

        public void Kill(Process process)
        {
            ProcessId = process.Id;
            if (killBeforeThrow)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new Win32Exception("Synthetic termination failure.");
        }
    }

    private sealed class ChunkedTextReader(params string[] chunks) : TextReader
    {
        private int _index;

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = chunks[_index++];
            chunk.AsSpan().CopyTo(buffer.Span);
            return ValueTask.FromResult(chunk.Length);
        }
    }
}
