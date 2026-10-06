using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;
using SmartPipe.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.Hosting.Tests.Fakes;

internal sealed class TestSmartPipeRegistry(
    IEnumerable<SmartPipeRegistrationDescriptor> registrations) : ISmartPipeRegistry
{
    private readonly SmartPipeRegistrationDescriptor[] _registrations = [.. registrations];

    internal static TestSmartPipeRegistry FromHosted(
        IEnumerable<IHostedPipelineRegistration> registrations) =>
        new(registrations.Select(static (registration, index) => new SmartPipeRegistrationDescriptor
        {
            Key = registration.Descriptor.Key,
            InputType = registration.Descriptor.InputType,
            OutputType = registration.Descriptor.OutputType,
            DefinitionType = typeof(PipelineDefinition<int, int>),
            FactoryType = typeof(ISmartPipeRunFactory<int, int>),
            DisplayName = registration.Descriptor.Key.Value,
            RegistrationOrder = index,
            IsReusable = true,
        }));

    public IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations() => _registrations;

    public SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key) =>
        _registrations.Single(registration => registration.Key == key);

    public bool TryGetRegistration(
        PipelineKey key,
        [NotNullWhen(true)] out SmartPipeRegistrationDescriptor? registration)
    {
        registration = _registrations.SingleOrDefault(candidate => candidate.Key == key);
        return registration is not null;
    }
}
