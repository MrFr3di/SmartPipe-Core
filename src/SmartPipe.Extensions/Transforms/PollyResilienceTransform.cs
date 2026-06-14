using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Transforms;

/// <summary>
/// Transformer wrapper that applies <see cref="ResiliencePipeline"/> to transform operations.
/// Supports Retry, Circuit Breaker, Timeout, and other resilience strategies via Polly.
/// Implements <see cref="IPipelineTransformer{TInput, TOutput}"/> for pipeline integration.
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public class PollyResilienceTransform<T> : IPipelineTransformer<T, T>
{
    private readonly ResiliencePipeline _pipeline;
    private readonly ILogger<PollyResilienceTransform<T>>? _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="PollyResilienceTransform{T}"/>.
    /// </summary>
    /// <param name="pipeline">The Polly resilience pipeline to apply.</param>
    /// <param name="logger">Optional logger for diagnostic information.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipeline"/> is null.</exception>
    public PollyResilienceTransform(
        ResiliencePipeline pipeline,
        ILogger<PollyResilienceTransform<T>>? logger = null
    )
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger;
    }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public async ValueTask<StageResult<T>> TransformAsync(
        ProcessingEnvelope<T> envelope,
        CancellationToken ct = default
    )
    {
        try
        {
            var result = await _pipeline.ExecuteAsync(
                static (envelope, ct) =>
                    ValueTask.FromResult(StageResult<T>.Success(envelope.Payload)),
                envelope,
                ct
            );

            _logger?.LogDebug("Polly transform succeeded for TraceId: {TraceId}", envelope.TraceId);
            return result;
        }
        catch (Polly.Timeout.TimeoutRejectedException ex)
        {
            _logger?.LogWarning(
                ex,
                "Polly transform timed out for TraceId: {TraceId}",
                envelope.TraceId
            );
            return StageResult<T>.Failure(
                new SmartPipeError(
                    $"Polly timeout: {ex.Message}",
                    ErrorType.Transient,
                    "Resilience",
                    ex
                )
            );
        }
        catch (BrokenCircuitException ex)
        {
            _logger?.LogWarning(
                ex,
                "Polly circuit breaker opened for TraceId: {TraceId}",
                envelope.TraceId
            );
            return StageResult<T>.Failure(
                new SmartPipeError(
                    $"Polly circuit breaker: {ex.Message}",
                    ErrorType.Transient,
                    "Resilience",
                    ex
                )
            );
        }
        catch (InvalidOperationException ex)
        {
            _logger?.LogError(ex, "Polly transform failed for TraceId: {TraceId}", envelope.TraceId);
            return StageResult<T>.Failure(
                new SmartPipeError(
                    $"Polly pipeline error: {ex.Message}",
                    ErrorType.Permanent,
                    "Resilience",
                    ex
                )
            );
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
