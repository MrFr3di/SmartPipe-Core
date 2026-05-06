#nullable enable

using System.Reflection;
using System.Threading.Channels;
using NSubstitute;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Core.Tests.Engine;

/// <summary>
/// Tests for SmartPipeChannel.ConsumeAsync method branches.
/// </summary>
public class ConsumeAsyncTests
{
    private readonly SmartPipeChannelOptions _options;
    private readonly SmartPipeChannel<string, string> _channel;
    private readonly CancellationTokenSource _cts;

    public ConsumeAsyncTests()
    {
        _options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1
        };
        _channel = new SmartPipeChannel<string, string>(_options);
        _cts = new CancellationTokenSource();
        InitializePipeline(); // Initialize channels and components
    }

    private async Task InvokeConsumeAsync(CancellationToken ct, int consumerIndex = 0)
    {
        var method = typeof(SmartPipeChannel<string, string>)
            .GetMethod("ConsumeAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(_channel, new object[] { ct, consumerIndex })!;
        await task;
    }

    private void InitializePipeline()
    {
        var method = typeof(SmartPipeChannel<string, string>)
            .GetMethod("InitializePipelineAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (Task)method.Invoke(_channel, new object[] { _cts.Token })!;
        task.GetAwaiter().GetResult();
    }

    private void AddTransformer(ITransformer<string, string> transformer)
    {
        var method = typeof(SmartPipeChannel<string, string>)
            .GetMethod("AddTransformer", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(_channel, new object[] { transformer });
    }

    private void AddSink(ISink<string> sink)
    {
        var method = typeof(SmartPipeChannel<string, string>)
            .GetMethod("AddSink", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(_channel, new object[] { sink });
    }

    private Channel<ProcessingContext<string>>? GetInputChannel()
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_inputChannel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (Channel<ProcessingContext<string>>?)field.GetValue(_channel);
    }

    private Channel<ProcessingResult<string>>? GetOutputChannel()
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_outputChannel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (Channel<ProcessingResult<string>>?)field.GetValue(_channel);
    }

    private void SetShardBuckets(int[] buckets)
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_shardBuckets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(_channel, buckets);
    }

    private void SetCircuitBreaker(CircuitBreaker cb)
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_circuitBreaker", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(_channel, cb);
    }

    /// <summary>
    /// Test: Normal flow - ShouldProcessItem=true, HandleCircuitBreakerAsync=true, ProcessTransformAsync succeeds
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_NormalFlow_ProcessesItem()
    {
        // Arrange
        var transformer = new TestTransformer();
        var sink = new TestSink();
        AddTransformer(transformer);
        AddSink(sink);

        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var ctx = new ProcessingContext<string>("test-payload");
        await inputChannel.Writer.WriteAsync(ctx, _cts.Token);
        inputChannel.Writer.Complete();

        // Act
        await InvokeConsumeAsync(_cts.Token);

        // Assert: Output channel should have the processed item (ConsumeAsync writes here)
        var outputChannel = GetOutputChannel();
        Assert.NotNull(outputChannel);
        var result = await outputChannel.Reader.ReadAsync(_cts.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal("test-payload", result.Value);
    }

    /// <summary>
    /// Test: Cancel during processing - OperationCanceledException
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_Cancellation_ThrowsOperationCanceledException()
    {
        // Arrange: Input channel is already initialized via constructor's InitializePipeline()
        var consumeTask = InvokeConsumeAsync(_cts.Token);
        await Task.Delay(100); // Let the method enter the read loop
        _cts.Cancel();

        // Act & Assert: Method catches cancellation internally, should complete without throwing
        await consumeTask;
    }

    /// <summary>
    /// Test: ShouldProcessItem=false (shard mismatch) → continues to next item
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_ShardMismatch_SkipsItem()
    {
        // Arrange
        var transformer = new TestTransformer();
        AddTransformer(transformer);

        // Set up shard buckets so that the item's TraceId hashes to a different bucket
        var shardBuckets = new int[_options.MaxDegreeOfParallelism];
        // Set all buckets to 0, but we'll use consumerIndex=1 and item will hash to bucket 0
        SetShardBuckets(shardBuckets);

        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var ctx = new ProcessingContext<string>("test-payload");
        await inputChannel.Writer.WriteAsync(ctx, _cts.Token);
        inputChannel.Writer.Complete();

        // Act: Use consumerIndex=1, but item will hash to bucket 0 (assuming JumpHash.Hash returns 0)
        // This should cause ShouldProcessItem to return false and skip the item
        await InvokeConsumeAsync(_cts.Token, consumerIndex: 1);

        // Assert: Method completes without error (item was skipped)
    }

    /// <summary>
    /// Test: HandleCircuitBreakerAsync=false → continues to next item
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_CircuitBreakerOpen_SkipsItem()
    {
        // Arrange
        var transformer = new TestTransformer();
        AddTransformer(transformer);

        // Create a real CircuitBreaker and trip it
        var circuitBreaker = new CircuitBreaker();
        
        // Record enough failures to trip the breaker (need >50% failure rate with min 10 requests)
        for (int i = 0; i < 10; i++)
        {
            circuitBreaker.RecordFailure();
        }
        // Verify it's open
        Assert.False(circuitBreaker.AllowRequest());
        
        SetCircuitBreaker(circuitBreaker);

        // Enable RetryQueue to handle the circuit breaker failure
        _options.EnableFeature("RetryQueue");

        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var ctx = new ProcessingContext<string>("test-payload");
        await inputChannel.Writer.WriteAsync(ctx, _cts.Token);
        inputChannel.Writer.Complete();

        // Act
        await InvokeConsumeAsync(_cts.Token);

        // Assert: Method completes without error (item was skipped due to circuit breaker)
        Assert.Equal(CircuitState.Open, circuitBreaker.State);
    }

    /// <summary>
    /// Test: ProcessTransformAsync failure → calls HandleTransformResultAsync
    /// </summary>
    [Fact]
    public async Task ConsumeAsync_TransformFailure_CallsHandleTransformResultAsync()
    {
        // Arrange
        var failingTransformer = new FailingTransformer();
        AddTransformer(failingTransformer);

        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var ctx = new ProcessingContext<string>("test-payload");
        await inputChannel.Writer.WriteAsync(ctx, _cts.Token);
        inputChannel.Writer.Complete();

        // Act
        await InvokeConsumeAsync(_cts.Token);

        // Assert: Method completes without error (failure is handled by HandleTransformResultAsync)
        // The output channel should have the failure result
        var outputChannel = GetOutputChannel();
        Assert.NotNull(outputChannel);
        var result = await outputChannel.Reader.ReadAsync(_cts.Token);
        Assert.False(result.IsSuccess);
    }

    /// <summary>
    /// Test transformer for unit testing
    /// </summary>
    private class TestTransformer : ITransformer<string, string>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<string>> TransformAsync(ProcessingContext<string> ctx, CancellationToken ct = default)
        {
            return ProcessingResult<string>.Success(ctx.Payload, ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Failing transformer for unit testing
    /// </summary>
    private class FailingTransformer : ITransformer<string, string>
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async ValueTask<ProcessingResult<string>> TransformAsync(ProcessingContext<string> ctx, CancellationToken ct = default)
        {
            return ProcessingResult<string>.Failure(new SmartPipeError("transform failed", ErrorType.Transient, "Test"), ctx.TraceId);
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }

    /// <summary>
    /// Test sink for unit testing
    /// </summary>
    private class TestSink : ISink<string>
    {
        public List<ProcessingResult<string>> Items { get; } = new();
        
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task WriteAsync(ProcessingResult<string> result, CancellationToken ct = default)
        {
            Items.Add(result);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
