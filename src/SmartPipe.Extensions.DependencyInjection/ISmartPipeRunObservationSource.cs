using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Captures current active runs plus the latest bounded terminal observation.</summary>
/// <remarks>This source is observational state, not a durable audit log.</remarks>
public interface ISmartPipeRunObservationSource
{
    /// <summary>Captures one registered pipeline by exact key.</summary>
    /// <param name="pipelineKey">Exact registered key.</param>
    /// <returns>An immutable point-in-time observation.</returns>
    SmartPipePipelineObservation Capture(PipelineKey pipelineKey);

    /// <summary>Captures all registered pipelines in registration order.</summary>
    /// <returns>Immutable point-in-time observations.</returns>
    IReadOnlyList<SmartPipePipelineObservation> CaptureAll();
}
