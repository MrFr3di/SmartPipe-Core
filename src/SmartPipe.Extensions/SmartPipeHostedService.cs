using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Hosted service for running <see cref="SmartPipeChannel{TInput, TOutput}"/> pipelines in ASP.NET Core.
/// Manages the full lifecycle: start, graceful shutdown (Drain), and stop with proper disposal.
/// Inherits from <see cref="BackgroundService"/> for standard hosting integration.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    private readonly SmartPipeChannel<TInput, TOutput> _pipeline;
    private readonly ILogger<SmartPipeHostedService<TInput, TOutput>> _logger;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeHostedService{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="pipeline">The SmartPipe pipeline to host.</param>
    /// <param name="logger">Logger for diagnostic information.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="pipeline"/> or <paramref name="logger"/> is null.</exception>
    public SmartPipeHostedService(
        SmartPipeChannel<TInput, TOutput> pipeline,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the pipeline asynchronously. Handles cancellation and graceful draining.
    /// </summary>
    /// <param name="ct">Cancellation token for stopping the pipeline.</param>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SmartPipe pipeline starting for {TInput} → {TOutput}",
            typeof(TInput).Name, typeof(TOutput).Name);

        try
        {
            await _pipeline.RunAsync(ct);
            _logger.LogInformation("SmartPipe pipeline completed normally");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("SmartPipe pipeline cancelled, draining...");
            await DrainPipelineAsync();
        }
       catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "SmartPipe pipeline failed due to invalid operation");
            throw;
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "SmartPipe pipeline failed due to unsupported operation");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Pipeline is now in Faulted state — do not throw, let health checks detect it
            _logger.LogError(ex, "SmartPipe pipeline faulted due to unhandled exception");
        }
    }

    /// <summary>
    /// Stops the hosted service by draining the pipeline, disposing resources, and then stopping the base service.
    /// </summary>
    /// <param name="ct">Cancellation token for the stop operation.</param>
    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("SmartPipe pipeline stopping, draining...");
        await DrainPipelineAsync();
        await _pipeline.DisposeAsync().ConfigureAwait(false);
        await base.StopAsync(ct);
    }

    /// <summary>
    /// Drains the pipeline with the configured timeout.
    /// </summary>
    private async Task DrainPipelineAsync()
    {
        using var drainCts = new CancellationTokenSource(DrainTimeout);
        try
        {
            await _pipeline.DrainAsync(DrainTimeout, drainCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("SmartPipe pipeline drain timed out after {Timeout}", DrainTimeout);
        }
    }
}