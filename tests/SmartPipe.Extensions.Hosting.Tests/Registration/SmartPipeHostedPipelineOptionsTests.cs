namespace SmartPipe.Extensions.Hosting.Tests.Registration;

public sealed class SmartPipeHostedPipelineOptionsTests
{
    [Fact]
    public void Defaults_AreCanonical()
    {
        var snapshot = SmartPipeHostedPipelineOptionsSnapshot.Create(
            new SmartPipeHostedPipelineOptions());

        Assert.Equal(0, snapshot.Order);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.DrainTimeout);
        Assert.Equal(SmartPipeHostedPipelineFailureBehavior.StopApplication, snapshot.FailureBehavior);
        Assert.Equal(SmartPipeHostedCompletionBehavior.KeepHostAlive, snapshot.CompletionBehavior);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Snapshot_AcceptsAnyOrder(int order)
    {
        var snapshot = SmartPipeHostedPipelineOptionsSnapshot.Create(
            new SmartPipeHostedPipelineOptions { Order = order });

        Assert.Equal(order, snapshot.Order);
    }

    [Fact]
    public void Snapshot_AcceptsPositiveAndInfiniteDrainTimeouts()
    {
        var positive = SmartPipeHostedPipelineOptionsSnapshot.Create(
            new SmartPipeHostedPipelineOptions { DrainTimeout = TimeSpan.FromTicks(1) });
        var infinite = SmartPipeHostedPipelineOptionsSnapshot.Create(
            new SmartPipeHostedPipelineOptions { DrainTimeout = Timeout.InfiniteTimeSpan });

        Assert.Equal(TimeSpan.FromTicks(1), positive.DrainTimeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, infinite.DrainTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Snapshot_RejectsNonPositiveFiniteDrainTimeout(long ticks)
    {
        var options = new SmartPipeHostedPipelineOptions
        {
            DrainTimeout = TimeSpan.FromTicks(ticks),
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SmartPipeHostedPipelineOptionsSnapshot.Create(options));

        Assert.Equal(nameof(SmartPipeHostedPipelineOptions.DrainTimeout), exception.ParamName);
    }

    [Fact]
    public void Snapshot_RejectsUndefinedFailureBehavior()
    {
        var options = new SmartPipeHostedPipelineOptions
        {
            FailureBehavior = (SmartPipeHostedPipelineFailureBehavior)int.MaxValue,
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SmartPipeHostedPipelineOptionsSnapshot.Create(options));

        Assert.Equal(nameof(SmartPipeHostedPipelineOptions.FailureBehavior), exception.ParamName);
    }

    [Fact]
    public void Snapshot_RejectsUndefinedCompletionBehavior()
    {
        var options = new SmartPipeHostedPipelineOptions
        {
            CompletionBehavior = (SmartPipeHostedCompletionBehavior)int.MaxValue,
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => SmartPipeHostedPipelineOptionsSnapshot.Create(options));

        Assert.Equal(nameof(SmartPipeHostedPipelineOptions.CompletionBehavior), exception.ParamName);
    }

    [Fact]
    public void Snapshot_DefensivelyCopiesOptions()
    {
        var options = new SmartPipeHostedPipelineOptions
        {
            Order = 7,
            DrainTimeout = TimeSpan.FromSeconds(8),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Ignore,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.StopApplication,
        };
        var snapshot = SmartPipeHostedPipelineOptionsSnapshot.Create(options);

        options.Order = 70;
        options.DrainTimeout = TimeSpan.FromSeconds(80);
        options.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Rethrow;
        options.CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive;

        Assert.Equal(7, snapshot.Order);
        Assert.Equal(TimeSpan.FromSeconds(8), snapshot.DrainTimeout);
        Assert.Equal(SmartPipeHostedPipelineFailureBehavior.Ignore, snapshot.FailureBehavior);
        Assert.Equal(SmartPipeHostedCompletionBehavior.StopApplication, snapshot.CompletionBehavior);
    }

    [Fact]
    public void Enums_HaveExactContractValues()
    {
        Assert.Equal(
            [
                SmartPipeHostedCompletionBehavior.KeepHostAlive,
                SmartPipeHostedCompletionBehavior.StopApplication,
            ],
            Enum.GetValues<SmartPipeHostedCompletionBehavior>());
        Assert.Equal(
            [
                SmartPipeHostedPipelineFailureBehavior.StopApplication,
                SmartPipeHostedPipelineFailureBehavior.Rethrow,
                SmartPipeHostedPipelineFailureBehavior.MarkUnhealthyAndKeepHostAlive,
                SmartPipeHostedPipelineFailureBehavior.Ignore,
            ],
            Enum.GetValues<SmartPipeHostedPipelineFailureBehavior>());
    }
}
