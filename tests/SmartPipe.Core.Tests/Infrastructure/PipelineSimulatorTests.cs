using FluentAssertions;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Infrastructure;

public class PipelineSimulatorTests
{
    [Fact]
    public async Task GenerateAsync_ShouldProduceCorrectCount()
    {
        var sim = new PipelineSimulator(seed: 42);
        var items = new List<ProcessingContext<int>>();

        await foreach (var item in sim.GenerateAsync(i => i, 10))
            items.Add(item);

        items.Should().HaveCount(10);
        items[0].Payload.Should().Be(0);
        items[9].Payload.Should().Be(9);
    }

    [Fact]
    public void SimulateFailure_ShouldFollowProbability()
    {
        var sim = new PipelineSimulator(seed: 42);
        int failures = 0;
        for (int i = 0; i < 10000; i++)
            if (sim.SimulateFailure(0.1)) failures++;

        // ~10% of 10000 = 1000, allow +/- 200
        failures.Should().BeInRange(800, 1200);
    }

    [Fact]
    public void Reset_ShouldClearStep()
    {
        var sim = new PipelineSimulator();
        sim.SimulateFailure();
        sim.Reset();

        sim.Step.Should().Be(0);
    }

    [Fact]
    public async Task SimulateDelayAsync_ShouldComplete()
    {
        var sim = new PipelineSimulator();
        await sim.SimulateDelayAsync(1, 5);
        sim.Step.Should().Be(1);
    }
}
