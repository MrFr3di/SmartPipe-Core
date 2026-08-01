using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Describes one typed SmartPipe registration.</summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public interface ISmartPipeRegistrationBuilder<TInput, TOutput> : ISmartPipeBuilder
{
    /// <summary>Gets the globally unique pipeline key.</summary>
    PipelineKey Key { get; }

    /// <summary>Gets the immutable pipeline definition.</summary>
    PipelineDefinition<TInput, TOutput> Definition { get; }
}
