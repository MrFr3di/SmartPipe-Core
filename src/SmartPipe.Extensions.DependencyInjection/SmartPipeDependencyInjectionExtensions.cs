using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Registers canonical SmartPipe definitions and runtime infrastructure.</summary>
public static class SmartPipeDependencyInjectionExtensions
{
    /// <summary>Adds canonical SmartPipe infrastructure to a service collection.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>A builder over the same collection.</returns>
    public static ISmartPipeBuilder AddSmartPipe(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = FindInfrastructure(services);
        if (existing is not null)
        {
            return new SmartPipeBuilder(services, existing.Value.Store);
        }

        var store = new SmartPipeRegistrationStore();
        var registry = new SmartPipeRegistry(store);
        var runRegistry = new SmartPipeRunRegistry();
        var descriptors = new List<ServiceDescriptor>
        {
            ServiceDescriptor.Singleton(store),
            ServiceDescriptor.Singleton(registry),
            ServiceDescriptor.Singleton<ISmartPipeRegistry>(ResolveRegistry),
            ServiceDescriptor.Singleton<SmartPipeFactoryProvider>(CreateFactoryProvider),
            ServiceDescriptor.Singleton<ISmartPipeFactoryProvider>(ResolveFactoryProvider),
            ServiceDescriptor.Singleton(runRegistry),
            ServiceDescriptor.Singleton<ISmartPipeRunRegistry>(ResolveRunRegistry),
            ServiceDescriptor.Singleton<ISmartPipeMutableRunRegistry>(ResolveMutableRunRegistry),
        };
        if (!services.Any(static descriptor =>
            descriptor.ServiceType == typeof(TimeProvider) && !descriptor.IsKeyedService))
        {
            descriptors.Add(ServiceDescriptor.Singleton(TimeProvider.System));
        }

        AddDescriptorsAtomically(services, descriptors);
        return new SmartPipeBuilder(services, store);
    }

    /// <summary>Registers one immutable definition and its canonical keyed run factory.</summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="builder">Canonical SmartPipe builder.</param>
    /// <param name="definition">Immutable pipeline definition.</param>
    /// <returns>An immutable typed registration builder.</returns>
    public static ISmartPipeRegistrationBuilder<TInput, TOutput> AddPipeline<TInput, TOutput>(
        this ISmartPipeBuilder builder,
        PipelineDefinition<TInput, TOutput> definition)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(builder.Services);

        var infrastructure = FindInfrastructure(builder.Services)
            ?? throw new InvalidOperationException(
                "The service collection does not contain valid SmartPipe infrastructure.");
        if (builder is SmartPipeBuilder smartPipeBuilder
            && !ReferenceEquals(smartPipeBuilder.Store, infrastructure.Store))
        {
            throw new InvalidOperationException("The SmartPipe builder is bound to a different registration store.");
        }

        var key = definition.Key;
        if (key.IsEmpty)
        {
            throw new ArgumentException("Pipeline key must be initialized.", nameof(definition));
        }

        ServiceDescriptor[] descriptors =
        [
            ServiceDescriptor.KeyedSingleton(
                typeof(PipelineDefinition<TInput, TOutput>),
                key.Value,
                definition),
            ServiceDescriptor.KeyedSingleton<ISmartPipeRunFactory<TInput, TOutput>>(
                key.Value,
                static (provider, serviceKey) => new SmartPipeRunFactory<TInput, TOutput>(
                    provider.GetRequiredKeyedService<PipelineDefinition<TInput, TOutput>>(serviceKey),
                    provider.GetRequiredService<IServiceScopeFactory>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ISmartPipeMutableRunRegistry>())),
        ];

        var reservation = infrastructure.Store.Reserve(key);
        var added = new List<ServiceDescriptor>(descriptors.Length);
        try
        {
            foreach (var descriptor in descriptors)
            {
                added.Add(descriptor);
                builder.Services.Add(descriptor);
            }

            var registration = new SmartPipeRegistrationDescriptor
            {
                Key = key,
                InputType = typeof(TInput),
                OutputType = typeof(TOutput),
                DefinitionType = typeof(PipelineDefinition<TInput, TOutput>),
                FactoryType = typeof(ISmartPipeRunFactory<TInput, TOutput>),
                DisplayName = key.Value,
                RegistrationOrder = reservation.RegistrationOrder,
                IsReusable = definition.IsReusable,
            };
            reservation.Commit(registration);
            return new SmartPipeRegistrationBuilder<TInput, TOutput>(
                builder.Services,
                infrastructure.Store,
                key,
                definition);
        }
        catch (Exception error)
        {
            var rollbackErrors = RollbackDescriptors(builder.Services, added);
            try
            {
                reservation.Rollback();
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(rollbackError);
            }

            if (rollbackErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            throw new AggregateException([error, .. rollbackErrors]);
        }
    }

    private static ISmartPipeRegistry ResolveRegistry(IServiceProvider provider) =>
        provider.GetRequiredService<SmartPipeRegistry>();

    private static SmartPipeFactoryProvider CreateFactoryProvider(IServiceProvider provider) =>
        new(provider, provider.GetRequiredService<ISmartPipeRegistry>());

    private static ISmartPipeFactoryProvider ResolveFactoryProvider(IServiceProvider provider) =>
        provider.GetRequiredService<SmartPipeFactoryProvider>();

    private static ISmartPipeRunRegistry ResolveRunRegistry(IServiceProvider provider) =>
        provider.GetRequiredService<SmartPipeRunRegistry>();

    private static ISmartPipeMutableRunRegistry ResolveMutableRunRegistry(IServiceProvider provider) =>
        provider.GetRequiredService<SmartPipeRunRegistry>();

    private static (SmartPipeRegistrationStore Store, SmartPipeRegistry Registry)? FindInfrastructure(
        IServiceCollection services)
    {
        var storeDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeRegistrationStore))
            .ToArray();
        var registryDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeRegistry))
            .ToArray();
        var aliasDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(ISmartPipeRegistry))
            .ToArray();
        var factoryProviderDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeFactoryProvider))
            .ToArray();
        var factoryProviderAliasDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(ISmartPipeFactoryProvider))
            .ToArray();
        var runRegistryDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeRunRegistry))
            .ToArray();
        var runRegistryAliasDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(ISmartPipeRunRegistry))
            .ToArray();
        var mutableRunRegistryAliasDescriptors = services
            .Where(static descriptor => descriptor.ServiceType == typeof(ISmartPipeMutableRunRegistry))
            .ToArray();

        if (storeDescriptors.Length == 0
            && registryDescriptors.Length == 0
            && aliasDescriptors.Length == 0
            && factoryProviderDescriptors.Length == 0
            && factoryProviderAliasDescriptors.Length == 0
            && runRegistryDescriptors.Length == 0
            && runRegistryAliasDescriptors.Length == 0
            && mutableRunRegistryAliasDescriptors.Length == 0)
        {
            return null;
        }

        var aliasFactory = aliasDescriptors.Length == 1
            ? aliasDescriptors[0].ImplementationFactory
            : null;
        var factoryProviderFactory = factoryProviderDescriptors.Length == 1
            ? factoryProviderDescriptors[0].ImplementationFactory
            : null;
        var factoryProviderAliasFactory = factoryProviderAliasDescriptors.Length == 1
            ? factoryProviderAliasDescriptors[0].ImplementationFactory
            : null;
        var runRegistryAliasFactory = runRegistryAliasDescriptors.Length == 1
            ? runRegistryAliasDescriptors[0].ImplementationFactory
            : null;
        var mutableRunRegistryAliasFactory = mutableRunRegistryAliasDescriptors.Length == 1
            ? mutableRunRegistryAliasDescriptors[0].ImplementationFactory
            : null;
        if (storeDescriptors.Length != 1
            || registryDescriptors.Length != 1
            || aliasDescriptors.Length != 1
            || factoryProviderDescriptors.Length != 1
            || factoryProviderAliasDescriptors.Length != 1
            || runRegistryDescriptors.Length != 1
            || runRegistryAliasDescriptors.Length != 1
            || mutableRunRegistryAliasDescriptors.Length != 1
            || storeDescriptors[0].ImplementationInstance is not SmartPipeRegistrationStore store
            || registryDescriptors[0].ImplementationInstance is not SmartPipeRegistry registry
            || !ReferenceEquals(registry.Store, store)
            || aliasFactory is null
            || aliasFactory.Method !=
                ((Func<IServiceProvider, ISmartPipeRegistry>)ResolveRegistry).Method
            || factoryProviderFactory is null
            || factoryProviderFactory.Method !=
                ((Func<IServiceProvider, SmartPipeFactoryProvider>)CreateFactoryProvider).Method
            || factoryProviderAliasFactory is null
            || factoryProviderAliasFactory.Method !=
                ((Func<IServiceProvider, ISmartPipeFactoryProvider>)ResolveFactoryProvider).Method
            || runRegistryDescriptors[0].ImplementationInstance is not SmartPipeRunRegistry
            || runRegistryAliasFactory is null
            || runRegistryAliasFactory.Method !=
                ((Func<IServiceProvider, ISmartPipeRunRegistry>)ResolveRunRegistry).Method
            || mutableRunRegistryAliasFactory is null
            || mutableRunRegistryAliasFactory.Method !=
                ((Func<IServiceProvider, ISmartPipeMutableRunRegistry>)ResolveMutableRunRegistry).Method)
        {
            throw new InvalidOperationException("SmartPipe infrastructure registrations are corrupted or split-brain.");
        }

        if (!services.Any(static descriptor =>
            descriptor.ServiceType == typeof(TimeProvider) && !descriptor.IsKeyedService))
        {
            throw new InvalidOperationException("SmartPipe infrastructure is missing its TimeProvider registration.");
        }

        return (store, registry);
    }

    private static void AddDescriptorsAtomically(
        IServiceCollection services,
        IReadOnlyList<ServiceDescriptor> descriptors)
    {
        var added = new List<ServiceDescriptor>(descriptors.Count);
        try
        {
            foreach (var descriptor in descriptors)
            {
                added.Add(descriptor);
                services.Add(descriptor);
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = RollbackDescriptors(services, added);
            if (rollbackErrors.Count == 0)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            throw new AggregateException([error, .. rollbackErrors]);
        }
    }

    private static List<Exception> RollbackDescriptors(
        IServiceCollection services,
        IReadOnlyList<ServiceDescriptor> added)
    {
        var errors = new List<Exception>();
        for (var addedIndex = added.Count - 1; addedIndex >= 0; addedIndex--)
        {
            try
            {
                for (var serviceIndex = services.Count - 1; serviceIndex >= 0; serviceIndex--)
                {
                    if (ReferenceEquals(services[serviceIndex], added[addedIndex]))
                    {
                        services.RemoveAt(serviceIndex);
                        break;
                    }
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }
}
