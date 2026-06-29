using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartPipe.Core;

namespace SmartPipe.Extensions;

/// <summary>
/// Hosted service for running typed SmartPipe pipelines.
/// </summary>
/// <typeparam name="TInput">Pipeline input type.</typeparam>
/// <typeparam name="TOutput">Pipeline output type.</typeparam>
public class SmartPipeHostedService<TInput, TOutput> : BackgroundService
{
    private readonly ISmartPipeFactory<TInput, TOutput> _typedFactory;
    private readonly ILogger<SmartPipeHostedService<TInput, TOutput>> _logger;
    private readonly SmartPipeHostedServiceOptions _options;
    private readonly IHostApplicationLifetime? _applicationLifetime;
    private PipelineRun<TOutput>? _typedRun;

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeHostedService{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="typedFactory">Factory used to create a fresh typed runtime for the hosted run.</param>
    /// <param name="logger">Logger for diagnostic information.</param>
    public SmartPipeHostedService(
        ISmartPipeFactory<TInput, TOutput> typedFactory,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger)
        : this(
            typedFactory,
            logger,
            Options.Create(new SmartPipeHostedServiceOptions()),
            applicationLifetime: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="SmartPipeHostedService{TInput, TOutput}"/>.
    /// </summary>
    /// <param name="typedFactory">Factory used to create a fresh typed runtime for the hosted run.</param>
    /// <param name="logger">Logger for diagnostic information.</param>
    /// <param name="options">Hosted service lifecycle options.</param>
    /// <param name="applicationLifetime">Optional host application lifetime used for StopApplication behavior.</param>
    public SmartPipeHostedService(
        ISmartPipeFactory<TInput, TOutput> typedFactory,
        ILogger<SmartPipeHostedService<TInput, TOutput>> logger,
        IOptions<SmartPipeHostedServiceOptions> options,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        _typedFactory = typedFactory ?? throw new ArgumentNullException(nameof(typedFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _options.Validate();
        _applicationLifetime = applicationLifetime;
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
            _typedRun = await _typedFactory.StartAsync(ct).ConfigureAwait(false);
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
            HandlePipelineFault(ex);
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

        using var timeoutCts = new CancellationTokenSource(_options.DrainTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            timeoutCts.Token);
        try
        {
            await _typedRun.DrainAsync(_options.DrainTimeout, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Typed SmartPipe pipeline drain aborted by host cancellation");
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Typed SmartPipe pipeline drain timed out after {Timeout}", _options.DrainTimeout);
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Typed SmartPipe pipeline drain timed out after {Timeout}", _options.DrainTimeout);
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

    /// <summary>
    /// Handles a pipeline fault according to the configured failure behavior.
    /// </summary>
    /// <param name="exception">The fault that occurred while running the pipeline.</param>
    /// <exception cref="InvalidOperationException">Thrown when the failure behavior is set to stop the application but no host lifetime is available.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the configured failure behavior is invalid.</exception>
    private void HandlePipelineFault(Exception exception)
    {
        switch (_options.FailureBehavior)
        {
            case SmartPipeHostedFailureBehavior.StopApplication:
                if (_applicationLifetime is not null)
                {
                    _applicationLifetime.StopApplication();
                    return;
                }

                throw new InvalidOperationException(
                    "Hosted SmartPipe pipeline faulted and no IHostApplicationLifetime was available to stop the host.",
                    exception);
            case SmartPipeHostedFailureBehavior.Rethrow:
                ExceptionDispatchInfo.Capture(exception).Throw();
                return;
            case SmartPipeHostedFailureBehavior.MarkUnhealthyAndKeepHostAlive:
            case SmartPipeHostedFailureBehavior.Ignore:
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(SmartPipeHostedServiceOptions.FailureBehavior),
                    _options.FailureBehavior,
                    "Hosted service failure behavior is invalid.");
        }
    }
}
