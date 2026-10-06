using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SmartPipe.Extensions.Hosting;

internal sealed class SmartPipeHostingInfrastructure
{
    internal SmartPipeHostedRegistrationStore Store { get; } = new();

    internal static SmartPipeHostingInfrastructure? Find(IServiceCollection services)
    {
        var markers = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeHostingInfrastructure))
            .ToArray();
        var stores = services
            .Where(static descriptor => descriptor.ServiceType == typeof(SmartPipeHostedRegistrationStore))
            .ToArray();
        var orchestrators = services
            .Where(static descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(SmartPipeHostedOrchestrator))
            .ToArray();

        if (markers.Length == 0 && stores.Length == 0 && orchestrators.Length == 0)
            return null;

        if (markers.Length != 1
            || stores.Length != 1
            || orchestrators.Length != 1
            || markers[0].ImplementationInstance is not SmartPipeHostingInfrastructure infrastructure
            || stores[0].ImplementationInstance is not SmartPipeHostedRegistrationStore store
            || !ReferenceEquals(infrastructure.Store, store)
            || orchestrators[0].Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                "SmartPipe Hosting infrastructure registrations are corrupted or split-brain.");
        }

        return infrastructure;
    }

    internal List<ServiceDescriptor> CreateDescriptors() =>
    [
        ServiceDescriptor.Singleton(this),
        ServiceDescriptor.Singleton(Store),
        ServiceDescriptor.Singleton<IHostedService, SmartPipeHostedOrchestrator>(),
    ];

    internal static List<Exception> RollbackDescriptors(
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
