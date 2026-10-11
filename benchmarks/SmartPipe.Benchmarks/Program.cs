using BenchmarkDotNet.Running;
using SmartPipe.Benchmarks;

if (args.Length == 2 && args[0] == "--runtime-io-latency")
{
    await RuntimeIoLatencyReport.WriteAsync(args[1]);
    return;
}

if (args.Length == 2 && args[0] == "--circuit-sampling-diagnostics")
{
    await CircuitBreakerSamplingDiagnostics.WriteAsync(args[1]);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
