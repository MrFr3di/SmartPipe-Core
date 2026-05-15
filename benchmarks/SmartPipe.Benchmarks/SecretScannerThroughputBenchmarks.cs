#nullable enable
using BenchmarkDotNet.Attributes;
using SmartPipe.Core;

namespace SmartPipe.Benchmarks;

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

    [GlobalSetup]
    public void Setup()
    {
        _1MbLoremIpsum = GenerateLoremIpsum(1_000_000);
        _5MbLoremIpsum = GenerateLoremIpsum(5_000_000);
        _10MbLoremIpsum = GenerateLoremIpsum(10_000_000);
        _1MbJson = GenerateJson(1_000_000);
        _5MbJson = GenerateJson(5_000_000);
        _10MbJson = GenerateJson(10_000_000);
    }

    private static string GenerateLoremIpsum(int size)
    {
        var lorem = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. ";
        var sb = new System.Text.StringBuilder(size);
        while (sb.Length < size)
            sb.Append(lorem);
        return sb.ToString(0, size);
    }

    private static string GenerateJson(int size)
    {
        var sb = new System.Text.StringBuilder(size);
        sb.Append("{\"data\":[");
        int count = size / 50;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":").Append(i).Append(",\"value\":\"test data ").Append(i).Append("\"}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    [Benchmark] public bool HasSecrets_1MB_LoremIpsum() => SecretScanner.HasSecrets(_1MbLoremIpsum!);
    [Benchmark] public bool HasSecrets_5MB_LoremIpsum() => SecretScanner.HasSecrets(_5MbLoremIpsum!);
    [Benchmark] public bool HasSecrets_10MB_LoremIpsum() => SecretScanner.HasSecrets(_10MbLoremIpsum!);
    [Benchmark] public bool HasSecrets_1MB_Json() => SecretScanner.HasSecrets(_1MbJson!);
    [Benchmark] public bool HasSecrets_5MB_Json() => SecretScanner.HasSecrets(_5MbJson!);
    [Benchmark] public bool HasSecrets_10MB_Json() => SecretScanner.HasSecrets(_10MbJson!);
    [Benchmark] public string? Redact_1MB_LoremIpsum() => SecretScanner.Redact(_1MbLoremIpsum!);
    [Benchmark] public string? Redact_5MB_LoremIpsum() => SecretScanner.Redact(_5MbLoremIpsum!);
    [Benchmark] public string? Redact_10MB_LoremIpsum() => SecretScanner.Redact(_10MbLoremIpsum!);
}