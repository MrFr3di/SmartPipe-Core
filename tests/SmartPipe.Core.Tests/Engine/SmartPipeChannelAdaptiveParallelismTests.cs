#nullable enable

using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using FluentAssertions;
using SmartPipe.Core.Tests;

namespace SmartPipe.Core.Tests.Engine;

public sealed class SmartPipeChannelAdaptiveParallelismTests
{
    [Fact]
    public async Task RunAsync_DefaultPath_ShouldNotCreateAdaptiveBufferOrLimiter()
    {
        var channel = new SmartPipeChannel<int, int>(new SmartPipeChannelOptions
        {
            BoundedCapacity = 4,
            MaxDegreeOfParallelism = 2,
        });
        channel.AddSource(new SimpleSource<int>([1, 2]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());

        await channel.RunAsync();

        GetPrivateField(channel, "_adaptiveChannelSet").Should().BeNull();
        GetPrivateField(channel, "_adaptiveInFlightLimiter").Should().BeNull();
    }

    [Theory]
    [InlineData(BoundedChannelFullMode.DropNewest)]
    [InlineData(BoundedChannelFullMode.DropOldest)]
    [InlineData(BoundedChannelFullMode.DropWrite)]
    public void Constructor_WhenAdaptiveModeUsesDropFullMode_ShouldThrow(BoundedChannelFullMode fullMode)
    {
        var options = AdaptiveOptions();
        options.FullMode = fullMode;

        var act = () => new SmartPipeChannel<int, int>(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Adaptive parallelism*FullMode*Wait*");
    }

    [Fact]
    public void Constructor_WhenAdaptiveModeUsesJumpHash_ShouldThrow()
    {
        var options = AdaptiveOptions();
        options.EnableFeature("JumpHash");

        var act = () => new SmartPipeChannel<int, int>(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Adaptive parallelism*JumpHash*");
    }

    [Fact]
    public async Task RunAsync_WithAdaptiveMode_ShouldProcessAcceptedItemsExactlyOnce()
    {
        var input = Enumerable.Range(0, 100).ToArray();
        var source = new AcceptedTrackingSource<int>(input);
        var sink = new CollectingSink<int>();
        var channel = new SmartPipeChannel<int, int>(AdaptiveOptions());
        channel.AddSource(source);
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(sink);

        await channel.RunAsync();

        GetPrivateField(channel, "_adaptiveChannelSet").Should().NotBeNull();
        GetPrivateField(channel, "_adaptiveInFlightLimiter").Should().NotBeNull();
        source.AcceptedCount.Should().Be(input.Length);
        sink.Items.Should().HaveCount(input.Length);
        sink.Items.Should().BeEquivalentTo(input);
        sink.Items.GroupBy(item => item).Should().OnlyContain(group => group.Count() == 1);
    }

    [Fact]
    public async Task RunAsync_WithAdaptiveMode_ShouldNotExceedInFlightLimit()
    {
        var options = AdaptiveOptions();
        options.AdaptiveParallelism.InitialInFlightItems = 2;
        options.AdaptiveParallelism.MaxInFlightItems = 2;
        options.AdaptiveParallelism.InitialDegreeOfParallelism = 2;
        var transformer = new ConcurrentTrackingTransformer(delay: TimeSpan.FromMilliseconds(30));
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>(Enumerable.Range(0, 20).ToArray()));
        channel.AddTransformer(transformer);
        channel.AddSink(new NoOpSink<int>());

        await channel.RunAsync();

        transformer.MaxConcurrent.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringLimiterWait_ShouldCompleteWithoutPermitLeak()
    {
        var options = AdaptiveOptions();
        options.AdaptiveParallelism.InitialInFlightItems = 1;
        options.AdaptiveParallelism.MaxInFlightItems = 1;
        options.AdaptiveParallelism.InitialDegreeOfParallelism = 1;
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new InfiniteSource<int>());
        channel.AddTransformer(new ConcurrentTrackingTransformer(delay: TimeSpan.FromSeconds(5)));
        channel.AddSink(new NoOpSink<int>());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await channel.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(10));

        var limiter = (AdaptiveInFlightLimiter?)GetPrivateField(channel, "_adaptiveInFlightLimiter");
        limiter.Should().NotBeNull();
        limiter!.InUse.Should().Be(0);
        channel.State.Should().Be(PipelineState.Cancelled);
    }

    [Fact]
    public async Task RunAsync_WithAdaptiveMode_ShouldNotAcquireInFlightLeaseWhileWaitingForInput()
    {
        var options = AdaptiveOptions();
        options.AdaptiveParallelism.MaxDegreeOfParallelism = 2;
        options.AdaptiveParallelism.InitialDegreeOfParallelism = 2;
        options.AdaptiveParallelism.InitialInFlightItems = 2;
        options.AdaptiveParallelism.MaxInFlightItems = 2;
        var source = new GatedEmptySource<int>();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());

        var runTask = channel.RunAsync();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var limiter = await WaitForLimiterAsync(channel);

        try
        {
            await Task.Delay(100);
            limiter.InUse.Should().Be(0);
        }
        finally
        {
            source.Release();
            await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RunAsync_WithAdaptiveMode_ShouldCompleteWithoutHanging()
    {
        var channel = new SmartPipeChannel<int, int>(AdaptiveOptions());
        channel.AddSource(new SimpleSource<int>(Enumerable.Range(0, 10).ToArray()));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());

        await channel.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        channel.State.Should().Be(PipelineState.Completed);
    }

    private static SmartPipeChannelOptions AdaptiveOptions()
    {
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 8,
            MaxDegreeOfParallelism = 4,
            FullMode = BoundedChannelFullMode.Wait,
        };
        options.AdaptiveParallelism.Enabled = true;
        options.AdaptiveParallelism.MinDegreeOfParallelism = 1;
        options.AdaptiveParallelism.MaxDegreeOfParallelism = 4;
        options.AdaptiveParallelism.InitialDegreeOfParallelism = 2;
        options.AdaptiveParallelism.InitialInFlightItems = 2;
        options.AdaptiveParallelism.MaxInFlightItems = 4;
        options.AdaptiveParallelism.SamplingInterval = TimeSpan.FromMilliseconds(25);
        options.AdaptiveParallelism.Cooldown = TimeSpan.FromMilliseconds(50);
        return options;
    }

    private static object? GetPrivateField<TInput, TOutput>(
        SmartPipeChannel<TInput, TOutput> channel,
        string fieldName)
    {
        return typeof(SmartPipeChannel<TInput, TOutput>)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(channel);
    }

    private static async Task<AdaptiveInFlightLimiter> WaitForLimiterAsync<TInput, TOutput>(
        SmartPipeChannel<TInput, TOutput> channel)
    {
        for (var i = 0; i < 100; i++)
        {
            if (GetPrivateField(channel, "_adaptiveInFlightLimiter") is AdaptiveInFlightLimiter limiter)
                return limiter;

            await Task.Delay(10);
        }

        throw new TimeoutException("Adaptive in-flight limiter was not initialized.");
    }

    private sealed class GatedEmptySource<T> : ISource<T>
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            yield break;
        }

        public void Release() => _release.TrySetResult();

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class ConcurrentTrackingTransformer(TimeSpan delay) : ITransformer<int, int>
    {
        private int _currentConcurrent;
        private int _maxConcurrent;

        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<int>> TransformAsync(
            ProcessingContext<int> ctx,
            CancellationToken ct = default)
        {
            var current = Interlocked.Increment(ref _currentConcurrent);
            UpdateMax(current);
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                return ProcessingResult<int>.Success(ctx.Payload, ctx.TraceId);
            }
            finally
            {
                Interlocked.Decrement(ref _currentConcurrent);
            }
        }

        public Task DisposeAsync() => Task.CompletedTask;

        private void UpdateMax(int current)
        {
            while (true)
            {
                var previous = Volatile.Read(ref _maxConcurrent);
                if (current <= previous)
                    return;

                if (Interlocked.CompareExchange(ref _maxConcurrent, current, previous) == previous)
                    return;
            }
        }
    }
}
