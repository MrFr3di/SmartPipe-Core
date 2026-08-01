using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.Hosting;

/// <summary>Registers canonical SmartPipe pipelines with the Generic Host lifecycle.</summary>
public static class SmartPipeHostingExtensions
{
    /// <summary>Adds one typed pipeline registration to the shared hosted orchestrator.</summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="registration">The canonical typed pipeline registration.</param>
    /// <param name="configure">An optional registration-only options callback.</param>
    /// <returns>The same typed registration builder.</returns>
    public static ISmartPipeRegistrationBuilder<TInput, TOutput> RunAsHostedService<TInput, TOutput>(
        this ISmartPipeRegistrationBuilder<TInput, TOutput> registration,
        Action<SmartPipeHostedPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Services);

        var options = new SmartPipeHostedPipelineOptions();
        configure?.Invoke(options);
        var snapshot = SmartPipeHostedPipelineOptionsSnapshot.Create(options);
        var existing = SmartPipeHostingInfrastructure.Find(registration.Services);
        var infrastructure = existing ?? new SmartPipeHostingInfrastructure();
        var reservation = infrastructure.Store.Reserve(
            registration.Key,
            typeof(TInput),
            typeof(TOutput));
        var descriptor = new HostedPipelineDescriptor
        {
            Key = registration.Key,
            InputType = typeof(TInput),
            OutputType = typeof(TOutput),
            Order = snapshot.Order,
            RegistrationOrder = reservation.RegistrationOrder,
            DrainTimeout = snapshot.DrainTimeout,
            FailureBehavior = snapshot.FailureBehavior,
            CompletionBehavior = snapshot.CompletionBehavior,
        };
        var descriptors = existing is null
            ? infrastructure.CreateDescriptors()
            : [];
        descriptors.Add(ServiceDescriptor.Singleton<IHostedPipelineRegistration>(provider =>
            new HostedPipelineRegistration<TInput, TOutput>(
                provider.GetRequiredKeyedService<ISmartPipeRunFactory<TInput, TOutput>>(
                    registration.Key.Value),
                descriptor)));
        var added = new List<ServiceDescriptor>(descriptors.Count);

        try
        {
            foreach (var serviceDescriptor in descriptors)
            {
                added.Add(serviceDescriptor);
                registration.Services.Add(serviceDescriptor);
            }

            infrastructure.Store.Commit(
                reservation,
                descriptor);
            return registration;
        }
        catch (Exception error)
        {
            var rollbackErrors = SmartPipeHostingInfrastructure.RollbackDescriptors(
                registration.Services,
                added);
            try
            {
                infrastructure.Store.Rollback(reservation);
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(rollbackError);
            }

            if (rollbackErrors.Count == 0)
                ExceptionDispatchInfo.Capture(error).Throw();

            throw new AggregateException([error, .. rollbackErrors]);
        }
    }
}
