using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace SmartPipe.Perf.HealthChecks;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "HealthChecks")]
public class HealthChecksEvolutionBenchmarks
{
    private HealthChecksTarget? _target;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _target = new HealthChecksTarget();

        var descriptorCount = HealthChecksTarget.RegisterAndBuildProvider();
        if (descriptorCount <= 0)
            throw new InvalidOperationException("Health-check precheck built an empty service collection.");

        if (_target.ResolveHealthCheckService() is null)
            throw new InvalidOperationException("Health-check precheck failed to resolve HealthCheckService.");

        var report = await _target.CheckRegisteredPipelineAsync().ConfigureAwait(false);
        if (report.Entries.Count != 1)
            throw new InvalidOperationException(
                $"Health-check precheck expected exactly one entry, got {report.Entries.Count}.");

        if (report.Status != HealthStatus.Degraded)
            throw new InvalidOperationException(
                $"Health-check precheck expected Degraded for a registered but not-started pipeline, got {report.Status}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _target?.Dispose();
        _target = null;
    }

    [Benchmark]
    public int RegisterAndBuildProvider() =>
        HealthChecksTarget.RegisterAndBuildProvider();

    [Benchmark]
    public object ResolveHealthCheckService() =>
        Target.ResolveHealthCheckService();

    [Benchmark]
    public Task<HealthReport> CheckRegisteredPipeline() =>
        Target.CheckRegisteredPipelineAsync();

    private HealthChecksTarget Target =>
        _target ?? throw new InvalidOperationException("Health-check benchmark target is not initialized.");
}
