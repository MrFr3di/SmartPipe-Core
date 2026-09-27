#nullable enable

using SmartPipe.Core;

namespace SmartPipe.Testing;

/// <summary>Creates a fresh activation context for a named test pipeline.</summary>
public static class TestActivation
{
    /// <summary>Creates a context with the exact supplied key and a fresh run ID.</summary>
    public static PipelineActivationContext Create(string pipelineKey) =>
        new(new PipelineKey(pipelineKey), Guid.NewGuid());
}
