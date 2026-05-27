#nullable enable

namespace SmartPipe.Core;

/// <summary>Envelope-aware output item produced by a pipeline run.</summary>
/// <typeparam name="T">Output payload type.</typeparam>
/// <param name="Envelope">Envelope associated with the result when available.</param>
/// <param name="Result">Legacy-compatible processing result.</param>
/// <remarks>
/// SmartPipe 1.1 exposes one primary runtime output stream of <see cref="PipelineOutput{T}"/>.
/// Legacy result-only readers are projections over this stream, not independent channels.
/// </remarks>
public sealed record PipelineOutput<T>(ProcessingEnvelope<T>? Envelope, ProcessingResult<T> Result);
