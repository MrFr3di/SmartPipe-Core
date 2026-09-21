using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;

namespace SmartPipe.Perf.DependencyInjection;

internal sealed class DependencyInjectionTarget : IDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ISmartPipeFactory<int, int> _factory;

    internal DependencyInjectionTarget()
    {
        var services = CreateServices();
        _provider = services.BuildServiceProvider(CreateProviderOptions());
        _factory = _provider.GetRequiredService<ISmartPipeFactory<int, int>>();
    }

    internal static int RegisterAndBuildProvider()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(CreateProviderOptions());
        return services.Count;
    }

    internal object ResolveFactory() =>
        _provider.GetRequiredService<ISmartPipeFactory<int, int>>();

    internal async Task<int> StartCompleteDisposeRunAsync()
    {
        var run = await _factory.StartAsync().ConfigureAwait(false);
        await run.Completion.ConfigureAwait(false);
        await run.DisposeAsync().ConfigureAwait(false);
        return 1;
    }

    public void Dispose() => _provider.Dispose();

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddScoped<EmptySource>();
        services.AddScoped<IdentityTransformer>();
        services.AddScoped<NullSink>();
        services.AddSmartPipe<int, int>(
            "perf-di",
            builder => builder
                .UseSource<EmptySource>()
                .UseStage<IdentityTransformer>()
                .UseSink<NullSink>()
                .WithRuntimeOptions(new PipelineRuntimeOptions
                {
                    OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
                }));
        return services;
    }

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

    private sealed class IdentityTransformer : IPipelineTransformer<int, int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<StageResult<int>> TransformAsync(
            ProcessingEnvelope<int> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

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
