using Microsoft.Extensions.DependencyInjection;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Exposes the service collection used to configure SmartPipe.</summary>
public interface ISmartPipeBuilder
{
    /// <summary>Gets the configured service collection.</summary>
    IServiceCollection Services { get; }
}
