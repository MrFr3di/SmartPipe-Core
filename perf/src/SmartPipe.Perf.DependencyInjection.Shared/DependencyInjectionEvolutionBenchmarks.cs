using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace SmartPipe.Perf.DependencyInjection;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
public sealed class DependencyInjectionEvolutionBenchmarks
{
    private DependencyInjectionTarget? _target;

    [GlobalSetup]
    public void Setup() => _target = new DependencyInjectionTarget();

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
