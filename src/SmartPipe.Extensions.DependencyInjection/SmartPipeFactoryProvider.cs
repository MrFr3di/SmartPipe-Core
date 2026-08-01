using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed class SmartPipeFactoryProvider : ISmartPipeFactoryProvider
{
    private readonly IServiceProvider _services;
    private readonly ISmartPipeRegistry _registry;

    internal SmartPipeFactoryProvider(
        IServiceProvider services,
        ISmartPipeRegistry registry)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public ISmartPipeRunFactory<TInput, TOutput> GetFactory<TInput, TOutput>(PipelineKey key)
    {
        if (TryGetFactory<TInput, TOutput>(key, out var factory))
        {
            return factory;
        }

        throw new KeyNotFoundException($"No pipeline with key '{key.Value}' is registered.");
    }

    public bool TryGetFactory<TInput, TOutput>(
        PipelineKey key,
        [NotNullWhen(true)] out ISmartPipeRunFactory<TInput, TOutput>? factory)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Pipeline key must be initialized.", nameof(key));
        }

        if (!_registry.TryGetRegistration(key, out var registration))
        {
            factory = null;
            return false;
        }

        if (registration.InputType != typeof(TInput) || registration.OutputType != typeof(TOutput))
        {
            throw new InvalidOperationException(
                $"Pipeline '{key.Value}' is registered for "
                + $"'{registration.InputType}' -> '{registration.OutputType}', but "
                + $"'{typeof(TInput)}' -> '{typeof(TOutput)}' was requested.");
        }

        factory = _services.GetKeyedService<ISmartPipeRunFactory<TInput, TOutput>>(key.Value)
            ?? throw new InvalidOperationException(
                $"The keyed run factory for pipeline '{key.Value}' is missing.");
        return true;
    }
}
