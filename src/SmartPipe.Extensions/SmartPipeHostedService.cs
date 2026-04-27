using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Hosted service for running SmartPipe pipelines in ASP.NET Core.
/// Manages lifecycle: start, graceful shutdown (Drain), stop.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    private readonly SmartPipeChannel<TInput, TOutput> _pipeline;
    private readonly ILogger<SmartPipeHostedService<TInput, TOutput>> _logger;

    public SmartPipeHostedService(
        SmartPipeChannel<TInput, TOutput> pipeline,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            await _pipeline.DrainAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SmartPipe pipeline failed");
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("SmartPipe pipeline stopping, draining...");
        await _pipeline.DrainAsync(TimeSpan.FromSeconds(30));
        await base.StopAsync(ct);
    }
}
