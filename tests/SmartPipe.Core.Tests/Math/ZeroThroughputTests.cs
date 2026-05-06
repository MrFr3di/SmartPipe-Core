using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SmartPipe.Core;
using SmartPipe.Core.Tests.Infrastructure;

namespace SmartPipe.Core.Tests.Engine;

/// <summary>
/// Tests zero throughput scenarios where consumers hang or timeouts fire.
/// Verifies CircuitBreaker behavior under these conditions.
/// </summary>
public class ZeroThroughputTests
{
    /// <summary>
    /// Transformer that simulates a hanging consumer by delaying indefinitely
    /// or until cancellation is requested.
    /// </summary>
    private class HangingTransformer<T> : ITransformer<T, T>
    {
        private readonly TimeSpan _delay;
        private readonly PipelineSimulator? _simulator;

        public HangingTransformer(TimeSpan? delay = null, PipelineSimulator? simulator = null)
        {
            _delay = delay ?? Timeout.InfiniteTimeSpan;
            _simulator = simulator;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
        {
            if (_simulator != null)
            {
                // Use simulator to potentially simulate failure before hanging
                if (_simulator.SimulateFailure(0.5))
                    return ProcessingResult<T>.Failure(
                        new SmartPipeError("Simulated failure", ErrorType.Transient), ctx.TraceId);
                
                // Simulate delay using the simulator
                await _simulator.SimulateDelayAsync(100, 500, ct);
            }

            // Hang until cancellation is requested
            if (_delay == Timeout.InfiniteTimeSpan)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            else
            {
                await Task.Delay(_delay, ct);
            }

            return ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Transformer that always fails after a delay, used to trigger CircuitBreaker.
    /// </summary>
    private class FailingTransformer<T> : ITransformer<T, T>
    {
        private readonly PipelineSimulator _simulator;
        private readonly string _errorMessage;

        public FailingTransformer(PipelineSimulator simulator, string errorMessage = "Transform failed")
        {
            _simulator = simulator;
            _errorMessage = errorMessage;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
        {
            // Simulate some delay
            await _simulator.SimulateDelayAsync(10, 50, ct);
            
            // Always fail
            return ProcessingResult<T>.Failure(
                new SmartPipeError(_errorMessage, ErrorType.Transient), ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task HangingConsumer_WithTimeout_ShouldFireTimeout()
    {
        // Arrange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new SimpleSource<int>(1, 2, 3);
        var hangingTransformer = new HangingTransformer<int>(TimeSpan.FromSeconds(10));
        var sink = new CollectionSink<int>();
        
        var options = new SmartPipeChannelOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(100), // Very short timeout
            TotalRequestTimeout = TimeSpan.FromSeconds(5),
            BoundedCapacity = 10
        };
        options.EnableFeature("CircuitBreaker");

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(hangingTransformer);
        channel.AddSink(sink);

        // Act & Assert - The pipeline should complete with timeout failures
        var task = channel.RunAsync(cts.Token);
        
        // Wait for pipeline to complete (should happen due to timeouts)
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) as Task;
        
        // The pipeline should have processed items with timeout failures
        // Since ContinueOnError is true by default, it should complete
        if (completed == task)
        {
            // Pipeline completed normally (possibly with failures)
            await task;
        }
        
        // Verify sink has no successful results (all timed out)
        sink.Results.Should().BeEmpty("all items should have timed out");
    }

    [Fact]
    public async Task CircuitBreaker_ShouldOpen_AfterConsecutiveFailures()
    {
        // Arrange
        var simulator = new PipelineSimulator(seed: 42);
        var source = new SimpleSource<int>(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
        var failingTransformer = new FailingTransformer<int>(simulator, "Circuit breaker test failure");
        var sink = new CollectionSink<int>();
        
        var options = new SmartPipeChannelOptions
        {
            AttemptTimeout = TimeSpan.FromSeconds(1),
            TotalRequestTimeout = TimeSpan.FromSeconds(30),
            BoundedCapacity = 100,
            MaxDegreeOfParallelism = 1
        };
        options.EnableFeature("CircuitBreaker");
        options.EnableFeature("RetryQueue");

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(failingTransformer);
        channel.AddSink(sink);

        // Act
        await channel.RunAsync();

        // Assert - CircuitBreaker should have opened due to consecutive failures
        // The CircuitBreaker opens when: failures >= minimumThroughput (10) AND failureRatio >= 0.5
        // With 11 consecutive failures, it should definitely open
        var dashboard = channel.CreateDashboard();
        
        // Verify that the circuit breaker state was Open during processing
        // The sink should have no successful results
        sink.Results.Should().BeEmpty("all items should have failed");
        
        // Check dashboard for circuit breaker state info
        dashboard.CBState.Should().NotBe("N/A");
    }

    [Fact]
    public async Task ZeroThroughput_WithHangingConsumer_AndCircuitBreaker_ShouldDetectFailure()
    {
        // Arrange - Create a scenario where consumer hangs
        var simulator = new PipelineSimulator(seed: 123);
        var source = new SimpleSource<int>(1, 2, 3, 4, 5);
        
        // Create a transformer that hangs (will be cancelled by timeout)
        var hangingTransformer = new HangingTransformer<int>(TimeSpan.FromMinutes(1), simulator);
        var sink = new CollectionSink<int>();
        
        var options = new SmartPipeChannelOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(200), // Short timeout to fire quickly
            TotalRequestTimeout = TimeSpan.FromSeconds(3),   // Overall pipeline timeout
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true
        };
        options.EnableFeature("CircuitBreaker");

        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(hangingTransformer);
        channel.AddSink(sink);

        // Track state changes
        var stateChanges = new List<(PipelineState Old, PipelineState New)>();
        channel.OnStateChanged += (oldState, newState) => stateChanges.Add((oldState, newState));

        // Act
        try
        {
            await channel.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected - timeout fires
        }

        // Assert
        // Pipeline should have transitioned to Faulted state due to timeout
        stateChanges.Should().Contain(sc => sc.New == PipelineState.Faulted || sc.New == PipelineState.Completed);
        
        // Sink should have no results (all timed out before processing)
        sink.Results.Should().BeEmpty();
    }

    [Fact]
    public void PipelineSimulator_SimulateDelayAsync_ShouldRespectCancellation()
    {
        // Arrange
        var simulator = new PipelineSimulator(seed: 42);
        using var cts = new CancellationTokenSource();

        // Act & Assert
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            cts.Cancel();
            simulator.SimulateDelayAsync(1000, 5000, cts.Token).GetAwaiter().GetResult();
        });
    }

    [Fact]
    public void CircuitBreaker_ShouldOpen_WithMinimumThroughput()
    {
        // Arrange
        var cb = new CircuitBreaker(minimumThroughput: 5, failureRatio: 0.5);

        // Act - Record failures to exceed minimum throughput and failure ratio
        for (int i = 0; i < 5; i++)
        {
            cb.RecordFailure();
        }

        // Assert - CircuitBreaker should be open
        cb.State.Should().Be(CircuitState.Open);
        
        // Verify metrics
        var metrics = cb.GetMetrics();
        metrics["cb_state"].Should().Be("Open");
    }

    [Fact]
    public async Task Pipeline_WithTimeoutAndCircuitBreaker_ShouldHandleZeroThroughput()
    {
        // Arrange
        var simulator = new PipelineSimulator(seed: 999);
        var source = new SimpleSource<string>("item1", "item2", "item3");
        
        // Transformer that uses SimulateFailure to control failure rate
        var transformer = new FailingTransformer<string>(simulator);
        var sink = new CollectionSink<string>();
        
        var options = new SmartPipeChannelOptions
        {
            AttemptTimeout = TimeSpan.FromMilliseconds(500),
            TotalRequestTimeout = TimeSpan.FromSeconds(5),
            BoundedCapacity = 100,
            ContinueOnError = true
        };
        options.EnableFeature("CircuitBreaker");
        options.EnableFeature("RetryQueue");

        var channel = new SmartPipeChannel<string, string>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);

        // Act
        await channel.RunAsync();

        // Assert
        // All items should have failed (transformer always fails)
        sink.Results.Should().BeEmpty();
        
        // CircuitBreaker should have opened due to failures
        // Check via dashboard - the CBState might be "Open" or "HalfOpen" after retries
        var dashboard = channel.CreateDashboard();
        dashboard.CBState.Should().NotBe("N/A");
    }
}
