using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Perf.CsvStrictAb;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Comparative", "StrictAB", "SP220-09")]
public class CsvStrictAbBenchmarks
{
    private CsvTransform<SmallRecord, SmallRecord>? _small;
    private CsvTransform<MediumRecord, MediumRecord>? _medium;

    private readonly ProcessingEnvelope<SmallRecord> _smallEnvelope =
        ProcessingEnvelope<SmallRecord>.Create(
            new SmallRecord
            {
                Id = 42,
                Name = "smartpipe",
                Enabled = true,
            },
            "perf-csv",
            "run",
            1);

    private readonly ProcessingEnvelope<MediumRecord> _mediumEnvelope =
        ProcessingEnvelope<MediumRecord>.Create(
            new MediumRecord
            {
                Id = 84,
                Name = "smartpipe-csv-benchmark",
                Description = new string('x', 256),
                Count = 123456,
                Price = 1234.5678m,
                Ratio = 0.875,
                Enabled = true,
                Category = "pipeline",
                Region = "eu",
                Sequence = 9876543210L,
            },
            "perf-csv",
            "run",
            2);

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _small = new CsvTransform<SmallRecord, SmallRecord>();
        _medium = new CsvTransform<MediumRecord, MediumRecord>();

        await _small.InitializeAsync().ConfigureAwait(false);
        await _medium.InitializeAsync().ConfigureAwait(false);

        AssertSmall(SmallRoundTrip());
        AssertMedium(MediumRoundTrip());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_medium is not null)
            await _medium.DisposeAsync().ConfigureAwait(false);
        if (_small is not null)
            await _small.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public StageResult<SmallRecord> SmallRoundTrip() =>
        Small.TransformAsync(_smallEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<MediumRecord> MediumRoundTrip() =>
        Medium.TransformAsync(_mediumEnvelope).GetAwaiter().GetResult();

    private CsvTransform<SmallRecord, SmallRecord> Small =>
        _small ?? throw new InvalidOperationException("Small CSV transform is not initialized.");

    private CsvTransform<MediumRecord, MediumRecord> Medium =>
        _medium ?? throw new InvalidOperationException("Medium CSV transform is not initialized.");

    private static void AssertSmall(StageResult<SmallRecord> result)
    {
        SmallRecord? value = result.Value;
        if (!result.IsSuccess ||
            value is null ||
            value.Id != 42 ||
            value.Name != "smartpipe" ||
            !value.Enabled)
        {
            throw new InvalidOperationException("Small CSV strict A/B correctness oracle failed.");
        }
    }

    private static void AssertMedium(StageResult<MediumRecord> result)
    {
        MediumRecord? value = result.Value;
        if (!result.IsSuccess ||
            value is null ||
            value.Id != 84 ||
            value.Name != "smartpipe-csv-benchmark" ||
            value.Description?.Length != 256 ||
            value.Count != 123456 ||
            value.Price != 1234.5678m ||
            Math.Abs(value.Ratio - 0.875) > 0.000001 ||
            !value.Enabled ||
            value.Category != "pipeline" ||
            value.Region != "eu" ||
            value.Sequence != 9876543210L)
        {
            throw new InvalidOperationException("Medium CSV strict A/B correctness oracle failed.");
        }
    }
}

public sealed class SmallRecord
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}

public sealed class MediumRecord
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int Count { get; set; }
    public decimal Price { get; set; }
    public double Ratio { get; set; }
    public bool Enabled { get; set; }
    public string? Category { get; set; }
    public string? Region { get; set; }
    public long Sequence { get; set; }
}
