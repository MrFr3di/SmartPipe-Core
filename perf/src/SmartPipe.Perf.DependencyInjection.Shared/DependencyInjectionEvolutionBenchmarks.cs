using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace SmartPipe.Perf.DependencyInjection;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "DependencyInjection")]
public sealed class DependencyInjectionEvolutionBenchmarks
{
    private DependencyInjectionTarget? _target;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _target = new DependencyInjectionTarget();

        var descriptorCount = DependencyInjectionTarget.RegisterAndBuildProvider();
        if (descriptorCount <= 0)
            throw new InvalidOperationException("DI correctness precheck built an empty service collection.");

        if (_target.ResolveFactory() is null)
            throw new InvalidOperationException("DI correctness precheck failed to resolve a factory.");

        var completedRuns = await _target.StartCompleteDisposeRunAsync().ConfigureAwait(false);
        if (completedRuns != 1)
            throw new InvalidOperationException(
                $"DI correctness precheck expected one completed run, got {completedRuns}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _target?.Dispose();
        _target = null;
    }

    [Benchmark]
    public int RegisterAndBuildProvider() =>
        DependencyInjectionTarget.RegisterAndBuildProvider();

    [Benchmark]
    public object ResolveFactory() =>
        Target.ResolveFactory();

    [Benchmark]
    public Task<int> StartCompleteDisposeRun() =>
        Target.StartCompleteDisposeRunAsync();

    private DependencyInjectionTarget Target =>
        _target ?? throw new InvalidOperationException("Benchmark target is not initialized.");
}
