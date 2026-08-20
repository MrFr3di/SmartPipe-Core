using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class RegistrationTransactionTests
{
    [Fact]
    public void AddPipeline_RegistersExactKeyedDescriptorsAndMetadata()
    {
        var services = new ServiceCollection();
        var definition = CreateDefinition<int>("orders");

        var registration = services.AddSmartPipe().AddPipeline(definition);

        Assert.Same(services, registration.Services);
        Assert.Equal(definition.Key, registration.Key);
        Assert.Same(definition, registration.Definition);

        var definitionDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(PipelineDefinition<int, int>));
        Assert.True(definitionDescriptor.IsKeyedService);
        Assert.Equal("orders", definitionDescriptor.ServiceKey);
        Assert.Same(definition, definitionDescriptor.KeyedImplementationInstance);

        var factoryDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(ISmartPipeRunFactory<int, int>));
        Assert.True(factoryDescriptor.IsKeyedService);
        Assert.Equal("orders", factoryDescriptor.ServiceKey);

        Assert.DoesNotContain(
            services,
            descriptor => !descriptor.IsKeyedService
                && (descriptor.ServiceType == typeof(PipelineDefinition<int, int>)
                    || descriptor.ServiceType == typeof(ISmartPipeRunFactory<int, int>)));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ISmartPipeRegistry>();
        Assert.Same(
            definition,
            provider.GetRequiredKeyedService<PipelineDefinition<int, int>>("orders"));
        Assert.IsType<SmartPipeRunFactory<int, int>>(
            provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("orders"));
        Assert.Null(provider.GetService<PipelineDefinition<int, int>>());
        Assert.Null(provider.GetService<ISmartPipeRunFactory<int, int>>());
        var descriptor = Assert.Single(registry.GetRegistrations());
        Assert.Equal(definition.Key, descriptor.Key);
        Assert.Equal(typeof(int), descriptor.InputType);
        Assert.Equal(typeof(int), descriptor.OutputType);
        Assert.Equal(typeof(PipelineDefinition<int, int>), descriptor.DefinitionType);
        Assert.Equal(typeof(ISmartPipeRunFactory<int, int>), descriptor.FactoryType);
        Assert.Equal("orders", descriptor.DisplayName);
        Assert.Equal(0, descriptor.RegistrationOrder);
        Assert.Equal(definition.IsReusable, descriptor.IsReusable);
        Assert.Same(descriptor, registry.GetRegistration(definition.Key));
    }

    [Fact]
    public async Task KeyedFactory_ResolvesTheCurrentDefinitionForItsExactServiceKey()
    {
        var services = new ServiceCollection();
        var registered = CreateValueDefinition("orders", 1);
        var replacement = CreateValueDefinition("orders", 2);
        services.AddSmartPipe().AddPipeline(registered);
        services.AddKeyedSingleton("orders", replacement);
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>("orders");

        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        var output = await run.Outputs.ReadAsync(TestContext.Current.CancellationToken);
        await run.Completion;

        Assert.Same(replacement, provider.GetRequiredKeyedService<PipelineDefinition<int, int>>("orders"));
        Assert.Equal(2, output.Result.Value);
    }

    [Fact]
    public void AddPipeline_RejectsDuplicateKeyAcrossGenericPairsWithoutMutation()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        builder.AddPipeline(CreateDefinition<int>("shared"));

        Assert.Throws<InvalidOperationException>(() =>
        {
            builder.AddPipeline(CreateDefinition<string>("shared"));
        });

        using var provider = services.BuildServiceProvider();
        var registration = Assert.Single(
            provider.GetRequiredService<ISmartPipeRegistry>().GetRegistrations());
        Assert.Equal(typeof(int), registration.InputType);
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(PipelineDefinition<string, string>));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(ISmartPipeRunFactory<string, string>));
    }

    [Fact]
    public void AddPipeline_WhenSecondKeyedDescriptorThrows_RollsBackAndRetrySucceeds()
    {
        var services = new ThrowOnceOnSecondKeyedDescriptorCollection();
        var builder = services.AddSmartPipe();
        var definition = CreateDefinition<int>("retry");

        Assert.Throws<InjectedServiceCollectionException>(() =>
        {
            builder.AddPipeline(definition);
        });

        Assert.DoesNotContain(services, descriptor => descriptor.IsKeyedService);
        using (var failedProvider = services.BuildServiceProvider())
        {
            Assert.Empty(failedProvider.GetRequiredService<ISmartPipeRegistry>().GetRegistrations());
        }

        var registration = builder.AddPipeline(definition);

        Assert.Equal(definition.Key, registration.Key);
        Assert.Equal(2, services.Count(descriptor => descriptor.IsKeyedService));
        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetRequiredService<ISmartPipeRegistry>().GetRegistrations());
    }

    [Fact]
    public async Task AddPipeline_CommitAndRollbackReleaseReservationsForAnotherThread()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        builder.AddPipeline(CreateDefinition<int>("committed"));

        var committingThread = Environment.CurrentManagedThreadId;
        var committed = await Task.Factory.StartNew(
            () => (ThreadId: Environment.CurrentManagedThreadId,
                Registration: builder.AddPipeline(CreateDefinition<int>("after-commit"))),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotEqual(committingThread, committed.ThreadId);
        Assert.Equal("after-commit", committed.Registration.Key.Value);

        var rollbackServices = new ThrowOnceOnSecondKeyedDescriptorCollection();
        var rollbackBuilder = rollbackServices.AddSmartPipe();
        Assert.Throws<InjectedServiceCollectionException>(() =>
        {
            rollbackBuilder.AddPipeline(CreateDefinition<int>("rolled-back"));
        });

        var rollingBackThread = Environment.CurrentManagedThreadId;
        var rolledBack = await Task.Factory.StartNew(
            () => (ThreadId: Environment.CurrentManagedThreadId,
                Registration: rollbackBuilder.AddPipeline(CreateDefinition<int>("after-rollback"))),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotEqual(rollingBackThread, rolledBack.ThreadId);
        Assert.Equal("after-rollback", rolledBack.Registration.Key.Value);
    }

    [Fact]
    public void AddSmartPipe_WhenCollectionThrowsAfterInfrastructureInsertion_RollsBackAndRetrySucceeds()
    {
        var services = new ThrowOnceAfterSecondInsertionCollection();

        Assert.Throws<InjectedServiceCollectionException>(() =>
        {
            services.AddSmartPipe();
        });

        Assert.Empty(services);
        var builder = services.AddSmartPipe();
        Assert.Same(services, builder.Services);
        using var provider = services.BuildServiceProvider();
        Assert.Same(
            provider.GetRequiredService<SmartPipeRegistry>(),
            provider.GetRequiredService<ISmartPipeRegistry>());
        Assert.Same(
            provider.GetRequiredService<SmartPipeRunRegistry>(),
            provider.GetRequiredService<ISmartPipeRunRegistry>());
        Assert.Same(
            provider.GetRequiredService<SmartPipeRunRegistry>(),
            provider.GetRequiredService<ISmartPipeMutableRunRegistry>());
        Assert.Same(
            provider.GetRequiredService<SmartPipeFactoryProvider>(),
            provider.GetRequiredService<ISmartPipeFactoryProvider>());
    }

    [Fact]
    public void Registry_IsOrdinalCaseSensitiveAndReturnsDefensiveOrderedSnapshots()
    {
        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        builder.AddPipeline(CreateDefinition<int>("orders"));
        builder.AddPipeline(CreateDefinition<int>("Orders"));

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ISmartPipeRegistry>();
        var registrations = registry.GetRegistrations();

        Assert.Equal(["orders", "Orders"], registrations.Select(item => item.Key.Value));
        Assert.Equal([0, 1], registrations.Select(item => item.RegistrationOrder));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SmartPipeRegistrationDescriptor>)registrations).Add(registrations[0]));
        Assert.True(registry.TryGetRegistration(new PipelineKey("Orders"), out var registration));
        Assert.Equal("Orders", registration.Key.Value);
        Assert.False(registry.TryGetRegistration(new PipelineKey("ORDERS"), out registration));
        Assert.Null(registration);
        Assert.Throws<KeyNotFoundException>(() => registry.GetRegistration(new PipelineKey("missing")));
        Assert.Throws<ArgumentException>(() => registry.GetRegistration(default));
        Assert.Throws<ArgumentException>(() => registry.TryGetRegistration(default, out _));
    }

    [Fact]
    public void AddSmartPipe_IsIdempotentPreservesTimeProviderAndAliasesOneRegistryInstance()
    {
        var services = new ServiceCollection();
        var timeProvider = new FixedTimeProvider();
        services.AddSingleton<TimeProvider>(timeProvider);

        var first = services.AddSmartPipe();
        var second = services.AddSmartPipe();

        Assert.Same(services, first.Services);
        Assert.Same(services, second.Services);
        using var provider = services.BuildServiceProvider();
        Assert.Same(timeProvider, provider.GetRequiredService<TimeProvider>());
        Assert.Same(
            provider.GetRequiredService<SmartPipeRegistry>(),
            provider.GetRequiredService<ISmartPipeRegistry>());
    }

    [Fact]
    public async Task AddSmartPipe_KeyedTimeProviderDoesNotSuppressUnkeyedDefault()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<TimeProvider>("custom", TimeProvider.System);
        var definition = CreateDefinition<int>("keyed-time-provider");

        services.AddSmartPipe().AddPipeline(definition);

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(TimeProvider) && !descriptor.IsKeyedService);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());

        var factory = provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(definition.Key.Value);
        var run = await factory.StartAsync(TestContext.Current.CancellationToken);
        await run.Completion;
    }

    [Fact]
    public void AddSmartPipe_WhenRequiredTimeProviderWasRemoved_RejectsCorruption()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe();
        var timeProvider = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(TimeProvider));
        services.Remove(timeProvider);

        Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddSmartPipe();
        });
    }

    [Fact]
    public void AddSmartPipe_WhenRegistryUsesAnotherStore_RejectsSplitBrain()
    {
        var services = new ServiceCollection();
        services.AddSmartPipe();
        var registryDescriptor = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(SmartPipeRegistry));
        services.Remove(registryDescriptor);
        services.AddSingleton(new SmartPipeRegistry(new SmartPipeRegistrationStore()));

        Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddSmartPipe();
        });
    }

    private static PipelineDefinition<T, T> CreateDefinition<T>(string key) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<T>>(
                    static (_, _) => ValueTask.FromResult<IPipelineSource<T>>(new EmptySource<T>())))
            .Build();

    private static PipelineDefinition<int, int> CreateValueDefinition(string key, int value) =>
        PipelineDefinitionBuilder.From(
                new PipelineKey(key),
                PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                    (_, _) => ValueTask.FromResult<IPipelineSource<int>>(
                        new ValueSource(value))))
            .Build();

    private sealed class EmptySource<T> : IPipelineSource<T>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ValueSource : IPipelineSource<int>
    {
        private readonly int _value;

        internal ValueSource(int value) => _value = value;

        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return ProcessingEnvelope<int>.Create(_value);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider : TimeProvider;

    private sealed class InjectedServiceCollectionException : Exception;

    private sealed class ThrowOnceOnSecondKeyedDescriptorCollection
        : Collection<ServiceDescriptor>, IServiceCollection
    {
        private int _keyedInsertions;

        protected override void InsertItem(int index, ServiceDescriptor item)
        {
            if (item.IsKeyedService && Interlocked.Increment(ref _keyedInsertions) == 2)
            {
                base.InsertItem(index, item);
                throw new InjectedServiceCollectionException();
            }

            base.InsertItem(index, item);
        }
    }

    private sealed class ThrowOnceAfterSecondInsertionCollection
        : Collection<ServiceDescriptor>, IServiceCollection
    {
        private int _insertions;

        protected override void InsertItem(int index, ServiceDescriptor item)
        {
            base.InsertItem(index, item);
            if (Interlocked.Increment(ref _insertions) == 2)
            {
                throw new InjectedServiceCollectionException();
            }
        }
    }
}
