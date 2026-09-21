using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting;

namespace SmartPipe.Perf.Hosting;

internal sealed class HostingTarget : IDisposable
{
    private const string PipelineKey = "perf-hosting";

    private readonly ServiceProvider _provider;

    internal HostingTarget() => _provider = BuildProvider();

    internal static int RegisterAndBuildProvider()
    {
        var services = CreateServices();
        using var provider = services.BuildServiceProvider(CreateProviderOptions());
        var hosted = provider.GetServices<IHostedService>().ToArray();
        if (hosted.Length != 1)
            throw new InvalidOperationException($"Expected one hosted orchestrator, got {hosted.Length}.");

        return services.Count;
    }

    internal int ResolveHostedServiceCount() =>
        _provider.GetServices<IHostedService>().Count();

    internal static async Task<int> StartStopFreshAsync()
    {
        await using var provider = BuildProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();

        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

        for (var index = hosted.Length - 1; index >= 0; index--)
            await hosted[index].StopAsync(CancellationToken.None).ConfigureAwait(false);

        return hosted.Length;
    }

    public void Dispose() => _provider.Dispose();

    private static ServiceProvider BuildProvider() =>
        CreateServices().BuildServiceProvider(CreateProviderOptions());

    private static ServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime, BenchmarkHostApplicationLifetime>();
        services.AddScoped<EmptySource>();
        services.AddScoped<IdentityTransformer>();
        services.AddScoped<NullSink>();

        var definition = PipelineDefinitionBuilder.From(
                new PipelineKey(PipelineKey),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (context, _) =>
                        ValueTask.FromResult<IPipelineSource<int>>(
                            context.Services!.GetRequiredService<EmptySource>())))
            .Transform(
                new PipelineStageKey("identity"),
                PipelineComponent.ScopeOwned<IPipelineTransformer<int, int>>(
                    static (context, _) =>
                        ValueTask.FromResult<IPipelineTransformer<int, int>>(
                            context.Services!.GetRequiredService<IdentityTransformer>())))
            .WithRuntimeOptions(new PipelineRuntimeOptions
            {
                OutputPolicy = PipelineOutputPolicy.SuppressSuccessWhenSinkAttached,
            })
            .To(PipelineComponent.ScopeOwned<IPipelineSink<int>>(
                static (context, _) =>
                    ValueTask.FromResult<IPipelineSink<int>>(
                        context.Services!.GetRequiredService<NullSink>())));

        services.AddSmartPipe()
            .AddPipeline(definition)
            .RunAsHostedService(options =>
            {
                options.Order = 0;
                options.CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive;
                options.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Rethrow;
            });

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
