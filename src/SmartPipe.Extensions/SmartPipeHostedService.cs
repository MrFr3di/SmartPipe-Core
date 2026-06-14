using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Hosted service for running typed SmartPipe pipelines.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ISmartPipeFactory<TInput, TOutput> _typedFactory;
    private readonly ILogger<SmartPipeHostedService<TInput, TOutput>> _logger;
    private PipelineRun<TOutput>? _typedRun;

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeHostedService{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="typedFactory">Factory used to create a fresh typed runtime for the hosted run.</param>
    /// <param name="logger">Logger for diagnostic information.</param>
    public SmartPipeHostedService(
        ISmartPipeFactory<TInput, TOutput> typedFactory,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger)
    {
        _typedFactory = typedFactory ?? throw new ArgumentNullException(nameof(typedFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Typed SmartPipe pipeline starting for {TInput} -> {TOutput}",
            typeof(TInput).Name,
            typeof(TOutput).Name);

        try
        {
            _typedRun = _typedFactory.Start(ct);
            await _typedRun.Completion.ConfigureAwait(false);
            _logger.LogInformation("Typed SmartPipe pipeline completed normally");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Typed SmartPipe pipeline cancelled, draining...");
            await DrainPipelineAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Typed SmartPipe pipeline faulted due to unhandled exception");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Typed SmartPipe pipeline stopping, draining...");
        await DrainPipelineAsync(ct).ConfigureAwait(false);
        await base.StopAsync(ct).ConfigureAwait(false);
        await DisposePipelineAsync().ConfigureAwait(false);
    }

    private async Task DrainPipelineAsync(CancellationToken stoppingToken)
    {
        if (_typedRun is null)
            return;

        using var timeoutCts = new CancellationTokenSource(DrainTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timeoutCts.Token);
        try
        {
            await _typedRun.DrainAsync(DrainTimeout, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Typed SmartPipe pipeline drain aborted by host cancellation");
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Typed SmartPipe pipeline drain timed out after {Timeout}", DrainTimeout);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Typed SmartPipe pipeline drain timed out after {Timeout}", DrainTimeout);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(
                ex,
                "Typed SmartPipe pipeline drain observed pipeline cancellation");
        }
    }

    private async Task DisposePipelineAsync()
    {
        if (_typedRun is not null)
            await _typedRun.DisposeAsync().ConfigureAwait(false);
    }
}
