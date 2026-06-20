#nullable enable

using System.Collections.Concurrent;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Engine;

public sealed class PipelineChannelFactoryTests
{
    [Fact]
    public async Task PipelineChannelFactory_Input_AllowsMultipleReaders()
    {
        var channel = PipelineChannelFactory.CreateInput<int>(
            capacity: 8,
            fullMode: BoundedChannelFullMode.Wait);
        var seen = new ConcurrentDictionary<int, byte>();

        var readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var envelope in channel.Reader.ReadAllAsync())
                    seen.TryAdd(envelope.Payload, 0);
            }))
            .ToArray();

        for (int i = 0; i < 100; i++)
            await channel.Writer.WriteAsync(ProcessingEnvelope<int>.Create(i));

        channel.Writer.Complete();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(5));

        seen.Keys.Should().BeEquivalentTo(Enumerable.Range(0, 100));
    }

    [Fact]
    public async Task PipelineChannelFactory_Output_AllowsMultipleWritersForSingleConsumer()
    {
        var channel = PipelineChannelFactory.CreateOutput<int>(
            capacity: 8,
            fullMode: BoundedChannelFullMode.Wait);
        var outputs = new ConcurrentDictionary<int, byte>();

        var reader = Task.Run(async () =>
        {
            await foreach (var output in channel.Reader.ReadAllAsync())
            {
                outputs.TryAdd(output.Result.Value, 0);
            }
        });

        var writers = Enumerable.Range(0, 100)
            .Select(i => Task.Run(async () =>
            {
                var envelope = ProcessingEnvelope<int>.Create(i);
                await channel.Writer.WriteAsync(
                    new PipelineOutput<int>(
                        envelope,
                        PipelineResult<int>.Success(i, envelope.TraceId)));
            }))
            .ToArray();

        await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(5));
        channel.Writer.Complete();
        await reader.WaitAsync(TimeSpan.FromSeconds(5));

        outputs.Keys.Should().BeEquivalentTo(Enumerable.Range(0, 100));
    }

    [Fact]
    public void OutputChannel_IsSingleReaderByContract()
    {
        var options = PipelineChannelFactory.CreateOutputOptions(
            capacity: 8,
            fullMode: BoundedChannelFullMode.Wait);

        options.Capacity.Should().Be(8);
        options.FullMode.Should().Be(BoundedChannelFullMode.Wait);
        options.SingleReader.Should().BeTrue();
        options.SingleWriter.Should().BeFalse();
        options.AllowSynchronousContinuations.Should().BeFalse();
    }

    [Fact]
    public void PipelineRunOutputs_DocumentedSingleConsumer()
    {
        var options = PipelineChannelFactory.CreateOutputOptions(
            capacity: 1024,
            fullMode: BoundedChannelFullMode.Wait);

        options.SingleReader.Should().BeTrue(
            "PipelineRun<T>.Outputs is documented and configured as a single-consumer channel");
    }

    [Fact]
    public async Task PipelineChannelFactory_BoundedInput_AppliesBackpressure()
    {
        var channel = PipelineChannelFactory.CreateInput<int>(
            capacity: 1,
            fullMode: BoundedChannelFullMode.Wait);

        await channel.Writer.WriteAsync(ProcessingEnvelope<int>.Create(1));
        var pendingWrite = channel.Writer.WriteAsync(ProcessingEnvelope<int>.Create(2)).AsTask();

        pendingWrite.IsCompleted.Should().BeFalse("the bounded input channel is full");

        var first = await channel.Reader.ReadAsync();
        first.Payload.Should().Be(1);
        await pendingWrite.WaitAsync(TimeSpan.FromSeconds(5));

        var second = await channel.Reader.ReadAsync();
        second.Payload.Should().Be(2);
    }

    [Fact]
    public async Task PipelineChannelFactory_InputDropMode_InvokesDroppedCallback()
    {
        var dropped = new List<int>();
        var channel = PipelineChannelFactory.CreateInput<int>(
            capacity: 1,
            fullMode: BoundedChannelFullMode.DropWrite,
            itemDropped: envelope => dropped.Add(envelope.Payload));

        await channel.Writer.WriteAsync(ProcessingEnvelope<int>.Create(1));
        await channel.Writer.WriteAsync(ProcessingEnvelope<int>.Create(2));

        dropped.Should().ContainSingle().Which.Should().Be(2);
    }

    [Fact]
    public async Task PipelineChannelFactory_OutputDropMode_InvokesDroppedCallback()
    {
        var dropped = new List<int>();
        var channel = PipelineChannelFactory.CreateOutput<int>(
            capacity: 1,
            fullMode: BoundedChannelFullMode.DropOldest,
            itemDropped: output => dropped.Add(output.Result.Value));

        var first = ProcessingEnvelope<int>.Create(1);
        var second = ProcessingEnvelope<int>.Create(2);

        await channel.Writer.WriteAsync(new PipelineOutput<int>(
            first,
            PipelineResult<int>.Success(first.Payload, first.TraceId)));
        await channel.Writer.WriteAsync(new PipelineOutput<int>(
            second,
            PipelineResult<int>.Success(second.Payload, second.TraceId)));

        dropped.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task PipelineChannelFactory_ObserverDropMode_InvokesDroppedCallback()
    {
        var dropped = new List<PipelineEvent>();
        var channel = PipelineChannelFactory.CreateObserverBuffer(
            capacity: 1,
            fullMode: BoundedChannelFullMode.DropWrite,
            itemDropped: dropped.Add);

        await channel.Writer.WriteAsync(new PipelineStartedEvent(
            "pipeline",
            "run",
            DateTimeOffset.UtcNow));
        await channel.Writer.WriteAsync(new PipelineCompletedEvent(
            "pipeline",
            "run",
            DateTimeOffset.UtcNow));

        dropped.Should().ContainSingle()
            .Which.Should().BeOfType<PipelineCompletedEvent>();
    }
}
