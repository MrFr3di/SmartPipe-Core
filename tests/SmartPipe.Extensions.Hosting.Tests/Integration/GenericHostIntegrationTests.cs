using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.Hosting.Tests.Integration;

[Trait("Category", "HostingLifecycle")]
public sealed class GenericHostIntegrationTests
{
    [Fact]
    public async Task Host_EqualOrdersUseCanonicalDiRegistrationOrderAndReverseShutdown()
    {
        var disposalOrder = new ConcurrentQueue<string>();
        var probes = new ConcurrentBag<ScopedProbe>();
        var starts = new ConcurrentQueue<string>();
        var first = new ControlledSource("first") { InitializeObserver = starts.Enqueue };
        var second = new ControlledSource("second") { InitializeObserver = starts.Enqueue };
        var builder = CreateBuilder();
        AddScopedProbes(builder, disposalOrder, probes);
        var smartPipe = builder.Services.AddSmartPipe();
        var firstRegistration = smartPipe.AddPipeline(CreateDefinition(first));
        var secondRegistration = smartPipe.AddPipeline(CreateDefinition(second));
        secondRegistration.RunAsHostedService();
        firstRegistration.RunAsHostedService();
        using var host = builder.Build();

        var start = host.StartAsync(TestContext.Current.CancellationToken);
        var firstStarted = await Task.WhenAny(
            first.InitializeCalled.Task,
            second.InitializeCalled.Task).WaitAsync(TestContext.Current.CancellationToken);
        if (ReferenceEquals(firstStarted, first.InitializeCalled.Task))
            first.AllowInitialize.SetResult();
        else
            second.AllowInitialize.SetResult();

        var secondStarted = ReferenceEquals(firstStarted, first.InitializeCalled.Task)
            ? second.InitializeCalled.Task
            : first.InitializeCalled.Task;
        await secondStarted.WaitAsync(TestContext.Current.CancellationToken);
        first.AllowInitialize.TrySetResult();
        second.AllowInitialize.TrySetResult();
        await start;

        Assert.Equal(["first", "second"], starts);

        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["second", "first"], disposalOrder);
        Assert.All(probes, probe => Assert.True(probe.IsDisposed));
    }

    [Fact]
    public async Task Host_SequentiallyStartsSameTypeKeysAndReverseDisposesScopes()
    {
        var disposalOrder = new ConcurrentQueue<string>();
        var probes = new ConcurrentBag<ScopedProbe>();
        var orders = new ControlledSource("orders");
        var replay = new ControlledSource("replay");
        var builder = CreateBuilder();
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ServicesStartConcurrently = true;
            options.ServicesStopConcurrently = true;
        });
        builder.Services.AddScoped(_ =>
        {
            var probe = new ScopedProbe(disposalOrder);
            probes.Add(probe);
            return probe;
        });
        var smartPipe = builder.Services.AddSmartPipe();
        smartPipe.AddPipeline(CreateDefinition(replay)).RunAsHostedService(options => options.Order = 1);
        smartPipe.AddPipeline(CreateDefinition(orders)).RunAsHostedService(options => options.Order = 0);
        using var host = builder.Build();

        var start = host.StartAsync(TestContext.Current.CancellationToken);
        await orders.InitializeCalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(replay.InitializeCalled.Task.IsCompleted);
        orders.AllowInitialize.SetResult();
        await replay.InitializeCalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        replay.AllowInitialize.SetResult();
        await start;

        Assert.Single(host.Services.GetServices<IHostedService>(), service =>
            service is SmartPipeHostedOrchestrator);
        Assert.Equal(2, probes.Count);
        Assert.All(probes, probe => Assert.False(probe.IsDisposed));

        await host.StopAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(orders.ReadCompleted.Task, replay.ReadCompleted.Task)
            .WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["replay", "orders"], disposalOrder);
        Assert.All(probes, probe => Assert.True(probe.IsDisposed));
    }

    [Fact]
    public async Task Host_PartialStartFailureRollsBackActiveRunAndScopes()
    {
        var disposalOrder = new ConcurrentQueue<string>();
        var probes = new ConcurrentBag<ScopedProbe>();
        var first = new ControlledSource("first");
        var second = new ControlledSource("second")
        {
            InitializeError = new InvalidOperationException("second failed"),
        };
        var builder = CreateBuilder();
        AddScopedProbes(builder, disposalOrder, probes);
        var smartPipe = builder.Services.AddSmartPipe();
        smartPipe.AddPipeline(CreateDefinition(first)).RunAsHostedService(options => options.Order = 0);
        smartPipe.AddPipeline(CreateDefinition(second)).RunAsHostedService(options => options.Order = 1);
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<ISmartPipeRunRegistry>();

        var start = host.StartAsync(TestContext.Current.CancellationToken);
        await first.InitializeCalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.AllowInitialize.SetResult();
        await second.InitializeCalled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(registry.GetActiveRuns(new PipelineKey("first")));
        second.AllowInitialize.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        Assert.Equal("second failed", error.Message);
        Assert.Empty(registry.GetActiveRuns(new PipelineKey("first")));
        Assert.Empty(registry.GetActiveRuns(new PipelineKey("second")));
        Assert.Equal(2, probes.Count);
        Assert.All(probes, probe => Assert.True(probe.IsDisposed));
    }

    [Fact]
    public async Task Host_StartupTimeoutCancelsRegistrationAndDisposesScope()
    {
        var disposalOrder = new ConcurrentQueue<string>();
        var probes = new ConcurrentBag<ScopedProbe>();
        var source = new ControlledSource("orders");
        var builder = CreateBuilder();
        builder.Services.Configure<HostOptions>(options =>
            options.StartupTimeout = TimeSpan.FromMilliseconds(100));
        AddScopedProbes(builder, disposalOrder, probes);
        builder.Services.AddSmartPipe()
            .AddPipeline(CreateDefinition(source))
            .RunAsHostedService();
        using var host = builder.Build();
        var registry = host.Services.GetRequiredService<ISmartPipeRunRegistry>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Empty(registry.GetActiveRuns(new PipelineKey("orders")));
        Assert.Single(probes);
        Assert.All(probes, probe => Assert.True(probe.IsDisposed));
    }

    [Fact]
    public async Task Host_FiniteCompletionDefaultKeepsApplicationRunning()
    {
        var source = new ControlledSource("orders");
        source.AllowInitialize.SetResult();
        var builder = CreateBuilder();
        AddScopedProbes(builder, new(), new());
        builder.Services.AddSmartPipe()
            .AddPipeline(CreateDefinition(source))
            .RunAsHostedService();
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var orchestrator = GetOrchestrator(host);

        source.AllowReadCompletion.SetResult();
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(BackgroundServiceExceptionBehavior.StopHost, true)]
    [InlineData(BackgroundServiceExceptionBehavior.Ignore, false)]
    public async Task Host_RethrowUsesConfiguredBackgroundServiceBehavior(
        BackgroundServiceExceptionBehavior hostBehavior,
        bool expectsStop)
    {
        var source = new ControlledSource("orders")
        {
            ReadError = new InvalidOperationException("run failed"),
        };
        source.AllowInitialize.SetResult();
        var builder = CreateBuilder();
        AddScopedProbes(builder, new(), new());
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = hostBehavior);
        builder.Services.AddSmartPipe()
            .AddPipeline(CreateDefinition(source))
            .RunAsHostedService(options =>
                options.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Rethrow);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var orchestrator = GetOrchestrator(host);

        source.AllowReadCompletion.SetResult();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken));
        Assert.Equal("run failed", error.Message);
        if (expectsStop)
            await lifetime.ApplicationStopping.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectsStop, lifetime.ApplicationStopping.IsCancellationRequested);
        var stopError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StopAsync(TestContext.Current.CancellationToken));
        Assert.Same(error, stopError);
    }

    [Fact]
    public async Task Host_StopApplicationRequestsLifetimeWithGlobalIgnore()
    {
        var source = new ControlledSource("orders")
        {
            ReadError = new InvalidOperationException("run failed"),
        };
        source.AllowInitialize.SetResult();
        var builder = CreateBuilder();
        AddScopedProbes(builder, new(), new());
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        builder.Services.AddSmartPipe()
            .AddPipeline(CreateDefinition(source))
            .RunAsHostedService(options =>
                options.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication);
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var orchestrator = GetOrchestrator(host);

        source.AllowReadCompletion.SetResult();
        await orchestrator.ExecuteTask!.WaitAsync(TestContext.Current.CancellationToken);
        await lifetime.ApplicationStopping.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Host_CancelledShutdownTokenAbortsAndDoesNotWaitForUncooperativeStage()
    {
        var probes = new ConcurrentBag<ScopedProbe>();
        var source = new ControlledSource("orders") { IgnoreReadCancellation = true };
        source.AllowInitialize.SetResult();
        var builder = CreateBuilder();
        AddScopedProbes(builder, new(), probes);
        builder.Services.AddSmartPipe()
            .AddPipeline(CreateDefinition(source))
            .RunAsHostedService(options =>
                options.DrainTimeout = TimeSpan.FromMilliseconds(25));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await source.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stop = host.StopAsync(cancellation.Token);
        await source.ReadCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The source still ignores cancellation, so stopping must not wait for its disposal.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        var probe = Assert.Single(probes);
        Assert.False(probe.IsDisposed);

        source.AllowReadCompletion.SetResult();
        await probe.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_DisposedWhileAbandonedDisposalPending_ReportsCancellationAndLeaksNoTaskFailure(
        bool faultLateCleanup)
    {
        var marker = $"abandoned-cleanup-{Guid.NewGuid():N}";
        var unobserved = new ConcurrentQueue<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(error => error.ToString().Contains(marker, StringComparison.Ordinal)))
                unobserved.Enqueue(args.Exception);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var probes = new ConcurrentBag<ScopedProbe>();
            var stuck = new ControlledSource("stuck") { IgnoreReadCancellation = true };
            var cooperative = new ControlledSource("cooperative");
            stuck.AllowInitialize.SetResult();
            cooperative.AllowInitialize.SetResult();
            var builder = CreateBuilder();
            AddScopedProbes(builder, new(), probes);
            var smartPipe = builder.Services.AddSmartPipe();
            smartPipe.AddPipeline(CreateDefinition(cooperative)).RunAsHostedService(options => options.Order = 0);
            smartPipe.AddPipeline(CreateDefinition(stuck)).RunAsHostedService(options => options.Order = 1);
            var host = builder.Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            await stuck.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cooperative.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var orchestrator = GetOrchestrator(host);
            var stuckProbe = stuck.Probe!;
            if (faultLateCleanup)
                stuckProbe.DisposeError = new InvalidOperationException(marker);

            using (var stopping = new CancellationTokenSource())
            {
                await stopping.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StopAsync(stopping.Token));
            }

            // Shutdown moved on: the stuck run is abandoned, the cooperative run was still aborted
            // after it (reverse order), and nothing reports a fully cleaned-up stop. A cooperative
            // read unregisters its cancellation probe as it exits, so its completion is the signal.
            await stuck.ReadCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            await cooperative.ReadCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(stuckProbe.IsDisposed);
            Assert.Equal(HostedOrchestratorState.Faulted, orchestrator.State);

            // The root provider and logging go away while the abandoned cleanup is still pending.
            host.Dispose();
            stuck.AllowReadCompletion.SetResult();
            await stuckProbe.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.Empty(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            }));
        return builder;
    }

    private static void AddScopedProbes(
        HostApplicationBuilder builder,
        ConcurrentQueue<string> disposalOrder,
        ConcurrentBag<ScopedProbe> probes) =>
        builder.Services.AddScoped(_ =>
        {
            var probe = new ScopedProbe(disposalOrder);
            probes.Add(probe);
            return probe;
        });

    private static SmartPipeHostedOrchestrator GetOrchestrator(IHost host) =>
        Assert.IsType<SmartPipeHostedOrchestrator>(
            Assert.Single(host.Services.GetServices<IHostedService>(), service =>
                service is SmartPipeHostedOrchestrator));

    private static PipelineDefinition<int, int> CreateDefinition(ControlledSource source) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(source.Key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((context, _) =>
                {
                    var probe = context.Services!.GetRequiredService<ScopedProbe>();
                    probe.Key = source.Key;
                    source.Probe = probe;
                    return ValueTask.FromResult<IPipelineSource<int>>(source);
                }))
            .Build();

    private sealed class ControlledSource(string key) : IPipelineSource<int>
    {
        internal string Key { get; } = key;

        internal ScopedProbe? Probe { get; set; }

        internal TaskCompletionSource InitializeCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowInitialize { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource AllowReadCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReadCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? InitializeError { get; init; }

        internal Exception? ReadError { get; init; }

        internal bool IgnoreReadCancellation { get; init; }

        internal Action<string>? InitializeObserver { get; init; }

        public async ValueTask InitializeAsync(CancellationToken ct = default)
        {
            InitializeObserver?.Invoke(Key);
            InitializeCalled.TrySetResult();
            await AllowInitialize.Task.WaitAsync(ct);
            if (InitializeError is not null)
                throw InitializeError;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            using var registration = ct.Register(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                ReadCancellationObserved);
            ReadStarted.TrySetResult();
            if (IgnoreReadCancellation)
            {
                await AllowReadCompletion.Task;
            }
            else
            {
                try
                {
                    await AllowReadCompletion.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }
            }

            ReadCompleted.TrySetResult();
            if (ReadError is not null)
                throw ReadError;

            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScopedProbe(ConcurrentQueue<string> disposalOrder) : IAsyncDisposable
    {
        internal string Key { get; set; } = string.Empty;

        internal bool IsDisposed { get; private set; }

        internal TaskCompletionSource Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Exception? DisposeError { get; set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            disposalOrder.Enqueue(Key);
            Disposed.TrySetResult();
            return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
        }
    }
}

file static class GenericHostIntegrationExtensions
{
    internal static async Task WaitAsync(
        this CancellationToken token,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion);
        await completion.Task.WaitAsync(cancellationToken);
    }

}
