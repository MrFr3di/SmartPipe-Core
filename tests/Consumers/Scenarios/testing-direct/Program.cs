using System.Runtime.CompilerServices;
using SmartPipe.Core;
using SmartPipe.Testing;

namespace SmartPipe.Testing.DirectConsumer;

internal static class Program
{
    private static async Task<int> Main()
    {
        var context = TestActivation.Create("testing-direct");
        await using var source = new TinySource();
        await source.InitializeAsync();
        var envelopes = await SourceReader.ReadEnvelopesAsync(source, 2);

        if (context.PipelineKey.Value != "testing-direct" ||
            context.RunId == Guid.Empty ||
            envelopes.Count != 1 ||
            envelopes[0].Payload != 42 ||
            source.DisposeCount != 0)
            return 1;

        Console.WriteLine("CONSUMER_OK testing-direct");
        return 0;
    }

    private sealed class TinySource : IPipelineSource<int>
    {
        private bool _initialized;

        public int DisposeCount { get; private set; }

        public ValueTask InitializeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _initialized = true;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_initialized)
                throw new InvalidOperationException("The source has not been initialized.");
            yield return ProcessingEnvelope<int>.Create(42);
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
