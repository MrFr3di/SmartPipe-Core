#nullable enable

namespace SmartPipe.Core;

/// <summary>Envelope-aware output item produced by a pipeline run.</summary>
/// <typeparam name="T">Output payload type.</typeparam>
/// <param name="Envelope">Envelope associated with the result when available.</param>
/// <param name="Result">Typed output result.</param>
/// <remarks>
/// SmartPipe exposes one primary runtime output stream of <see cref="PipelineOutput{T}"/>.
/// </remarks>
public sealed record PipelineOutput<T>(ProcessingEnvelope<T>? Envelope, PipelineResult<T> Result);
