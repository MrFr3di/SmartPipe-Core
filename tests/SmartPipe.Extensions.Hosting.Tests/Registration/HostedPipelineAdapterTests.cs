using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Registration;

public sealed class HostedPipelineAdapterTests
{
    [Fact]
    public async Task Registration_StartsExactFactoryOnceWithSameToken()
    {
        var run = CreateRun();
        var factory = new RecordingFactory(run);
        var registration = new HostedPipelineRegistration<int, int>(
            factory,
            CreateDescriptor("orders"));
        using var cancellation = new CancellationTokenSource();

        var hostedRun = await registration.StartAsync(cancellation.Token);

        Assert.Equal(1, factory.StartCalls);
        Assert.Equal(cancellation.Token, factory.LastToken);
        Assert.Same(run.Completion, hostedRun.Completion);
    }

    [Fact]
    public async Task RunAdapter_DelegatesCompletionStateDrainAbortAndDispose()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = PipelineRunState.Running;
        var abortCalls = 0;
        var disposeCalls = 0;
        CancellationToken abortToken = default;
        var run = CreateRun(
            completion.Task,
            () => state,
            token =>
            {
                abortCalls++;
                abortToken = token;
                return ValueTask.CompletedTask;
            },
            () =>
            {
                disposeCalls++;
                return ValueTask.CompletedTask;
            });
        var adapter = new HostedPipelineRun<int>(run);
        using var cancellation = new CancellationTokenSource();

        var drain = await adapter.TryDrainAsync(TimeSpan.FromSeconds(3), cancellation.Token);
        await adapter.AbortAsync(cancellation.Token);
        await adapter.DisposeAsync();

        Assert.Same(completion.Task, adapter.Completion);
        Assert.Equal(PipelineRunState.Running, adapter.State);
        Assert.Equal(PipelineDrainStatus.AlreadyCompleted, drain.Status);
        Assert.Equal(PipelineRunState.Running, drain.State);
        Assert.Equal(1, abortCalls);
        Assert.Equal(cancellation.Token, abortToken);
        Assert.Equal(1, disposeCalls);

        state = PipelineRunState.Aborted;
        Assert.Equal(PipelineRunState.Aborted, adapter.State);
    }

    [Fact]
    public async Task CanonicalFactory_PreservesPipelineKeyAndRunId()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe().AddPipeline(CreateDefinition("orders")).RunAsHostedService();
        await using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(provider.GetServices<IHostedPipelineRegistration>());

        var run = await registration.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new PipelineKey("orders"), run.Key);
        Assert.NotEqual(Guid.Empty, run.RunId);
        await run.Completion;
        await run.DisposeAsync();
    }

    [Fact]
    public void MissingExactKeyedFactory_FailsDeterministicallyOnResolution()
    {
        var services = new ServiceCollection();
        using var lifetime = new RecordingHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<ILogger<SmartPipeHostedOrchestrator>>(
            NullLogger<SmartPipeHostedOrchestrator>.Instance);
        var registration = new TestRegistrationBuilder(
            services,
            new PipelineKey("orders"),
            CreateDefinition("orders"));
        services.AddKeyedSingleton<ISmartPipeRunFactory<string, int>>(
            "orders",
            new WrongTypeFactory());
        registration.RunAsHostedService();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetServices<IHostedPipelineRegistration>().ToArray());
    }

    private static HostedPipelineDescriptor CreateDescriptor(string key) =>
        new()
        {
            Key = new PipelineKey(key),
            InputType = typeof(int),
            OutputType = typeof(int),
            Order = 0,
            RegistrationOrder = 0,
            DrainTimeout = TimeSpan.FromSeconds(30),
            FailureBehavior = SmartPipeHostedPipelineFailureBehavior.StopApplication,
            CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive,
        };

    private static PipelineRun<int> CreateRun(
        Task? completion = null,
        Func<PipelineRunState>? state = null,
        Func<CancellationToken, ValueTask>? abort = null,
        Func<ValueTask>? dispose = null) =>
        new(
            Channel.CreateUnbounded<PipelineOutput<int>>().Reader,
            completion ?? Task.CompletedTask,
            state ?? (static () => PipelineRunState.Running),
            abort: abort,
            dispose: dispose);

    private static PipelineDefinition<int, int> CreateDefinition(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new EmptySource())))
            .Build();

    private sealed class RecordingFactory(PipelineRun<int> run) : ISmartPipeRunFactory<int, int>
    {
        internal int StartCalls { get; private set; }

        internal CancellationToken LastToken { get; private set; }

        public Task<PipelineRun<int>> StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            LastToken = cancellationToken;
            return Task.FromResult(run);
        }
    }

    private sealed class WrongTypeFactory : ISmartPipeRunFactory<string, int>
    {
        public Task<PipelineRun<int>> StartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record TestRegistrationBuilder(
        IServiceCollection Services,
        PipelineKey Key,
        PipelineDefinition<int, int> Definition) : ISmartPipeRegistrationBuilder<int, int>;

    private sealed class EmptySource : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
