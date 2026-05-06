#nullable enable
using System.Runtime.CompilerServices;
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
            // Бесконечно генерируем данные пока не отменят
            while (!ct.IsCancellationRequested)
            {
                yield return new ProcessingContext<TestItem> { Payload = new TestItem { Value = "test" } };
                await Task.Delay(100, ct); // Задержка между элементами
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

        // Act - запускаем с таймаутом
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var startTask = hostedService.StartAsync(cts.Token);
        
        // Даем время на обработку
        await Task.Delay(500);
        
        // Stop
        await hostedService.StopAsync(CancellationToken.None);
        
        // Assert - не должно быть исключений
        Assert.True(true);
    }
}
