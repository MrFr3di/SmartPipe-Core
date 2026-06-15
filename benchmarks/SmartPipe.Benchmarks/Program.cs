using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

var config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance));

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
