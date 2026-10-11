using BenchmarkDotNet.Running;
using SmartPipe.Benchmarks;

if (args is ["--runtime-policy-controls"])
{
    foreach (var scenario in Enum.GetValues<RuntimeIoPolicyControl>())
        await new RuntimeIoPolicyControlBenchmarks { Scenario = scenario }.ProcessMixedItems();
    Console.WriteLine("Verified all six mixed policy, observer, telemetry and lineage controls.");
    return;
}

if (args is ["--runtime-resilience-diagnostics", var resiliencePath])
{
    await SmartPipe.Benchmarks.RuntimeResilienceControlBenchmarks.WriteDiagnosticsAsync(resiliencePath);
    return;
}

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
