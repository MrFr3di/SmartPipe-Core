using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace SmartPipe.Perf.OpenTelemetry;

public static class Program
{
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
[BenchmarkCategory("Evolution", "OpenTelemetry")]
public sealed class OpenTelemetryEvolutionBenchmarks
{
    private OpenTelemetryTarget? _target;

    [GlobalSetup]
    public void Setup()
    {
        OpenTelemetryTarget.ValidateRegistration();
        _target = new OpenTelemetryTarget();

        var descriptorCount = OpenTelemetryTarget.RegisterInstrumentation();
        if (descriptorCount <= 0)
            throw new InvalidOperationException("OpenTelemetry precheck built an empty service collection.");

        var providers = _target.ResolveProviders();
        if (providers != 2)
            throw new InvalidOperationException(
                $"OpenTelemetry precheck expected two resolved providers, got {providers}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _target?.Dispose();
        _target = null;
    }

    [Benchmark]
    public int RegisterInstrumentation() =>
        OpenTelemetryTarget.RegisterInstrumentation();

    [Benchmark]
    public int BuildResolveDisposeProviders() =>
        OpenTelemetryTarget.BuildResolveDisposeProviders();

    [Benchmark]
    public int ResolveProviders() =>
        Target.ResolveProviders();

    private OpenTelemetryTarget Target =>
        _target ?? throw new InvalidOperationException("OpenTelemetry benchmark target is not initialized.");
}
