using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed class SmartPipeRegistry : ISmartPipeRegistry
{
    private readonly SmartPipeRegistrationStore _store;

    internal SmartPipeRegistry(SmartPipeRegistrationStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    internal SmartPipeRegistrationStore Store => _store;

    public IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations() =>
        _store.GetRegistrations();

    public SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key) =>
        _store.GetRegistration(key);

    public bool TryGetRegistration(
        PipelineKey key,
        [NotNullWhen(true)] out SmartPipeRegistrationDescriptor? registration) =>
        _store.TryGetRegistration(key, out registration);
}
