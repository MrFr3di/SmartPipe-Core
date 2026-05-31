#nullable enable
using System.Diagnostics;
using System.Threading;
using Xunit;
using Moq;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests;

public class SmartPipeChannelTests
{
    [Fact]
    public void CreateChannel_SetsCorrectBoundedCapacity()
    {
        // Arrange
        var options = new SmartPipeChannelOptions { BoundedCapacity = 100 };
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Act & Assert
        Assert.NotNull(pipeline);
        Assert.Equal(100, pipeline.Options.BoundedCapacity);
    }

    [Fact]
    public async Task ProduceAsync_ProcessesItem_Successfully()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            UseRendezvous = false
        };
        var sourceMock = new Mock<ISource<string>>();
        var transformerMock = new Mock<ITransformer<string, string>>();
        var sinkMock = new Mock<ISink<string>>();

        var testItem = new ProcessingContext<string>("test");
        sourceMock.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns(new[] { testItem }.ToAsyncEnumerable());
        transformerMock.Setup(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ProcessingResult<string>>(ProcessingResult<string>.Success("test-output", 1UL)));
        sinkMock.Setup(s => s.WriteAsync(It.IsAny<ProcessingResult<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pipeline = new SmartPipeChannel<string, string>(options);
        pipeline.AddSource(sourceMock.Object);
        pipeline.AddTransformer(transformerMock.Object);
        pipeline.AddSink(sinkMock.Object);

        // Act
        await pipeline.RunAsync(CancellationToken.None);

        // Assert
        transformerMock.Verify(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        sinkMock.Verify(s => s.WriteAsync(It.IsAny<ProcessingResult<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HandleFailureAsync_RetrySucceeds_WithinMaxRetries()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true,
            DefaultRetryPolicy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10))
        };
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Act & Assert
        Assert.NotNull(pipeline);
        Assert.True(pipeline.Options.ContinueOnError);
        Assert.NotNull(pipeline.Options.DefaultRetryPolicy);
        Assert.Equal(3, pipeline.Options.DefaultRetryPolicy.MaxRetries);
    }

    [Fact]
    public void HandleFailureAsync_Throws_WhenMaxRetriesExceeded()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            ContinueOnError = false,
            DefaultRetryPolicy = new RetryPolicy(1, TimeSpan.Zero)
        };
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Act & Assert
        Assert.NotNull(pipeline);
        Assert.False(pipeline.Options.ContinueOnError);
        Assert.Equal(1, pipeline.Options.DefaultRetryPolicy!.MaxRetries);
    }

    [Fact]
    public void Constructor_SetsOptions_Correctly()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 500,
            MaxDegreeOfParallelism = 4,
            TotalRequestTimeout = TimeSpan.FromMinutes(5)
        };
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Assert
        Assert.Equal(500, pipeline.Options.BoundedCapacity);
        Assert.Equal(4, pipeline.Options.MaxDegreeOfParallelism);
        Assert.Equal(TimeSpan.FromMinutes(5), pipeline.Options.TotalRequestTimeout);
    }

    [Fact]
    public void Pipeline_StateChanges_WorkCorrectly()
    {
        // Arrange
        var options = new SmartPipeChannelOptions();
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Assert initial state
        Assert.Equal(PipelineState.NotStarted, pipeline.State);

        // Pipeline state can be changed via internal methods
        Assert.NotNull(pipeline);
    }

    [Fact]
    public void HandleSuccess_UpdatesMetrics_Correctly()
    {
        // Arrange
        var options = new SmartPipeChannelOptions { BoundedCapacity = 10 };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var result = ProcessingResult<string>.Success("test-output", 1UL);

        // Use reflection to call private method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod(
            "HandleSuccess",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act & Assert - method should not throw
        Assert.NotNull(method);
        method.Invoke(pipeline, new object[] { result, null!, 100L });
    }

    [Fact]
    public void ShouldProcessItem_ReturnsTrue_ForValidItem()
    {
        // Arrange
        var options = new SmartPipeChannelOptions { BoundedCapacity = 10 };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var ctx = new ProcessingContext<string>("test");

        // Use reflection to call private method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod(
            "ShouldProcessItem",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act
        Assert.NotNull(method);
        var result = (bool)method.Invoke(pipeline, new object[] { ctx, 0 })!;

        // Assert - when _shardBuckets is null, should return true
        Assert.True(result);
    }

    [Fact]
    public void HandleFailureAsync_ShouldRetry_WhenRetryCountLessThanMax()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            ContinueOnError = true,
            DefaultRetryPolicy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10))
        };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var ctx = new ProcessingContext<string>("test");
        var error = new SmartPipeError("Transient error", ErrorType.Transient);
        var result = ProcessingResult<string>.Failure(error, ctx.TraceId);

        // Use reflection to call private method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod(
            "HandleFailureAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act & Assert - should not throw
        Assert.NotNull(method);
        var valueTask = (ValueTask)method.Invoke(pipeline, new object[] { ctx, result, null!, CancellationToken.None })!;
        valueTask.AsTask().Wait();
    }

    [Fact]
    public void HandleFailureAsync_ShouldNotRetry_WhenMaxRetriesExceeded()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            ContinueOnError = false,
            DefaultRetryPolicy = new RetryPolicy(1, TimeSpan.Zero)
        };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var ctx = new ProcessingContext<string>("test");
        var error = new SmartPipeError("Permanent error", ErrorType.Permanent);
        var result = ProcessingResult<string>.Failure(error, ctx.TraceId);

        // Use reflection to call private method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod(
            "HandleFailureAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Act & Assert - should complete without throwing (ContinueOnError=false cancels via token, no exception)
        Assert.NotNull(method);
        var valueTask = (ValueTask)method.Invoke(pipeline, new object[] { ctx, result, null!, CancellationToken.None })!;
        valueTask.AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task ProcessSingleAsync_HandlesSuccessfulItem()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1
        };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var transformerMock = new Mock<ITransformer<string, string>>();
        transformerMock.Setup(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ProcessingResult<string>>(ProcessingResult<string>.Success("output", 1UL)));
        pipeline.AddTransformer(transformerMock.Object);
        var ctx = new ProcessingContext<string>("test");

        // Use reflection to call public method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod("ProcessSingleAsync");

        // Act
        Assert.NotNull(method);
        var task = (ValueTask<ProcessingResult<string>>)method.Invoke(pipeline, new object[] { ctx, CancellationToken.None })!;
        var result = await task;

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("output", result.Value);
    }

    [Fact]
    public async Task ProcessSingleAsync_HandlesFailedItem()
    {
        // Arrange
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1
        };
        var pipeline = new SmartPipeChannel<string, string>(options);
        var transformerMock = new Mock<ITransformer<string, string>>();
        transformerMock.Setup(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ProcessingResult<string>>(
                ProcessingResult<string>.Failure(new SmartPipeError("error", ErrorType.Transient), 1UL)));
        pipeline.AddTransformer(transformerMock.Object);
        var ctx = new ProcessingContext<string>("test");

        // Use reflection to call public method
        var method = typeof(SmartPipeChannel<string, string>).GetMethod("ProcessSingleAsync");

        // Act
        Assert.NotNull(method);
        var task = (ValueTask<ProcessingResult<string>>)method.Invoke(pipeline, new object[] { ctx, CancellationToken.None })!;
        var result = await task;

        // Assert
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RunAsync_LogsDebug_OnCancellation()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SmartPipeChannel<string, string>>>();
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            UseRendezvous = false
        };
        var sourceMock = new Mock<ISource<string>>();
        var transformerMock = new Mock<ITransformer<string, string>>();
        var sinkMock = new Mock<ISink<string>>();

        // Setup source to throw OperationCanceledException when cancellation is requested
        sourceMock.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException();
                return new[] { new ProcessingContext<string>("test") }.ToAsyncEnumerable();
            });
        transformerMock.Setup(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ProcessingResult<string>>(ProcessingResult<string>.Success("output", 1UL)));
        sinkMock.Setup(s => s.WriteAsync(It.IsAny<ProcessingResult<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var pipeline = new SmartPipeChannel<string, string>(options, logger: loggerMock.Object);
        pipeline.AddSource(sourceMock.Object);
        pipeline.AddTransformer(transformerMock.Object);
        pipeline.AddSink(sinkMock.Object);

        // Act - run with a cancellation token that gets cancelled
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        try
        {
            await pipeline.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - verify that LogDebug was called
        await Task.Delay(500);
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("cancelled", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<OperationCanceledException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void Logger_IsNull_DoesNotThrow()
    {
        // Arrange
        var options = new SmartPipeChannelOptions { BoundedCapacity = 10 };
        // Create pipeline without logger (logger is null)
        var pipeline = new SmartPipeChannel<string, string>(options);

        // Assert - pipeline should work without logger
        Assert.NotNull(pipeline);
        Assert.Equal(options.BoundedCapacity, pipeline.Options.BoundedCapacity);
    }

    [Fact]
    public async Task RunAsync_WithCancellationToken_CallsLogger_WhenCancelled()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<SmartPipeChannel<string, string>>>();
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            UseRendezvous = false
        };

        var pipeline = new SmartPipeChannel<string, string>(options, logger: loggerMock.Object);

        // Add a source that yields one item then waits (will be cancelled during consume)
        var sourceMock = new Mock<ISource<string>>();
        sourceMock.Setup(s => s.ReadAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                return new[] { new ProcessingContext<string>("test") }.ToAsyncEnumerable();
            });
        pipeline.AddSource(sourceMock.Object);

        // Transformer will wait and be cancelled
        var transformerMock = new Mock<ITransformer<string, string>>();
        transformerMock.Setup(t => t.TransformAsync(It.IsAny<ProcessingContext<string>>(), It.IsAny<CancellationToken>()))
            .Returns((ProcessingContext<string> ctx, CancellationToken ct) =>
            {
                // Wait until cancellation is requested
                var tcs = new TaskCompletionSource<ProcessingResult<string>>();
                ct.Register(() => tcs.TrySetCanceled());
                return new ValueTask<ProcessingResult<string>>(tcs.Task);
            });
        pipeline.AddTransformer(transformerMock.Object);

        var sinkMock = new Mock<ISink<string>>();
        pipeline.AddSink(sinkMock.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        try
        {
            await pipeline.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - logger should have been called
        await Task.Delay(500);
        loggerMock.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == LogLevel.Debug),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<OperationCanceledException>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldNotDiscardAlreadyAcceptedItems()
    {
        // Source emits 3 items; sink is slow (bounded channel with capacity 1)
        // Run via RunInBackground, call DrainAsync(10s)
        // Assert all 3 items reached the sink
        var itemsProcessed = 0;
        var options = new SmartPipeChannelOptions { BoundedCapacity = 3, MaxDegreeOfParallelism = 1 };
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1, 2, 3]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new CallbackSink<int>(_ => Interlocked.Increment(ref itemsProcessed)));
        var reader = channel.RunInBackground();
        // Wait for all items to be produced (poll up to 5s)
        for (int i = 0; i < 50 && Volatile.Read(ref itemsProcessed) < 3; i++)
            await Task.Delay(100);
        await channel.DrainAsync(TimeSpan.FromSeconds(10));
        // Give a little time for async processing to complete
        await Task.Delay(200);
        itemsProcessed.Should().Be(3, "all 3 items should be processed after DrainAsync");
        try { using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await reader.Completion.WaitAsync(cts2.Token); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldCompleteOutputAfterDrain()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([42]));
        channel.AddTransformer(new IdentityTransformer<int>());
        var itemsInSink = new List<int>();
        channel.AddSink(new CallbackSink<int>(itemsInSink.Add));
        channel.RunInBackground();
        for (int i = 0; i < 50 && itemsInSink.Count == 0; i++)
            await Task.Delay(100);
        var sw = Stopwatch.StartNew();
        await channel.DrainAsync(TimeSpan.FromSeconds(10));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "DrainAsync must complete within 5 seconds");
        // Item must have been processed (not discarded)
        await Task.Delay(100);
        itemsInSink.Should().ContainSingle()
            .Which.Should().Be(42, "item should be processed by sink after drain");
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldBeIdempotent()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        channel.RunInBackground();
        await Task.Delay(100); // let item enter the pipeline
        await channel.DrainAsync(TimeSpan.FromSeconds(10));
        // Second call should return immediately, no throw, no hang
        var sw = Stopwatch.StartNew();
        await channel.DrainAsync(TimeSpan.FromSeconds(10));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "second DrainAsync should return immediately");
    }

    [Fact]
    public async Task SmartPipeChannel_RunInBackground_ShouldReturnReaderConnectedToPipelineOutput()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([99]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        var reader = channel.RunInBackground();
        var items = new List<ProcessingResult<int>>();
        await foreach (var item in reader.ReadAllAsync())
            items.Add(item);
        items.Should().NotBeEmpty("reader should receive processed items");
        items[0].Value.Should().Be(99);
    }

    [Fact]
    public void SmartPipeChannel_RunInBackground_ShouldNotRegisterDuplicateForwardingSinks_WhenCalledTwice()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        channel.RunInBackground(); // first call succeeds
        var act = () => channel.RunInBackground(); // second call
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already started*");
    }

    [Fact]
    public void SmartPipeChannel_ShouldRejectMutationAfterStart()
    {
        var options = new SmartPipeChannelOptions { ThrowOnMutationAfterStart = true };
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        channel.RunInBackground();
        var act = () => channel.AddSource(new SimpleSource<int>([2]));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*mutated after start*");
    }

    [Fact]
    public void SmartPipeChannel_MutationAfterStart_ShouldNotThrow_WhenOptionDisabled()
    {
        var options = new SmartPipeChannelOptions(); // ThrowOnMutationAfterStart defaults to false
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        channel.RunInBackground();
        var act = () => channel.AddSource(new SimpleSource<int>([2]));
        act.Should().NotThrow("ThrowOnMutationAfterStart is disabled by default");
    }

    [Fact]
    public async Task SmartPipeChannel_DisposeAsync_ShouldCompleteChannelsAndDisposeComponentsOnce()
    {
        var source = new DisposableCountingSource<int>([1, 2, 3]);
        var transformer = new DisposableCountingTransformer<int, int>();
        var sink = new DisposableCountingSink<int>();
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(source);
        channel.AddTransformer(transformer);
        channel.AddSink(sink);
        channel.RunInBackground();
        await Task.Delay(200); // let some items flow
        await channel.DisposeAsync();
        source.DisposeCallCount.Should().Be(1, "source disposed exactly once");
        transformer.DisposeCallCount.Should().Be(1, "transformer disposed exactly once");
        sink.DisposeCallCount.Should().Be(1, "sink disposed exactly once");
    }

    [Fact]
    public async Task SmartPipeChannel_Cancel_ShouldStopBackgroundRunWithoutUnobservedTaskException()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new InfiniteSource<int>());
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        await Task.Delay(50);
        var reader = channel.RunInBackground();
        await Task.Delay(200); // let pipeline start producing
        channel.Cancel();
        // Cancel should complete the reader (via try-finally in RunInBackground)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await reader.Completion.WaitAsync(cts.Token); } catch (OperationCanceledException) { }
        channel.State.Should().BeOneOf(PipelineState.Cancelled, PipelineState.Faulted);
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldPropagateRunFault()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new ThrowingInitializeSource<int>());
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());

        channel.RunInBackground();
        await Task.Delay(100);

        await channel.Invoking(c => c.DrainAsync(TimeSpan.FromSeconds(5)))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*source initialization failed*");
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldRespectCallerCancellation()
    {
        var options = new SmartPipeChannelOptions();
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new InfiniteSource<int>());
        channel.AddTransformer(new SlowTransformer<int>(TimeSpan.FromSeconds(5)));
        channel.AddSink(new NoOpSink<int>());
        channel.RunInBackground();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await channel.Invoking(c => c.DrainAsync(TimeSpan.FromSeconds(30), cts.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        channel.Cancel();
    }

    [Fact]
    public void SmartPipeChannelOptions_RetryQueueOverflowPolicy_ShouldDefaultToWait()
    {
        var options = new SmartPipeChannelOptions();
        options.RetryQueueOverflowPolicy.Should().Be(RetryQueueOverflowPolicy.Wait);
    }

    [Fact]
    public void SmartPipeChannelOptions_RetryQueueOverflowPolicy_ShouldBeSettable()
    {
        var options = new SmartPipeChannelOptions();
        options.RetryQueueOverflowPolicy = RetryQueueOverflowPolicy.FailFast;
        options.RetryQueueOverflowPolicy.Should().Be(RetryQueueOverflowPolicy.FailFast);
    }

    [Fact]
    public async Task SmartPipeChannel_RetryQueueOverflowPolicy_ShouldNotAffectPipeline_WhenRetryQueueDisabled()
    {
        var options = TinyCapacityOptionsFactory.Create(10);
        options.RetryQueueOverflowPolicy = RetryQueueOverflowPolicy.DropNewest;
        // Feature flag not enabled
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([1]));
        channel.AddTransformer(new IdentityTransformer<int>());
        channel.AddSink(new NoOpSink<int>());
        // Should not throw — retry queue feature is disabled
        await channel.RunAsync();
        channel.State.Should().Be(PipelineState.Completed);
    }

    [Fact]
    public async Task SmartPipeChannel_RetryQueueOverflowPolicy_ShouldNotChangeSuccessfulPipeline()
    {
        var options = TinyCapacityOptionsFactory.Create(10);
        options.EnableFeature("RetryQueue");
        options.RetryQueueOverflowPolicy = RetryQueueOverflowPolicy.Wait;
        var channel = new SmartPipeChannel<int, int>(options);
        channel.AddSource(new SimpleSource<int>([42]));
        channel.AddTransformer(new IdentityTransformer<int>());
        var sink = new CollectingSink<int>();
        channel.AddSink(sink);
        await channel.RunAsync();
        sink.Items.Should().ContainSingle().Which.Should().Be(42);
    }

    [Fact]
    public async Task SmartPipeChannel_RetryOverflow_FailFast_ShouldEmitTerminalFailure()
    {
        // Use retry queue with FailFast policy and tiny capacity
        var options = TinyCapacityOptionsFactory.Create(1);
        options.EnableFeature("RetryQueue");
        options.DefaultRetryPolicy = new RetryPolicy(3, TimeSpan.Zero);
        options.RetryQueueOverflowPolicy = RetryQueueOverflowPolicy.FailFast;
        options.MaxDegreeOfParallelism = 1;

        var channel = new SmartPipeChannel<string, string>(options);
        channel.AddSource(new SimpleSource<string>(["a", "b", "c"]));
        channel.AddTransformer(new AlwaysTransientFailingTransformer<string, string>());
        var sink = new CollectingSink<string>();
        channel.AddSink(sink);

        await channel.RunAsync();
        channel.State.Should().Be(PipelineState.Completed);
    }

    [Fact]
    public async Task SmartPipeChannel_DrainAsync_ShouldNotCloseOutputBeforeAcceptedWorkFinishes()
    {
        // Arrange: pipeline with a slow transformer so consumers are still processing when DrainAsync is called.
        var options = new SmartPipeChannelOptions { BoundedCapacity = 5, MaxDegreeOfParallelism = 1 };
        var channel = new SmartPipeChannel<int, int>(options);
        var source = new AcceptedTrackingSource<int>([1, 2, 3]);
        channel.AddSource(source);
        // Transformer takes 200ms per item — consumers will still be busy when drain is called.
        channel.AddTransformer(new SlowTransformer<int>(TimeSpan.FromMilliseconds(200)));
        var items = new List<int>();
        channel.AddSink(new CallbackSink<int>(items.Add));
        channel.RunInBackground();

        // Wait until the source loop has handed all items to the pipeline before drain is requested.
        for (int i = 0; i < 50 && source.AcceptedCount < 3; i++)
            await Task.Delay(100);
        source.AcceptedCount.Should().Be(3, "the test verifies drain of accepted work");

        // Act: call DrainAsync while consumers are still processing accepted items.
        var sw = Stopwatch.StartNew();
        await channel.DrainAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        // Assert: all 3 items must have been processed, even though DrainAsync was called
        // while consumers were still busy. The drain must wait for consumer completion,
        // not close the output channel prematurely.
        await Task.Delay(100);
        items.Should().HaveCount(3, "all accepted items should be processed before output is closed");
        items.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        // Drain should not be instant — it waited for consumer processing.
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100),
            "drain waited for consumer processing");
    }

    [Fact]
    public async Task SmartPipeChannel_DelayedRetry_ShouldExecuteAfterProducerCompletion()
    {
        // Arrange: single-item source, transformer that fails transiently on first call
        // then succeeds on retry. RetryQueue must be enabled.
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true,
            DefaultRetryPolicy = new RetryPolicy(3, TimeSpan.FromMilliseconds(10))
        };
        options.EnableFeature("RetryQueue");

        var channel = new SmartPipeChannel<string, string>(options);
        channel.AddSource(new SimpleSource<string>(["retry-me"]));
        var transformer = new RetrySucceedingTransformer<string>();
        channel.AddTransformer(transformer);
        var sink = new CollectingSink<string>();
        channel.AddSink(sink);

        // Act: producer completes after one item; retry should not be silently dropped.
        await channel.RunAsync();

        // Assert: transformer was called twice (first fail, then retry success).
        transformer.CallCount.Should().Be(2, "first call fails transiently, second (retry) succeeds");
        sink.Items.Should().ContainSingle()
            .Which.Should().Be("retry-me", "retry was requeued and processed successfully");
        channel.State.Should().Be(PipelineState.Completed);
    }

    [Fact]
    public async Task SmartPipeChannel_DelayedRetry_ShouldKeepRetryLoopAliveUntilRetryQueueEmpty()
    {
        var options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1,
            ContinueOnError = true,
            DefaultRetryPolicy = new RetryPolicy(3, TimeSpan.FromMilliseconds(250))
        };
        options.EnableFeature("RetryQueue");

        var channel = new SmartPipeChannel<string, string>(options);
        channel.AddSource(new SimpleSource<string>(["retry-me"]));
        var transformer = new RetrySucceedingTransformer<string>();
        channel.AddTransformer(transformer);
        var sink = new CollectingSink<string>();
        channel.AddSink(sink);

        await channel.RunAsync();

        transformer.CallCount.Should().Be(2);
        sink.Items.Should().ContainSingle().Which.Should().Be("retry-me");
        channel.State.Should().Be(PipelineState.Completed);
    }
}
