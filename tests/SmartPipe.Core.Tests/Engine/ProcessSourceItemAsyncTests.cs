#nullable enable

using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Diagnostics;
using NSubstitute;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Core.Tests.Engine;

/// <summary>
/// Tests for SmartPipeChannel.ProcessSourceItemAsync method branches.
/// </summary>
public class ProcessSourceItemAsyncTests
{
    private readonly SmartPipeChannelOptions _options;
    private readonly SmartPipeChannel<string, string> _channel;
    private readonly CancellationTokenSource _cts;

    public ProcessSourceItemAsyncTests()
    {
        _options = new SmartPipeChannelOptions
        {
            BoundedCapacity = 10,
            MaxDegreeOfParallelism = 1
        };
        _channel = new SmartPipeChannel<string, string>(_options);
        _cts = new CancellationTokenSource();
        // Don't initialize pipeline here - causes state leakage between tests
        // Each test that needs channels will initialize them individually
    }

    private async ValueTask InvokeProcessSourceItemAsync(ProcessingContext<string> ctx, CancellationToken ct)
    {
        var method = typeof(SmartPipeChannel<string, string>)
            .GetMethod("ProcessSourceItemAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var task = (ValueTask)method.Invoke(_channel, new object[] { ctx, ct })!;
        await task;
    }

    private Channel<ProcessingContext<string>>? GetInputChannel()
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_inputChannel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (Channel<ProcessingContext<string>>?)field.GetValue(_channel);
    }

    private void SetPaused(bool value)
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_isPaused", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(_channel, value);
    }

    private void InitializeChannels()
    {
        // Directly set the input and output channels via reflection
        var inputChannelField = typeof(SmartPipeChannel<string, string>)
            .GetField("_inputChannel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(inputChannelField);
        inputChannelField.SetValue(_channel, 
            Channel.CreateBounded<ProcessingContext<string>>(new BoundedChannelOptions(10)));
        
        var outputChannelField = typeof(SmartPipeChannel<string, string>)
            .GetField("_outputChannel", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(outputChannelField);
        outputChannelField.SetValue(_channel, 
            Channel.CreateBounded<ProcessingResult<string>>(new BoundedChannelOptions(10)));
    }

    private void SetCuckooFilter(CuckooFilter filter)
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_cuckooFilter", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(_channel, filter);
    }

    private class TestBackpressureStrategy : BackpressureStrategy
    {
        public bool ThrottleAsyncCalled { get; private set; }
        public int CallCount { get; private set; }
        public List<int> CallSizes { get; } = new();
        public ManualResetEventSlim? ThrottleCalledEvent { get; set; }

        public TestBackpressureStrategy(int capacity) : base(capacity) { }

        public override async ValueTask ThrottleAsync(int currentSize, CancellationToken ct)
        {
            ThrottleAsyncCalled = true;
            CallCount++;
            CallSizes.Add(currentSize);
            ThrottleCalledEvent?.Set();
            await base.ThrottleAsync(currentSize, ct);
        }
    }

    private void SetBackpressureStrategy(BackpressureStrategy strategy)
    {
        var field = typeof(SmartPipeChannel<string, string>)
            .GetField("_backpressure", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(_channel, strategy);
    }

    /// <summary>
    /// Test: Normal flow - writes to _inputChannel via WriteAsync
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_NormalFlow_WritesToInputChannel()
    {
        // Arrange
        InitializeChannels();
        var ctx = new ProcessingContext<string>("test-payload");
        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);

        // Act
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert: Item should be in the input channel
        var itemRead = inputChannel.Reader.TryRead(out var readCtx);
        Assert.True(itemRead);
        Assert.Equal(ctx.TraceId, readCtx.TraceId);
    }

    /// <summary>
    /// Test: OnProgress callback invoked
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_OnProgressCallback_Invoked()
    {
        // Arrange
        InitializeChannels();
        bool progressCalled = false;
        _options.OnProgress = (current, total, elapsed, _) => { progressCalled = true; };
        var ctx = new ProcessingContext<string>("test-payload");

        // Act
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert
        Assert.True(progressCalled);
    }

    /// <summary>
    /// Test: OnMetrics callback invoked
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_OnMetricsCallback_Invoked()
    {
        // Arrange
        InitializeChannels();
        bool metricsCalled = false;
        _options.OnMetrics = (metrics) => { metricsCalled = true; };
        var ctx = new ProcessingContext<string>("test-payload");

        // Act
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert
        Assert.True(metricsCalled);
    }

    /// <summary>
    /// Test: CuckooFilter deduplication (when Contains returns true)
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_CuckooFilterDedup_SkipsItem()
    {
        // Arrange
        InitializeChannels();
        _options.EnableFeature("CuckooFilter"); // Enable feature flag
        var ctx = new ProcessingContext<string>("test-payload");
        
        // Create a CuckooFilter and add the TraceId to trigger deduplication
        var cuckooFilter = new CuckooFilter();
        cuckooFilter.Add(ctx.TraceId);
        // Verify CuckooFilter is working
        Assert.True(cuckooFilter.Contains(ctx.TraceId));
        SetCuckooFilter(cuckooFilter);

        // Act
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert: Item should NOT be in the input channel (deduped)
        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var itemRead = inputChannel.Reader.TryRead(out _);
        Assert.False(itemRead);
    }

    /// <summary>
    /// Test: DeduplicationFilter deduplication (when ContainsAndAdd returns true)
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_DeduplicationFilterDedup_SkipsItem()
    {
        // Arrange
        InitializeChannels();
        var ctx = new ProcessingContext<string>("test-payload");
        
        // Create DeduplicationFilter and add the item first so ContainsAndAdd returns true
        var dedupFilter = new DeduplicationFilter();
        // Call ContainsAndAdd twice - first time returns false (adds), second time returns true (already seen)
        dedupFilter.ContainsAndAdd(ctx.TraceId); // First call - adds the item
        
        // Now set the filter - the method will call ContainsAndAdd again
        // Since the item was already added, the second call should return true
        _options.DeduplicationFilter = dedupFilter;

        // Act: This should be deduplicated (ContainsAndAdd returns true)
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert: Item should NOT be in the input channel (deduped)
        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        var itemRead = inputChannel.Reader.TryRead(out _);
        Assert.False(itemRead);
    }

    /// <summary>
    /// Test: Backpressure.ThrottleAsync is called under backpressure conditions.
    /// Deterministic test - uses TestBackpressureStrategy to verify the call.
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_Backpressure_ThrottleAsyncCalled()
    {
        // Arrange
        InitializeChannels();
        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);

        // Create test backpressure strategy with event for deterministic waiting
        var throttleCalledEvent = new ManualResetEventSlim(false);
        var testBackpressure = new TestBackpressureStrategy(10)
        {
            ThrottleCalledEvent = throttleCalledEvent
        };
        SetBackpressureStrategy(testBackpressure);

        // Fill the channel to near capacity (9 items, capacity 10)
        // This creates fill ratio 9/10 = 0.9, which exceeds the default target of 0.7
        for (int i = 0; i < 9; i++)
        {
            inputChannel.Writer.TryWrite(new ProcessingContext<string>($"filler-{i}"));
        }

        var ctx = new ProcessingContext<string>("test-payload");

        // Act: Write the test item (triggers backpressure ThrottleAsync)
        await InvokeProcessSourceItemAsync(ctx, _cts.Token);

        // Assert: ThrottleAsync was called (deterministic check via test double)
        Assert.True(testBackpressure.ThrottleAsyncCalled,
            "ThrottleAsync should be called when channel fill ratio exceeds target");

        // Verify the fill ratio was high enough to trigger throttling
        // At the time of ThrottleAsync call, the channel has 9 items (filler items)
        // The target fill ratio is 0.85 when throughput < 100 (default for uninitialized metrics)
        // So 9/10 = 0.9 > 0.85, which should trigger throttling
        Assert.Contains(testBackpressure.CallSizes, size => size >= 9);

        // Verify the item was written to the channel
        // Read all items from the channel and check if our test item is there
        var allItems = new List<ProcessingContext<string>>();
        while (inputChannel.Reader.TryRead(out var item))
        {
            allItems.Add(item);
        }
        Assert.Contains(allItems, item => item.TraceId == ctx.TraceId);
    }

    /// <summary>
    /// Test: Paused state - _isPaused=true → Task.Delay loop
    /// </summary>
    [Fact]
    public async Task ProcessSourceItemAsync_PausedState_WaitsForResume()
    {
        // Arrange
        InitializeChannels();
        var ctx = new ProcessingContext<string>("test-payload");
        _channel.Pause(); // Use public method instead of reflection

        var inputChannel = GetInputChannel();
        Assert.NotNull(inputChannel);
        
        // Start background reader to consume items (prevents potential hangs)
        var readerCts = new CancellationTokenSource();
        var readerTask = Task.Run(async () =>
        {
            await foreach (var _ in inputChannel.Reader.ReadAllAsync(readerCts.Token)) { }
        }, readerCts.Token);

        try
        {
            // Act: Start processing, resume after 50ms
            var sw = Stopwatch.StartNew();
            var task = InvokeProcessSourceItemAsync(ctx, _cts.Token).AsTask();
            
            // Wait 50ms then resume
            await Task.Delay(50);
            _channel.Resume(); // Use public method instead of reflection
            
            // Wait for task with timeout to prevent hangs
            var completed = await Task.WhenAny(task, Task.Delay(5000));
            sw.Stop();

            // Assert: Task completed successfully
            Assert.Same(task, completed);
            await task; // Propagate exceptions

            // Assert: Method waited at least 45ms (due to pause)
            Assert.True(sw.ElapsedMilliseconds >= 45, 
                $"Expected wait >=45ms, actual {sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            readerCts.Cancel();
            try { await readerTask; } catch (OperationCanceledException) { }
        }
    }
}
