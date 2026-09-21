using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SmartPipe.Core;
using SmartPipe.Extensions.Transforms;

namespace SmartPipe.Perf.JsonStrictAb;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Comparative", "StrictAB", "SP220-08")]
public class JsonStrictAbBenchmarks
{
    private JsonTransform<SmallPayload, SmallPayload>? _sourceGeneratedSmall;
    private JsonTransform<SmallPayload, SmallPayload>? _optionsSmall;
    private JsonTransform<MediumPayload, MediumPayload>? _sourceGeneratedMedium;
    private JsonTransform<MediumPayload, MediumPayload>? _optionsMedium;

    private readonly ProcessingEnvelope<SmallPayload> _smallEnvelope =
        ProcessingEnvelope<SmallPayload>.Create(
            new SmallPayload
            {
                Id = 42,
                Name = "smartpipe",
                Enabled = true,
            },
            "perf-json",
            "run",
            1);

    private readonly ProcessingEnvelope<MediumPayload> _mediumEnvelope =
        ProcessingEnvelope<MediumPayload>.Create(
            new MediumPayload
            {
                Id = 84,
                Name = "smartpipe-json-benchmark",
                Description = new string('x', 512),
                Values = Enumerable.Range(0, 128).ToArray(),
                Tags = ["alpha", "beta", "gamma", "delta"],
            },
            "perf-json",
            "run",
            2);

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _sourceGeneratedSmall = new(
            PerfJsonContext.Default.SmallPayload,
            PerfJsonContext.Default.SmallPayload);
        _optionsSmall = new(CreateOptions());

        _sourceGeneratedMedium = new(
            PerfJsonContext.Default.MediumPayload,
            PerfJsonContext.Default.MediumPayload);
        _optionsMedium = new(CreateOptions());

        await _sourceGeneratedSmall.InitializeAsync().ConfigureAwait(false);
        await _optionsSmall.InitializeAsync().ConfigureAwait(false);
        await _sourceGeneratedMedium.InitializeAsync().ConfigureAwait(false);
        await _optionsMedium.InitializeAsync().ConfigureAwait(false);

        AssertSmall(SourceGeneratedSmallRoundTrip(), nameof(SourceGeneratedSmallRoundTrip));
        AssertSmall(OptionsSmallRoundTrip(), nameof(OptionsSmallRoundTrip));
        AssertMedium(SourceGeneratedMediumRoundTrip(), nameof(SourceGeneratedMediumRoundTrip));
        AssertMedium(OptionsMediumRoundTrip(), nameof(OptionsMediumRoundTrip));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_optionsMedium is not null)
            await _optionsMedium.DisposeAsync().ConfigureAwait(false);
        if (_sourceGeneratedMedium is not null)
            await _sourceGeneratedMedium.DisposeAsync().ConfigureAwait(false);
        if (_optionsSmall is not null)
            await _optionsSmall.DisposeAsync().ConfigureAwait(false);
        if (_sourceGeneratedSmall is not null)
            await _sourceGeneratedSmall.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public StageResult<SmallPayload> SourceGeneratedSmallRoundTrip() =>
        SourceGeneratedSmall.TransformAsync(_smallEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<SmallPayload> OptionsSmallRoundTrip() =>
        OptionsSmall.TransformAsync(_smallEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<MediumPayload> SourceGeneratedMediumRoundTrip() =>
        SourceGeneratedMedium.TransformAsync(_mediumEnvelope).GetAwaiter().GetResult();

    [Benchmark]
    public StageResult<MediumPayload> OptionsMediumRoundTrip() =>
        OptionsMedium.TransformAsync(_mediumEnvelope).GetAwaiter().GetResult();

    private JsonTransform<SmallPayload, SmallPayload> SourceGeneratedSmall =>
        _sourceGeneratedSmall ?? throw new InvalidOperationException("Source-generated small transform is not initialized.");

    private JsonTransform<SmallPayload, SmallPayload> OptionsSmall =>
        _optionsSmall ?? throw new InvalidOperationException("Options small transform is not initialized.");

    private JsonTransform<MediumPayload, MediumPayload> SourceGeneratedMedium =>
        _sourceGeneratedMedium ?? throw new InvalidOperationException("Source-generated medium transform is not initialized.");

    private JsonTransform<MediumPayload, MediumPayload> OptionsMedium =>
        _optionsMedium ?? throw new InvalidOperationException("Options medium transform is not initialized.");

    private static JsonSerializerOptions CreateOptions() =>
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

    private static void AssertSmall(StageResult<SmallPayload> result, string operation)
    {
        SmallPayload? value = result.Value;
        if (!result.IsSuccess ||
            value is null ||
            value.Id != 42 ||
            value.Name != "smartpipe" ||
            !value.Enabled)
        {
            throw new InvalidOperationException($"{operation} strict A/B correctness oracle failed.");
        }
    }

    private static void AssertMedium(StageResult<MediumPayload> result, string operation)
    {
        MediumPayload? value = result.Value;
        if (!result.IsSuccess ||
            value is null ||
            value.Id != 84 ||
            value.Name != "smartpipe-json-benchmark" ||
            value.Description?.Length != 512 ||
            value.Values is null ||
            value.Values.Length != 128 ||
            value.Values[0] != 0 ||
            value.Values[^1] != 127 ||
            value.Tags is null ||
            value.Tags.Count != 4 ||
            value.Tags[0] != "alpha" ||
            value.Tags[^1] != "delta")
        {
            throw new InvalidOperationException($"{operation} strict A/B correctness oracle failed.");
        }
    }
}

public sealed class SmallPayload
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; }
}

public sealed class MediumPayload
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int[]? Values { get; set; }
    public List<string>? Tags { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(SmallPayload))]
[JsonSerializable(typeof(MediumPayload))]
internal partial class PerfJsonContext : JsonSerializerContext;
