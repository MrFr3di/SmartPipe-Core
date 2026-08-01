using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;

var services = new ServiceCollection();
services.AddScoped<LegacySource>();
services.AddScoped<LegacyStage>();
services.AddScoped<LegacySink>();
services.AddSmartPipe<int, int>(
    "legacy-di-binary",
    builder => builder
        .UseSource<LegacySource>()
        .UseStage<LegacyStage>()
        .UseSink<LegacySink>());

await using var provider = services.BuildServiceProvider(
    new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
var definition = provider.GetRequiredService<ISmartPipeDefinition<int, int>>();
var healthMonitor = provider.GetRequiredService<SmartPipeRunHealthMonitor<int, int>>();

await VerifyLegacyStartAsync(new SmartPipeFactory<int, int>(scopeFactory, definition));
await VerifyLegacyStartAsync(new SmartPipeFactory<int, int>(scopeFactory, definition, healthMonitor));

Console.WriteLine("CONSUMER_OK dependency-injection-facade-binary-2.1.2");

static async Task VerifyLegacyStartAsync(ISmartPipeFactory<int, int> factory)
{
    var run = factory.Start();
    await run.Completion;
    await run.DisposeAsync();
}

internal sealed class LegacySource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return ProcessingEnvelope<int>.Create(1, "legacy-di-binary", "run", 1);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LegacyStage : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask<StageResult<int>> TransformAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class LegacySink : IPipelineSink<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask WriteAsync(
        ProcessingEnvelope<int> envelope,
        CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
