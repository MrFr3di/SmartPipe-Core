using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using SmartPipe.Extensions;

namespace SmartPipe.Perf.ExtensionsFocus;

[MemoryDiagnoser]
[BenchmarkCategory("V220Only", "SP220-07-Focus", "ChannelMerge")]
public class ChannelMergeFocusBenchmarks
{
    private const int ItemsPerReader = 64;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        long compatibility = await CompatibilityTwoReader().ConfigureAwait(false);
        long generalized = await MergeManyTwoReader().ConfigureAwait(false);
        long expected = ExpectedChecksum();

        if (compatibility != expected || generalized != expected)
            throw new InvalidOperationException(
                $"Channel focus correctness failed. expected={expected}, compatibility={compatibility}, generalized={generalized}.");
    }

    [Benchmark(Baseline = true)]
    public Task<long> CompatibilityTwoReader() =>
        RunAsync(static (first, second) => ChannelMerge.Merge(first, second));

    [Benchmark]
    public Task<long> MergeManyTwoReader() =>
        RunAsync(static (first, second) =>
            ChannelMerge.MergeMany(
                new ChannelReader<int>[] { first, second },
                options: null,
                cancellationToken: CancellationToken.None));

    private static async Task<long> RunAsync(
        Func<ChannelReader<int>, ChannelReader<int>, ChannelReader<int>> merge)
    {
        Channel<int> first = Channel.CreateUnbounded<int>();
        Channel<int> second = Channel.CreateUnbounded<int>();

        for (int index = 0; index < ItemsPerReader; index++)
        {
            first.Writer.TryWrite(index);
            second.Writer.TryWrite(1000 + index);
        }

        first.Writer.Complete();
        second.Writer.Complete();

        ChannelReader<int> merged = merge(first.Reader, second.Reader);
        long checksum = 0;
        int count = 0;

        await foreach (int item in merged.ReadAllAsync().ConfigureAwait(false))
        {
            checksum += item;
            count++;
        }

        if (count != ItemsPerReader * 2)
            throw new InvalidOperationException($"Expected {ItemsPerReader * 2} merged items, got {count}.");

        return checksum;
    }

    private static long ExpectedChecksum()
    {
        long sequence = (long)ItemsPerReader * (ItemsPerReader - 1) / 2;
        return sequence + (ItemsPerReader * 1000L + sequence);
    }
}
