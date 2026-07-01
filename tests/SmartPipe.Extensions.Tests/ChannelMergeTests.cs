using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Extensions;

namespace SmartPipe.Extensions.Tests;

public class ChannelMergeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Merge_NullFirst_ThrowsArgumentNullException()
    {
        var second = Channel.CreateUnbounded<int>();

        var act = () => ChannelMerge.Merge<int>(null!, second.Reader);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("first");
    }

    [Fact]
    public void Merge_NullSecond_ThrowsArgumentNullException()
    {
        var first = Channel.CreateUnbounded<int>();

        var act = () => ChannelMerge.Merge<int>(first.Reader, null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("second");
    }

    [Fact]
    public void Merge_WithCancellation_NullFirst_ThrowsArgumentNullException()
    {
        var second = Channel.CreateUnbounded<int>();

        var act = () => ChannelMerge.Merge<int>(
            null!,
            second.Reader,
            options: null,
            CancellationToken.None);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("first");
    }

    [Fact]
    public void Merge_WithCancellation_NullSecond_ThrowsArgumentNullException()
    {
        var first = Channel.CreateUnbounded<int>();

        var act = () => ChannelMerge.Merge<int>(
            first.Reader,
            null!,
            options: null,
            CancellationToken.None);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("second");
    }

    [Fact]
    public async Task Merge_TwoChannels_ShouldCombine()
    {
        var ch1 = Channel.CreateUnbounded<int>();
        var ch2 = Channel.CreateUnbounded<int>();

        await ch1.Writer.WriteAsync(1);
        await ch1.Writer.WriteAsync(2);
        ch1.Writer.Complete();

        await ch2.Writer.WriteAsync(3);
        await ch2.Writer.WriteAsync(4);
        ch2.Writer.Complete();

        var merged = ChannelMerge.Merge(ch1.Reader, ch2.Reader);
        var results = await ReadAllAsync(merged).WaitAsync(Timeout);

        results.Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Merge_BoundedOutput_ShouldPreserveItems()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        };

        await first.Writer.WriteAsync(1);
        await first.Writer.WriteAsync(2);
        first.Writer.Complete();

        await second.Writer.WriteAsync(3);
        await second.Writer.WriteAsync(4);
        second.Writer.Complete();

        var merged = ChannelMerge.Merge(first.Reader, second.Reader, options);
        var results = await ReadAllAsync(merged).WaitAsync(Timeout);

        results.Should().BeEquivalentTo([1, 2, 3, 4]);
    }

    [Fact]
    public async Task Merge_Cancellation_CompletesOutputWithCancellation()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        using var cts = new CancellationTokenSource();
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        };

        var merged = ChannelMerge.Merge(first.Reader, second.Reader, options, cts.Token);

        await first.Writer.WriteAsync(1);
        await first.Writer.WriteAsync(2);
        (await merged.WaitToReadAsync().AsTask().WaitAsync(Timeout)).Should().BeTrue();

        cts.Cancel();

        var readTask = ReadAllAsync(merged);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Merge_WithPreCancelledToken_CompletesOutputWithCancellation()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var merged = ChannelMerge.Merge(first.Reader, second.Reader, options: null, cts.Token);

        var readTask = ReadAllAsync(merged);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Merge_WhenInputFaults_CompletesOutputWithFault()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var expected = new InvalidOperationException("input failed");
        var merged = ChannelMerge.Merge(first.Reader, second.Reader);

        first.Writer.TryComplete(expected);

        var readTask = ReadAllAsync(merged);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => readTask.WaitAsync(Timeout)
        );

        exception.Should().BeSameAs(expected);
    }

    private static async Task<List<T>> ReadAllAsync<T>(ChannelReader<T> reader)
    {
        var results = new List<T>();

        await foreach (var item in reader.ReadAllAsync())
            results.Add(item);

        return results;
    }
}
