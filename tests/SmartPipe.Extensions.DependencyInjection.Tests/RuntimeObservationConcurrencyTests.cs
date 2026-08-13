using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection.Tests;

public sealed class RuntimeObservationConcurrencyTests
{
    [Fact]
    public async Task ActualFactoriesAndObservationsRemainConsistentAcrossTwentyKeys()
    {
        const int keyCount = 20;
        const int operationCount = 1_000;
        const int operationsPerKey = operationCount / keyCount;
        var keys = Enumerable.Range(0, keyCount)
            .Select(index => $"runtime-{index:D2}")
            .ToArray();
        var gates = keys.ToDictionary(
            key => key,
            key => new RunGate(operationsPerKey),
            StringComparer.Ordinal);

        var services = new ServiceCollection();
        var builder = services.AddSmartPipe();
        foreach (var key in keys)
        {
            var definition = PipelineDefinitionBuilder.From(
                    new PipelineKey(key),
                    PipelineComponent.ScopeOwned<IPipelineSource<int>>(
                        (_, _) => ValueTask.FromResult<IPipelineSource<int>>(new GatedSource(gates[key]))))
                .Build();
            builder.AddPipeline(definition);
        }

        await using var provider = services.BuildServiceProvider();
        var observationSource = provider.GetRequiredService<ISmartPipeRunObservationSource>();
        var factories = keys.ToDictionary(
            key => key,
            key => provider.GetRequiredKeyedService<ISmartPipeRunFactory<int, int>>(key),
            StringComparer.Ordinal);
        var activeSnapshots = new ConcurrentBag<SmartPipePipelineObservation>();
        var terminalSnapshots = new ConcurrentBag<SmartPipePipelineObservation>();
        var sequences = keys.ToDictionary(
            key => key,
            _ => new ConcurrentQueue<long>(),
            StringComparer.Ordinal);
        var captureLocks = keys.ToDictionary(
            key => key,
            _ => new object(),
            StringComparer.Ordinal);

        var started = await Task.WhenAll(Enumerable.Range(0, operationCount).Select(async index =>
        {
            var key = keys[index % keyCount];
            var run = await factories[key].StartAsync(TestContext.Current.CancellationToken);
            return (Key: key, Run: run);
        }));

        await Task.WhenAll(gates.Values.Select(static gate => gate.AllStarted));

        var activeCaptureTasks = Enumerable.Range(0, operationCount).Select(index => Task.Run(() =>
        {
            var key = keys[index % keyCount];
            lock (captureLocks[key])
            {
                var observation = observationSource.Capture(new PipelineKey(key));
                ValidateObservation(observation, key);
                activeSnapshots.Add(observation);
                if (observation.LatestTerminal is { } terminal)
                    sequences[key].Enqueue(terminal.Sequence);
            }
        })).ToArray();
        var activeCaptureAllTasks = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            foreach (var observation in observationSource.CaptureAll())
            {
                ValidateObservation(observation, observation.PipelineKey.Value);
                activeSnapshots.Add(observation);
            }
        })).ToArray();
        await Task.WhenAll(activeCaptureTasks.Concat(activeCaptureAllTasks));

        foreach (var gate in gates.Values)
            gate.Release();

        var completionTasks = started.Select(async item =>
        {
            await item.Run.Completion;
            await item.Run.DisposeAsync();
            lock (captureLocks[item.Key])
            {
                var observation = observationSource.Capture(new PipelineKey(item.Key));
                ValidateObservation(observation, item.Key);
                terminalSnapshots.Add(observation);
                if (observation.LatestTerminal is { } terminal)
                    sequences[item.Key].Enqueue(terminal.Sequence);
            }
        }).ToArray();
        var terminalCaptureTasks = Enumerable.Range(0, operationCount).Select(index => Task.Run(() =>
        {
            var key = keys[index % keyCount];
            lock (captureLocks[key])
            {
                var observation = observationSource.Capture(new PipelineKey(key));
                ValidateObservation(observation, key);
                terminalSnapshots.Add(observation);
                if (observation.LatestTerminal is { } terminal)
                    sequences[key].Enqueue(terminal.Sequence);
            }
        })).ToArray();
        var terminalCaptureAllTasks = Enumerable.Range(0, 40).Select(_ => Task.Run(() =>
        {
            foreach (var observation in observationSource.CaptureAll())
            {
                ValidateObservation(observation, observation.PipelineKey.Value);
                terminalSnapshots.Add(observation);
            }
        })).ToArray();

        await Task.WhenAll(completionTasks.Concat(terminalCaptureTasks).Concat(terminalCaptureAllTasks));

        Assert.Equal(operationCount, started.Length);
        Assert.Equal(keys, observationSource.CaptureAll().Select(item => item.PipelineKey.Value));
        Assert.NotEmpty(activeSnapshots);
        Assert.NotEmpty(terminalSnapshots);
        Assert.All(keys, key =>
        {
            var terminal = observationSource.Capture(new PipelineKey(key)).LatestTerminal;
            Assert.NotNull(terminal);
            Assert.Equal(operationsPerKey, terminal!.Sequence);
            Assert.Equal(key, terminal.Identity.PipelineKey.Value);
            Assert.All(sequences[key], sequence => Assert.InRange(sequence, 1, operationsPerKey));
            var ordered = sequences[key].ToArray();
            Assert.Equal(ordered.Order(), ordered);
        });
        Assert.All(activeSnapshots.Concat(terminalSnapshots), observation =>
        {
            Assert.Equal(observation.PipelineKey.Value, observation.LatestTerminal?.Identity.PipelineKey.Value
                ?? observation.PipelineKey.Value);
            var activeRunIds = observation.ActiveRuns.Select(run => run.Identity.RunId).ToArray();
            Assert.Equal(activeRunIds.Length, activeRunIds.Distinct().Count());
            Assert.All(observation.ActiveRuns, run =>
            {
                Assert.Equal(observation.PipelineKey, run.Identity.PipelineKey);
                Assert.NotEqual(Guid.Empty, run.Identity.RunId);
            });
        });
    }

    private static void ValidateObservation(SmartPipePipelineObservation observation, string key)
    {
        Assert.Equal(key, observation.PipelineKey.Value);
        Assert.InRange(observation.ActiveRuns.Count, 0, 1_000);
        Assert.True(observation.ActiveRuns.Count == observation.ActiveRuns.Select(run => run.Identity.RunId).Distinct().Count());
        Assert.True(observation.LatestTerminal is null or { Sequence: > 0 });
    }

    private sealed class RunGate(int expectedRuns)
    {
        private readonly TaskCompletionSource _allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        internal Task AllStarted => _allStarted.Task;

        internal void Started()
        {
            if (Interlocked.Increment(ref _started) == expectedRuns)
                _allStarted.TrySetResult();
        }

        internal void Release() => _release.TrySetResult();

        internal Task WaitForRelease(CancellationToken cancellationToken) => _release.Task.WaitAsync(cancellationToken);
    }

    private sealed class GatedSource(RunGate gate) : IPipelineSource<int>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            gate.Started();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await gate.WaitForRelease(ct).ConfigureAwait(false);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
