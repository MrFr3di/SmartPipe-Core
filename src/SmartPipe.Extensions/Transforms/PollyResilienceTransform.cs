using Microsoft.Extensions.Logging;
using Polly;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Transformer wrapper that applies Polly resilience pipeline to any transform operation.
/// Uses Microsoft.Extensions.Resilience for Retry, Circuit Breaker, and Timeout strategies.
/// </summary>
/// <typeparam name="T">Data type.</typeparam>
public class PollyResilienceTransform<T> : ITransformer<T, T>
{
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<PollyResilienceTransform<T>>? _logger;

    public PollyResilienceTransform(ResiliencePipeline pipeline, ILogger<PollyResilienceTransform<T>>? logger = null)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async ValueTask<ProcessingResult<T>> TransformAsync(ProcessingContext<T> ctx, CancellationToken ct = default)
    {
        try
        {
            var result = await _pipeline.ExecuteAsync(
                static (ctx, ct) => ValueTask.FromResult(ProcessingResult<T>.Success(ctx.Payload, ctx.TraceId)),
                ctx,
                ct);

            _logger?.LogDebug("Polly transform succeeded for TraceId: {TraceId}", ctx.TraceId);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Polly transform failed for TraceId: {TraceId}", ctx.TraceId);
            return ProcessingResult<T>.Failure(
                new SmartPipeError($"Polly pipeline exhausted: {ex.Message}", ErrorType.Permanent, "Resilience", ex),
                ctx.TraceId);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
