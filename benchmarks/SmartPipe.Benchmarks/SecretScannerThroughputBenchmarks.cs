#nullable enable
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;
using SmartPipe.Core.Tests.Infrastructure.TestCases;

namespace SmartPipe.Benchmarks;

/// <summary>
/// BenchmarkDotNet benchmarks for SecretScanner throughput validation.
/// Measures processing speed for large benign inputs (1MB, 5MB, 10MB).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(iterationCount: 10, warmupCount: 2)]
public class SecretScannerThroughputBenchmarks
{
    private string? _1MbLoremIpsum;
    private string? _5MbLoremIpsum;
    private string? _10MbLoremIpsum;
    private string? _1MbJson;
    private string? _5MbJson;
    private string? _10MbJson;

    /// <summary>
    /// Setup method called before benchmarks run.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _1MbLoremIpsum = LargeBenignInputs.Generate1MbLoremIpsum();
        _5MbLoremIpsum = LargeBenignInputs.Generate5MbLoremIpsum();
        _10MbLoremIpsum = LargeBenignInputs.Generate10MbLoremIpsum();
        _1MbJson = LargeBenignInputs.Generate1MbJson();
        _5MbJson = LargeBenignInputs.Generate5MbJson();
        _10MbJson = LargeBenignInputs.Generate10MbJson();
    }

    /// <summary>
    /// Benchmark for 1MB Lorem Ipsum input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_1MB_LoremIpsum()
    {
        return SecretScanner.HasSecrets(_1MbLoremIpsum!);
    }

    /// <summary>
    /// Benchmark for 5MB Lorem Ipsum input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_5MB_LoremIpsum()
    {
        return SecretScanner.HasSecrets(_5MbLoremIpsum!);
    }

    /// <summary>
    /// Benchmark for 10MB Lorem Ipsum input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_10MB_LoremIpsum()
    {
        return SecretScanner.HasSecrets(_10MbLoremIpsum!);
    }

    /// <summary>
    /// Benchmark for 1MB JSON input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_1MB_Json()
    {
        return SecretScanner.HasSecrets(_1MbJson!);
    }

    /// <summary>
    /// Benchmark for 5MB JSON input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_5MB_Json()
    {
        return SecretScanner.HasSecrets(_5MbJson!);
    }

    /// <summary>
    /// Benchmark for 10MB JSON input using HasSecrets.
    /// </summary>
    [Benchmark]
    public bool HasSecrets_10MB_Json()
    {
        return SecretScanner.HasSecrets(_10MbJson!);
    }

    /// <summary>
    /// Benchmark for 1MB Lorem Ipsum input using Redact.
    /// </summary>
    [Benchmark]
    public string? Redact_1MB_LoremIpsum()
    {
        return SecretScanner.Redact(_1MbLoremIpsum!);
    }

    /// <summary>
    /// Benchmark for 5MB Lorem Ipsum input using Redact.
    /// </summary>
    [Benchmark]
    public string? Redact_5MB_LoremIpsum()
    {
        return SecretScanner.Redact(_5MbLoremIpsum!);
    }

    /// <summary>
    /// Benchmark for 10MB Lorem Ipsum input using Redact.
    /// </summary>
    [Benchmark]
    public string? Redact_10MB_LoremIpsum()
    {
        return SecretScanner.Redact(_10MbLoremIpsum!);
    }
}
