using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

/// <summary>Provides immutable snapshots of active runs.</summary>
public interface ISmartPipeRunRegistry
{
    /// <summary>Gets active runs for the exact pipeline key.</summary>
    /// <param name="pipelineKey">Pipeline key.</param>
    /// <returns>A defensive ordered snapshot.</returns>
    IReadOnlyList<SmartPipeRunSnapshot> GetActiveRuns(PipelineKey pipelineKey);
}
