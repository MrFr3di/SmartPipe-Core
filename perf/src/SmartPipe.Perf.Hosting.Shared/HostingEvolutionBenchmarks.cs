using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Hosting;

namespace SmartPipe.Perf.Hosting;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "Hosting")]
public class HostingEvolutionBenchmarks
{
    private HostingTarget? _target;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _target = new HostingTarget();

        var descriptorCount = HostingTarget.RegisterAndBuildProvider();
        if (descriptorCount <= 0)
            throw new InvalidOperationException("Hosting precheck built an empty service collection.");

        var hostedServiceCount = _target.ResolveHostedServiceCount();
        if (hostedServiceCount != 1)
            throw new InvalidOperationException(
                $"Hosting precheck expected exactly one IHostedService, got {hostedServiceCount}.");

        var lifecycleCount = await HostingTarget.StartStopFreshAsync().ConfigureAwait(false);
        if (lifecycleCount != 1)
            throw new InvalidOperationException(
                $"Hosting precheck expected one start/stop lifecycle, got {lifecycleCount}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _target?.Dispose();
        _target = null;
    }

    [Benchmark]
    public int RegisterAndBuildProvider() =>
        HostingTarget.RegisterAndBuildProvider();

    [Benchmark]
    public int ResolveHostedServiceGraph() =>
        Target.ResolveHostedServiceCount();

    [Benchmark]
    public Task<int> StartStopHostedPipeline() =>
        HostingTarget.StartStopFreshAsync();

    private HostingTarget Target =>
        _target ?? throw new InvalidOperationException("Hosting benchmark target is not initialized.");
}

internal sealed class BenchmarkHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _started = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _stopped = new();

    public CancellationToken ApplicationStarted => _started.Token;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => _stopped.Token;

    public void StopApplication() => _stopping.Cancel();

    public void Dispose()
    {
        _started.Dispose();
        _stopping.Dispose();
        _stopped.Dispose();
    }
}
