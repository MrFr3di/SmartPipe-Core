using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;
using SmartPipe.Extensions;
using SmartPipe.Extensions.DependencyInjection;

_ = typeof(ISmartPipeBuilder);
var services = new ServiceCollection();
services.AddScoped<FacadeSource>();
services.AddScoped<FacadeStage>();
services.AddScoped<FacadeSink>();
services.AddSmartPipe<int, int>(
    "facade-source",
    builder => builder.UseSource<FacadeSource>().UseStage<FacadeStage>().UseSink<FacadeSink>());
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
var run = await provider.GetRequiredService<ISmartPipeFactory<int, int>>().StartAsync();
await run.Completion;
await run.DisposeAsync();

Console.WriteLine("CONSUMER_OK dependency-injection-facade-source");

internal sealed class FacadeSource : IPipelineSource<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ProcessingEnvelope<int>> ReadEnvelopesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return ProcessingEnvelope<int>.Create(1);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FacadeStage : IPipelineTransformer<int, int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<StageResult<int>> TransformAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) =>
        ValueTask.FromResult(StageResult<int>.Success(envelope.Payload));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FacadeSink : IPipelineSink<int>
{
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask WriteAsync(ProcessingEnvelope<int> envelope, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
