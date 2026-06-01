using FluentAssertions;
using System.Runtime.CompilerServices;
using SmartPipe.Core;

namespace SmartPipe.Core.Tests.Types;

public class LegacyAdapterTests
{
    [Fact]
    public async Task LegacySourceAdapter_ShouldPreservePayloadAndMetadata()
    {
        var source = new ContextSource<string>(new ProcessingContext<string>(
            "payload",
            new Dictionary<string, string> { ["key"] = "value" }));
        var adapter = new LegacySourceAdapter<string>(source, "pipe", "run");

        var envelopes = new List<ProcessingEnvelope<string>>();
        await foreach (var envelope in adapter.ReadEnvelopesAsync())
            envelopes.Add(envelope);

        envelopes.Should().ContainSingle();
        envelopes[0].PipelineId.Should().Be("pipe");
        envelopes[0].RunId.Should().Be("run");
        envelopes[0].Payload.Should().Be("payload");
        envelopes[0].Metadata.GetString("key").Should().Be("value");
    }

    [Fact]
    public async Task LegacyTransformerAdapter_ShouldReturnStageResult()
    {
        var adapter = new LegacyTransformerAdapter<int, int>(new PassthroughTransformer<int>());
        var envelope = new ProcessingEnvelope<int>
        {
            PipelineId = "pipe",
            RunId = "run",
            TraceId = 1,
            Payload = 42,
            Metadata = MetadataBag.Empty,
            Lineage = [],
            Attempt = 0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await adapter.TransformAsync(envelope);

        result.IsValid.Should().BeTrue();
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    private sealed class ContextSource<T> : ISource<T>
    {
        private readonly ProcessingContext<T> _context;

        public ContextSource(ProcessingContext<T> context)
        {
            _context = context;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ProcessingContext<T>> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return _context;
            await Task.Yield();
        }

        public Task DisposeAsync() => Task.CompletedTask;
    }
}
