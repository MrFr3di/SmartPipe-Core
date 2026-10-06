using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.Consumers;

internal sealed record DotNetProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string LogDirectory, TimeSpan Timeout);
internal sealed record DotNetProcessResult(int ExitCode, string StandardOutput, string StandardError, string StandardOutputLog, string StandardErrorLog, string Command, DateTimeOffset StartedUtc, long DurationMs);

internal sealed partial class DotNetProcessRunner
{
    private readonly IProcessRunner _processRunner;
    private readonly int _maximumCapturedCharacters;

    public DotNetProcessRunner(IProcessRunner? processRunner = null, int maximumCapturedCharacters = 64 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCapturedCharacters);
        _processRunner = processRunner ?? new ProcessRunner(maximumRetainedOutputCharacters: maximumCapturedCharacters);
        _maximumCapturedCharacters = maximumCapturedCharacters;
    }

    public async Task<DotNetProcessResult> RunAsync(DotNetProcessRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(request.LogDirectory);
        var startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var result = await _processRunner.RunAsync(new ProcessRequest(request.FileName, request.Arguments, request.Timeout, request.WorkingDirectory, request.LogDirectory), ct).ConfigureAwait(false);
        var stdout = Redact(result.StandardOutput);
        var stderr = Redact(result.StandardError);
        var stdoutLog = result.StandardOutputLog ?? throw new InvalidOperationException("Process runner did not create the stdout spill log.");
        var stderrLog = result.StandardErrorLog ?? throw new InvalidOperationException("Process runner did not create the stderr spill log.");
        var command = Redact(request.FileName + " " + string.Join(' ', request.Arguments.Select(QuoteForDisplay)));
        return new(result.ExitCode, Tail(stdout), Tail(stderr), stdoutLog, stderrLog, command, startedUtc, stopwatch.ElapsedMilliseconds);
    }

    internal static string Redact(string value)
    {
        var redacted = DiagnosticRedactor.Redact(value);
        redacted = UrlSecretRegex().Replace(redacted, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "<redacted-url>";
            return uri.GetLeftPart(UriPartial.Path).Replace(uri.UserInfo + "@", string.IsNullOrEmpty(uri.UserInfo) ? "" : "<redacted>@", StringComparison.Ordinal) + (string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) ? "" : "?<redacted>");
        });
        return CredentialRegex().Replace(redacted, "${key}=<redacted>");
    }

    private string Tail(string value) => value.Length <= _maximumCapturedCharacters ? value : "[output spilled]\n" + value[^_maximumCapturedCharacters..];
    private static string QuoteForDisplay(string value) => value.Any(char.IsWhiteSpace) ? "\"<argument>\"" : value;
    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex UrlSecretRegex();
    [GeneratedRegex(@"(?<key>password|passwd|token|apikey|api_key|secret)\s*=\s*[^\s;]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex CredentialRegex();
}
