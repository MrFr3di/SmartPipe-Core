using System.Text.Json.Serialization;

namespace SmartPipe.RepositoryChecks.Reporting;

internal sealed record CheckDiagnostic
{
    public CheckDiagnostic(string code, string summary, string? path = null, int? line = null, string? evidencePath = null)
    {
        Code = code;
        Summary = summary;
        Path = path;
        Line = line;
        EvidencePath = evidencePath;
    }

    public string Code { get; }
    public string Summary { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Line { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidencePath { get; }
}

internal sealed record CheckRun
{
    public CheckRun(
        string check,
        string? profile,
        bool success,
        int exitCode,
        IReadOnlyList<CheckDiagnostic> diagnostics,
        IReadOnlyDictionary<string, int>? counters = null)
    {
        Check = check;
        Profile = profile;
        Success = success;
        ExitCode = exitCode;
        Diagnostics = diagnostics;
        Counters = counters;
    }

    public int SchemaVersion { get; init; } = CheckRunNormalizer.SchemaVersion;
    public string Check { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; init; }
    public bool Success { get; }
    public int ExitCode { get; }
    public IReadOnlyList<CheckDiagnostic> Diagnostics { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, int>? Counters { get; init; }
}

internal static class CheckRunNormalizer
{
    public const int SchemaVersion = 1;
    public const int MaxDiagnostics = 100;

    private const int MaxIdentityLength = 256;
    private const int MaxCodeLength = 128;
    private const int MaxSummaryLength = 1_024;
    private const int MaxPathLength = 512;
    private const int MaxCounterCount = 100;

    public static CheckRun Normalize(CheckRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SchemaVersion != SchemaVersion)
        {
            throw new ArgumentException($"Unsupported diagnostic schema version: {run.SchemaVersion}.", nameof(run));
        }

        var check = SingleLine(run.Check, nameof(run.Check), MaxIdentityLength);
        var profile = OptionalSingleLine(run.Profile, nameof(run.Profile), MaxIdentityLength);
        ArgumentNullException.ThrowIfNull(run.Diagnostics);

        var diagnostics = run.Success
            ? []
            : run.Diagnostics
                .Select(NormalizeDiagnostic)
                .OrderBy(static item => item.Code, StringComparer.Ordinal)
                .ThenBy(static item => item.Path, StringComparer.Ordinal)
                .ThenBy(static item => item.Line ?? int.MinValue)
                .ThenBy(static item => item.Summary, StringComparer.Ordinal)
                .ThenBy(static item => item.EvidencePath, StringComparer.Ordinal)
                .Take(MaxDiagnostics)
                .ToArray();

        IReadOnlyDictionary<string, int>? counters = null;
        if (run.Counters is not null)
        {
            if (run.Counters.Count > MaxCounterCount)
            {
                throw new ArgumentException($"At most {MaxCounterCount} counters are supported.", nameof(run));
            }

            var sorted = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in run.Counters)
            {
                var name = SingleLine(pair.Key, "counter name", MaxIdentityLength);
                if (pair.Value < 0)
                {
                    throw new ArgumentException("Counter values must be non-negative.", nameof(run));
                }

                if (!sorted.TryAdd(name, pair.Value))
                {
                    throw new ArgumentException($"Duplicate counter name: {name}.", nameof(run));
                }
            }

            counters = sorted;
        }

        return run with
        {
            Check = check,
            Profile = profile,
            Diagnostics = diagnostics,
            Counters = counters,
        };
    }

    private static CheckDiagnostic NormalizeDiagnostic(CheckDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var code = SingleLine(diagnostic.Code, nameof(diagnostic.Code), MaxCodeLength);
        var summary = SingleLine(diagnostic.Summary, nameof(diagnostic.Summary), MaxSummaryLength);
        var path = RelativePath(diagnostic.Path, nameof(diagnostic.Path));
        var evidencePath = RelativePath(diagnostic.EvidencePath, nameof(diagnostic.EvidencePath));
        if (diagnostic.Line is <= 0)
        {
            throw new ArgumentException("Diagnostic line must be positive.", nameof(diagnostic));
        }

        return new CheckDiagnostic(code, summary, path, diagnostic.Line, evidencePath);
    }

    private static string SingleLine(string value, string parameterName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maxLength || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException($"{parameterName} must be a bounded single line.", parameterName);
        }

        return value;
    }

    private static string? OptionalSingleLine(string? value, string parameterName, int maxLength) =>
        value is null ? null : SingleLine(value, parameterName, maxLength);

    private static string? RelativePath(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        var path = SingleLine(value, parameterName, MaxPathLength).Replace('\\', '/');
        if (Path.IsPathRooted(value) || path.StartsWith("/", StringComparison.Ordinal) || path.Contains(':'))
        {
            throw new ArgumentException($"{parameterName} must be repository-relative.", parameterName);
        }

        var segments = path.Split('/');
        if (segments.Any(static segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException($"{parameterName} must be a normalized repository-relative path.", parameterName);
        }

        return path;
    }
}
