using Microsoft.Extensions.DependencyInjection;
using SmartPipe.Core;

namespace SmartPipe.Extensions.DependencyInjection;

internal sealed record SmartPipeBuilder(
    IServiceCollection Services,
    SmartPipeRegistrationStore Store) : ISmartPipeBuilder;

internal sealed record SmartPipeRegistrationBuilder<TInput, TOutput>(
    IServiceCollection Services,
    SmartPipeRegistrationStore Store,
    PipelineKey Key,
    PipelineDefinition<TInput, TOutput> Definition)
    : ISmartPipeRegistrationBuilder<TInput, TOutput>;
