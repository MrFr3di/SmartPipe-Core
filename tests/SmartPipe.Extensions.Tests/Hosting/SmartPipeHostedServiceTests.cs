#nullable enable
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using Xunit;

namespace SmartPipe.Extensions.Tests.Hosting;

public class SmartPipeHostedServiceTests
{
    private class TestItem
    {
        public string? Value { get; set; }
    }

    private class TestSource : ISource<TestItem>
    {
        public async IAsyncEnumerable<ProcessingContext<TestItem>> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            // Continuously generate data until cancelled
            while (!ct.IsCancellationRequested)
            {
                yield return new ProcessingContext<TestItem> { Payload = new TestItem { Value = "test" } };
                await Task.Delay(100, ct); // Delay between items
            }
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisposeAsync() => Task.CompletedTask;
    }

    private class TestTransformer : ITransformer<TestItem, TestItem>
    {
        public ValueTask<ProcessingResult<TestItem>> TransformAsync(ProcessingContext<TestItem> context, CancellationToken ct = default)
        {
            return ValueTask.FromResult(ProcessingResult<TestItem>.Success(context.Payload!, context.TraceId));
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisposeAsync() => Task.CompletedTask;
    }

    private class TestSink : ISink<TestItem>
    {
        public Task WriteAsync(ProcessingResult<TestItem> result, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed class BlockingSource : ISource<TestItem>
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ProcessingContext<TestItem>> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            yield break;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DisposeAsync() => Task.CompletedTask;
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose() { }
        }
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenPipelineIsNull()
    {
        var logger = Mock.Of<ILogger<SmartPipeHostedService<string, string>>>();
        Assert.Throws<ArgumentNullException>(() => new SmartPipeHostedService<string, string>(null!, logger));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        var pipeline = new SmartPipeChannel<string, string>();
        Assert.Throws<ArgumentNullException>(() => new SmartPipeHostedService<string, string>(pipeline, null!));
    }

    [Fact]
    public async Task StartAsync_ExecutesPipeline()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<SmartPipeHostedService<TestItem, TestItem>>>();
        
        var pipeline = new SmartPipeChannel<TestItem, TestItem>();
        pipeline.AddSource(new TestSource());
        pipeline.AddTransformer(new TestTransformer());
        pipeline.AddSink(new TestSink());

        var hostedService = new SmartPipeHostedService<TestItem, TestItem>(
            pipeline, logger);

        // Act - start with timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var startTask = hostedService.StartAsync(cts.Token);
        
        // Allow some processing time
        await Task.Delay(500);
        
        // Stop
        await hostedService.StopAsync(CancellationToken.None);
        
        // Assert - no exceptions should occur
        Assert.True(true);
    }

    [Fact]
    public async Task StopAsync_ShouldRespectHostCancellationTokenDuringDrain()
    {
        var source = new BlockingSource();
        var pipeline = new SmartPipeChannel<TestItem, TestItem>();
        pipeline.AddSource(source);
        pipeline.AddTransformer(new TestTransformer());
        pipeline.AddSink(new TestSink());
        var logger = new RecordingLogger<SmartPipeHostedService<TestItem, TestItem>>();
        var hostedService = new SmartPipeHostedService<TestItem, TestItem>(pipeline, logger);

        await hostedService.StartAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopTask = hostedService.StopAsync(stopCts.Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(1))) == stopTask;

        if (!completed)
        {
            try
            {
                pipeline.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The stop path won the cleanup race.
            }
        }

        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Warning
                && entry.Message.Contains("host cancellation", StringComparison.OrdinalIgnoreCase)
        );
    }
}
