using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides immutable registration metadata and exact-key lookup.</summary>
public interface ISmartPipeRegistry
{
    /// <summary>Gets a defensive snapshot in successful registration order.</summary>
    /// <returns>Registered pipeline metadata.</returns>
    IReadOnlyList<SmartPipeRegistrationDescriptor> GetRegistrations();

    /// <summary>Gets registration metadata for an exact key.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <returns>Registration metadata.</returns>
    SmartPipeRegistrationDescriptor GetRegistration(PipelineKey key);

    /// <summary>Attempts to get registration metadata for an exact key.</summary>
    /// <param name="key">Pipeline key.</param>
    /// <param name="registration">Registration metadata when found.</param>
    /// <returns><see langword="true"/> when the key is registered.</returns>
    bool TryGetRegistration(
        PipelineKey key,
        [NotNullWhen(true)] out SmartPipeRegistrationDescriptor? registration);
}
