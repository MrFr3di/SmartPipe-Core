using System.Diagnostics.CodeAnalysis;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Resolves typed run factories by pipeline key.</summary>
public interface ISmartPipeFactoryProvider
{
    /// <summary>Gets the factory registered for the exact key and type pair.</summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="key">Pipeline key.</param>
    /// <returns>The typed run factory.</returns>
    ISmartPipeRunFactory<TInput, TOutput> GetFactory<TInput, TOutput>(PipelineKey key);

    /// <summary>Attempts to get the factory registered for the exact key and type pair.</summary>
    /// <typeparam name="TInput">Pipeline input type.</typeparam>
    /// <typeparam name="TOutput">Pipeline output type.</typeparam>
    /// <param name="key">Pipeline key.</param>
    /// <param name="factory">The registered factory when found.</param>
    /// <returns><see langword="true"/> when a matching factory exists.</returns>
    bool TryGetFactory<TInput, TOutput>(
        PipelineKey key,
        [NotNullWhen(true)] out ISmartPipeRunFactory<TInput, TOutput>? factory);
}
