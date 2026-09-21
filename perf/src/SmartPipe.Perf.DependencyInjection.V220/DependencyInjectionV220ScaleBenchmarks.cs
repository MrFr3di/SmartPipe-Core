using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Perf.DependencyInjection;

[MemoryDiagnoser]
[BenchmarkCategory("V220Only", "DependencyInjection", "Scale")]
public class DependencyInjectionV220ScaleBenchmarks
{
    private ServiceProvider? _provider;
    private string? _lastKey;

    [Params(1, 32, 256)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = CreateServices(KeyCount);
        _provider = services.BuildServiceProvider(CreateProviderOptions());
        _lastKey = GetKey(KeyCount - 1);

        for (var index = 0; index < KeyCount; index++)
        {
            var factory = _provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(GetKey(index));
            if (factory is null)
                throw new InvalidOperationException($"DI scale precheck failed for key index {index}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _provider?.Dispose();
        _provider = null;
        _lastKey = null;
    }

    [Benchmark]
    public int RegisterAndBuildProviderManyKeys()
    {
        var services = CreateServices(KeyCount);
        using var provider = services.BuildServiceProvider(CreateProviderOptions());
        return services.Count;
    }

    [Benchmark]
    public object ResolveLastKeyedFactory() =>
        Provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(LastKey);

    private ServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("DI scale provider is not initialized.");

    private string LastKey =>
        _lastKey ?? throw new InvalidOperationException("DI scale key is not initialized.");

    private static ServiceCollection CreateServices(int keyCount)
    {
        if (keyCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(keyCount));

        var services = new ServiceCollection();
        var smartPipe = services.AddSmartPipe();

        for (var index = 0; index < keyCount; index++)
        {
            var key = GetKey(index);
            var definition = PipelineDefinitionBuilder.From(
                    new PipelineKey(key),
                    PipelineComponent.RuntimeOwned<IPipelineSource<int>>(
                        static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
                .To(PipelineComponent.RuntimeOwned<IPipelineSink<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSink<int>>(new NullSink())));

            smartPipe.AddPipeline(definition);
        }

        return services;
    }

    private static string GetKey(int index) => $"perf-di-scale-{index:D4}";

    private static ServiceProviderOptions CreateProviderOptions() =>
        new()
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        };

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullSink : IPipelineSink<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask WriteAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
