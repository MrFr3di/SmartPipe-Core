using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;
using SmartPipe.Extensions.Hosting.Tests.Fakes;

namespace SmartPipe.Extensions.Hosting.Tests.Registration;

public sealed class SmartPipeHostingExtensionsTests
{
    [Fact]
    public void RunAsHostedService_RejectsNullRegistration()
    {
        ISmartPipeRegistrationBuilder<int, int>? registration = null;

        Assert.Throws<ArgumentNullException>(() => registration!.RunAsHostedService());
    }

    [Fact]
    public void ConfigureException_PublishesNothingAndRetrySucceeds()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(CreateDefinition("orders"));
        var before = services.ToArray();

        Assert.Throws<InjectedConfigurationException>(() =>
            registration.RunAsHostedService(_ => throw new InjectedConfigurationException()));

        Assert.Equal(before, services);
        Assert.Same(registration, registration.RunAsHostedService());
    }

    [Fact]
    public void OneRegistration_AddsOneSingletonOrchestrator()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(CreateDefinition("orders"));

        registration.RunAsHostedService();

        var descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(SmartPipeHostedOrchestrator));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void OneHundredRegistrations_StillAddOneOrchestrator()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();

        for (var index = 0; index < 100; index++)
            builder.AddPipeline(CreateDefinition($"pipeline-{index:D3}")).RunAsHostedService();

        Assert.Single(
            services,
            item => item.ServiceType == typeof(IHostedService)
                && item.ImplementationType == typeof(SmartPipeHostedOrchestrator));
        Assert.Equal(100, services.Count(item => item.ServiceType == typeof(IHostedPipelineRegistration)));
    }

    [Fact]
    public void DuplicateHostedRegistration_ThrowsWithoutMutation()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(CreateDefinition("orders"));
        registration.RunAsHostedService();
        var before = services.ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            registration.RunAsHostedService());

        Assert.Contains("orders", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, services);
    }

    [Fact]
    public void RunAsHostedService_ReturnsSameTypedBuilderWithoutStartingFactory()
    {
        var factoryCalls = 0;
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(
            CreateDefinition("orders", () => Interlocked.Increment(ref factoryCalls)));

        var returned = registration.RunAsHostedService();

        Assert.Same(registration, returned);
        Assert.Equal(0, factoryCalls);
        Assert.DoesNotContain(
            services,
            item => item.ServiceType.IsGenericType
                && item.ServiceType.GetGenericTypeDefinition() == typeof(ISmartPipeRunFactory<,>)
                && !item.IsKeyedService);
    }

    [Fact]
    public void Options_AreFrozenBeforeRegistrationPublication()
    {
        var services = new ServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(CreateDefinition("orders"));
        SmartPipeHostedPipelineOptions? configured = null;
        registration.RunAsHostedService(options =>
        {
            configured = options;
            options.Order = 7;
            options.DrainTimeout = TimeSpan.FromSeconds(8);
            options.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Ignore;
            options.CompletionBehavior = SmartPipeHostedCompletionBehavior.StopApplication;
        });

        configured!.Order = 70;
        configured.DrainTimeout = TimeSpan.FromSeconds(80);
        configured.FailureBehavior = SmartPipeHostedPipelineFailureBehavior.Rethrow;
        configured.CompletionBehavior = SmartPipeHostedCompletionBehavior.KeepHostAlive;
        using var provider = services.BuildServiceProvider();
        var descriptor = Assert.Single(provider.GetServices<IHostedPipelineRegistration>()).Descriptor;

        Assert.Equal(7, descriptor.Order);
        Assert.Equal(TimeSpan.FromSeconds(8), descriptor.DrainTimeout);
        Assert.Equal(SmartPipeHostedPipelineFailureBehavior.Ignore, descriptor.FailureBehavior);
        Assert.Equal(SmartPipeHostedCompletionBehavior.StopApplication, descriptor.CompletionBehavior);
    }

    [Fact]
    public void RegistrationTransaction_RollsBackDescriptorsAndReservation()
    {
        var services = new ThrowOnceServiceCollection();
        var registration = services.AddSmartPipe().AddPipeline(CreateDefinition("orders"));
        var before = services.ToArray();
        services.ThrowOnNextInsert = true;

        Assert.Throws<InjectedServiceCollectionException>(() =>
            registration.RunAsHostedService());

        Assert.Equal(before, services);
        Assert.Same(registration, registration.RunAsHostedService());
    }

    [Fact]
    public void Provider_ValidatesScopesAndClosedGenericRegistration()
    {
        var services = new ServiceCollection();
        using var lifetime = new RecordingHostApplicationLifetime();
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<ILogger<SmartPipeHostedOrchestrator>>(
            NullLogger<SmartPipeHostedOrchestrator>.Instance);
        services.AddSmartPipe().AddPipeline(CreateDefinition("orders")).RunAsHostedService();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.IsType<HostedPipelineRegistration<int, int>>(
            Assert.Single(provider.GetServices<IHostedPipelineRegistration>()));
        Assert.IsType<SmartPipeHostedOrchestrator>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    private static PipelineDefinition<int, int> CreateDefinition(
        string key,
        Action? onFactoryCall = null) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>((_, _) =>
                {
                    onFactoryCall?.Invoke();
                    return ValueTask.FromResult<IPipelineSource<int>>(new EmptySource());
                }))
            .Build();

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

    private sealed class InjectedConfigurationException : Exception;

    private sealed class InjectedServiceCollectionException : Exception;

    private sealed class ThrowOnceServiceCollection : Collection<ServiceDescriptor>, IServiceCollection
    {
        internal bool ThrowOnNextInsert { get; set; }

        protected override void InsertItem(int index, ServiceDescriptor item)
        {
            base.InsertItem(index, item);
            if (ThrowOnNextInsert)
            {
                ThrowOnNextInsert = false;
                throw new InjectedServiceCollectionException();
            }
        }
    }
}
