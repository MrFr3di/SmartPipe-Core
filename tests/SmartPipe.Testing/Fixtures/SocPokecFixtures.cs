using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartPipe.Core;

namespace SmartPipe.Testing.Fixtures;

public readonly record struct SocPokecEdge(long SourceId, long TargetId);

public sealed record SocPokecReadSummary(
    long TotalLines,
    long ValidCount,
    long InvalidCount,
    long? MinId,
    long? MaxId,
    string RollingDigest);

public sealed record StressSummary(
    string FixtureId,
    string FixturePath,
    long SizeBytes,
    long ProcessedCount,
    long ValidCount,
    long InvalidCount,
    long FilteredCount,
    long DroppedCount,
    long DeadLetterCount,
    long ElapsedMs,
    double ItemsPerSecond,
    long MaxWorkingSet,
    long MaxGcHeap,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    string FinalPipelineState,
    string OutputPolicy,
    int InputCapacity,
    int? OutputCapacity,
    int MaxConcurrency);

public static class SocPokecFixture
{
    public static bool TryGetHugeFixturePath(out string path, out string reason)
    {
        path = FixtureEnvironment.ConfiguredSocPokecPath ?? "";

        if (!FixtureEnvironment.HugeFixturesEnabled)
        {
            reason = FixtureSkip.HugeFixturesDisabled;
            return false;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "SMARTPIPE_SOC_POKEC_PATH must point to soc-pokec-relationships.txt.";
            return false;
        }

        if (!File.Exists(path))
        {
            reason = $"Configured soc-pokec fixture path does not exist: {path}";
            return false;
        }

        reason = "";
        return true;
    }

    public static async IAsyncEnumerable<SocPokecEdge> ReadEdgesAsync(
        string path,
        Action<string>? invalidLine = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(path);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;

            if (TryParseEdge(line, out var edge))
            {
                yield return edge;
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                invalidLine?.Invoke(line);
            }
        }
    }

    public static async Task<SocPokecReadSummary> AnalyzeAsync(
        string path,
        CancellationToken ct = default)
    {
        using var reader = new StreamReader(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = 0L;
        var valid = 0L;
        var invalid = 0L;
        long? min = null;
        long? max = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                break;

            total++;
            if (!TryParseEdge(line, out var edge))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    invalid++;
                continue;
            }

            valid++;
            min = Min(min, edge.SourceId, edge.TargetId);
            max = Max(max, edge.SourceId, edge.TargetId);
            var digestLine = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{edge.SourceId}->{edge.TargetId}\n"));
            digest.AppendData(digestLine);
        }

        return new SocPokecReadSummary(
            total,
            valid,
            invalid,
            min,
            max,
            Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant());
    }

    public static bool TryParseEdge(string line, out SocPokecEdge edge)
    {
        edge = default;
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        if (!long.TryParse(parts[0], CultureInfo.InvariantCulture, out var sourceId))
            return false;
        if (!long.TryParse(parts[1], CultureInfo.InvariantCulture, out var targetId))
            return false;

        edge = new SocPokecEdge(sourceId, targetId);
        return true;
    }

    public static async Task WriteSummaryAsync(
        string path,
        StressSummary summary,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, summary, cancellationToken: ct).ConfigureAwait(false);
    }

    public static StressSummary CreateSummary(
        string fixtureId,
        string fixturePath,
        long processedCount,
        long validCount,
        long invalidCount,
        Stopwatch stopwatch,
        PipelineRunState finalState,
        PipelineRuntimeOptions options,
        long filteredCount = 0,
        long droppedCount = 0,
        long deadLetterCount = 0)
    {
        var elapsedMs = Math.Max(1, stopwatch.ElapsedMilliseconds);
        return new StressSummary(
            fixtureId,
            fixturePath,
            new FileInfo(fixturePath).Length,
            processedCount,
            validCount,
            invalidCount,
            filteredCount,
            droppedCount,
            deadLetterCount,
            elapsedMs,
            processedCount / (elapsedMs / 1000d),
            Environment.WorkingSet,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            finalState.ToString(),
            options.OutputPolicy.ToString(),
            options.InputCapacity,
            options.OutputCapacity,
            options.MaxConcurrency);
    }

    private static long Min(long? current, long left, long right)
    {
        var value = Math.Min(left, right);
        return current is null ? value : Math.Min(current.Value, value);
    }

    private static long Max(long? current, long left, long right)
    {
        var value = Math.Max(left, right);
        return current is null ? value : Math.Max(current.Value, value);
    }
}
