#nullable enable

using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;

namespace SmartPipe.Core.Tests.Engine;

public sealed class AdaptiveChannelSetTests
{
    [Fact]
    public async Task CaptureSnapshot_WhenAllLanesAreFull_ShouldReportTotalPressureAtOne()
    {
        await using var buffer = new AdaptiveChannelSet<int>(
            capacity: 5,
            totalLaneCount: 3,
            initialActiveLaneCount: 3,
            fullMode: BoundedChannelFullMode.Wait);

        for (var item = 0; item < 5; item++)
            await buffer.WriteAsync(item, CancellationToken.None);

        var snapshot = buffer.CaptureSnapshot();

        snapshot.TotalBufferedItems.Should().Be(5);
        snapshot.TotalQueuePressure.Should().Be(1.0);
    }

    [Fact]
    public async Task RequestActiveLaneCount_WhenDecreased_ShouldKeepInactiveBufferedItemsReadable()
    {
        await using var buffer = new AdaptiveChannelSet<int>(
            capacity: 6,
            totalLaneCount: 3,
            initialActiveLaneCount: 3,
            fullMode: BoundedChannelFullMode.Wait);

        await buffer.WriteAsync(0, CancellationToken.None);
        await buffer.WriteAsync(1, CancellationToken.None);
        await buffer.WriteAsync(2, CancellationToken.None);

        buffer.RequestActiveLaneCount(1);

        var snapshot = buffer.CaptureSnapshot();
        snapshot.ActiveBufferedItems.Should().Be(1);
        snapshot.InactiveBufferedItems.Should().Be(2);

        var inactiveReader = buffer.CreateReader(1);
        var drained = await inactiveReader.ReadAsync(CancellationToken.None);

        drained.Should().Be(1);
        buffer.CaptureSnapshot().InactiveBufferedItems.Should().Be(1);
    }

    [Fact]
    public async Task RequestActiveLaneCount_WhenLaneBecomesInactive_ShouldNotDropBufferedItems()
    {
        await using var buffer = new AdaptiveChannelSet<int>(
            capacity: 8,
            totalLaneCount: 4,
            initialActiveLaneCount: 4,
            fullMode: BoundedChannelFullMode.Wait);

        for (var item = 0; item < 8; item++)
            await buffer.WriteAsync(item, CancellationToken.None);

        buffer.RequestActiveLaneCount(1);

        var readers = Enumerable.Range(0, 4).Select(buffer.CreateReader).ToArray();
        var drained = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            var reader = readers[i % readers.Length];
            drained.Add(await reader.ReadAsync(CancellationToken.None));
        }

        drained.Should().BeEquivalentTo(Enumerable.Range(0, 8));
        buffer.CaptureSnapshot().TotalBufferedItems.Should().Be(0);
    }

    [Fact]
    public async Task RequestActiveLaneCount_ShouldRejectValuesOutsideLaneRange()
    {
        await using var buffer = new AdaptiveChannelSet<int>(
            capacity: 4,
            totalLaneCount: 2,
            initialActiveLaneCount: 1,
            fullMode: BoundedChannelFullMode.Wait);

        var zero = () => buffer.RequestActiveLaneCount(0);
        var tooMany = () => buffer.RequestActiveLaneCount(3);

        zero.Should().Throw<ArgumentOutOfRangeException>();
        tooMany.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IInputBufferAbstractions_ShouldNotExposeRawChannelReader()
    {
        var exposedTypes = typeof(IInputBuffer<int>)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Concat(typeof(IInputBufferReader<int>).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .SelectMany(method => new[] { method.ReturnType }.Concat(method.GetParameters().Select(p => p.ParameterType)));

        exposedTypes.Should().NotContain(type =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ChannelReader<>));
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    public void SmartPipeChannelOptionsValidate_ShouldRejectAdaptiveDropModesBeforeBufferUse(
        BoundedChannelFullMode fullMode)
    {
        var options = new SmartPipeChannelOptions { FullMode = fullMode };
        options.AdaptiveParallelism.Enabled = true;

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>();
    }
}
