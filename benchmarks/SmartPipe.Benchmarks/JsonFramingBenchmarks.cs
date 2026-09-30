using BenchmarkDotNet.Attributes;
using SmartPipe.Shared.JsonFraming;

namespace SmartPipe.Benchmarks;

[MemoryDiagnoser]
public class JsonFramingBenchmarks
{
    private MemoryStream _stream = null!;

    [Params(128, 64 * 1024, 1024 * 1024, 16 * 1024 * 1024, 16 * 1024 * 1024 + 1)]
    public int InputSize { get; set; }

    [Params("Plain", "BomTrim", "Oversize")]
    public string Variant { get; set; } = "Plain";

    private int Limit => Variant == "Oversize" ? Math.Min(InputSize - 1, 16 * 1024 * 1024) : 16 * 1024 * 1024;

    [GlobalSetup]
    public void Setup()
    {
        var data = new byte[InputSize];
        Array.Fill(data, (byte)'x');
        if (Variant == "BomTrim")
        {
            data[0] = 0xEF;
            data[1] = 0xBB;
            data[2] = 0xBF;
            data[3] = (byte)' ';
            data[^1] = (byte)' ';
        }
        _stream = new MemoryStream(data, writable: false);
    }

    [GlobalCleanup]
    public void Cleanup() => _stream.Dispose();

    [Benchmark]
    public async Task<long> ReadRecords()
    {
        _stream.Position = 0;
        long checksum = 0;
        await foreach (var record in Utf8LineRecordReader.ReadAsync(_stream, Limit, default))
        {
            checksum += record.Bytes.Length + (record.TooLarge ? 1 : 0);
            if (record.Bytes.Length != 0)
                checksum += record.Bytes[0] + record.Bytes[^1];
        }
        return checksum;
    }
}
