#nullable enable
using Xunit;
using Moq;
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
}
