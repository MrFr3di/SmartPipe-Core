using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Hosted service for running <see cref="SmartPipeChannel{TInput, TOutput}"/> pipelines in ASP.NET Core.
/// Manages the full lifecycle: start, graceful shutdown (Drain), and stop.
/// Inherits from <see cref="BackgroundService"/> for standard hosting integration.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    private readonly SmartPipeChannel<TInput, TOutput> _pipeline;
    private readonly ILogger<SmartPipeHostedService<TInput, TOutput>> _logger;

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

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

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
    }

    /// <summary>
    /// Stops the hosted service by draining the pipeline and then stopping the base service.
    /// </summary>
    /// <param name="ct">Cancellation token for the stop operation.</param>
    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("SmartPipe pipeline stopping, draining...");
        await DrainPipelineAsync();
        await base.StopAsync(ct);
    }

    /// <summary>
    /// Drains the pipeline with the configured timeout.
    /// </summary>
    private async Task DrainPipelineAsync()
    {
        await _pipeline.DrainAsync(DrainTimeout);
    }
}
