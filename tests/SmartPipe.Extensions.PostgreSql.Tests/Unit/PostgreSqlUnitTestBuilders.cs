using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Tests.Unit;

/// <summary>Definition-builder shapes reused by the descriptor, purity and argument-validation tests.</summary>
internal static class PostgreSqlUnitTestBuilders
{
    internal static readonly PipelineStageKey BatchStageKey = new("to-batch");

    /// <summary>Starts a batch definition whose source rows are already complete batches.</summary>
    internal static PipelineDefinitionBuilder<IReadOnlyList<int>> CreateBatchBuilder(
        NpgsqlDataSource dataSource,
        string pipelineId) =>
        PostgreSqlPipelineDefinitionBuilder.FromBinaryCopy<IReadOnlyList<int>>(
            new PipelineKey(pipelineId),
            dataSource,
            PostgreSqlUnitTestSupport.CopyToCommand,
            static (_, _, _) => ValueTask.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
            new PostgreSqlBinaryCopySourceOptions());

    /// <summary>Starts a multi-stage batch definition: the COPY source is followed by one transform.</summary>
    internal static PipelineDefinitionBuilder<IReadOnlyList<int>, IReadOnlyList<int>> CreateStagedBatchBuilder(
        NpgsqlDataSource dataSource,
        string pipelineId) =>
        CreateBatchBuilder(dataSource, pipelineId).Transform(
            BatchStageKey,
            PipelineComponent.Borrowed<IPipelineTransformer<IReadOnlyList<int>, IReadOnlyList<int>>>(
                new PassThroughBatchTransformer()));

    /// <summary>Identity transformer used only to reach the multi-stage builder state.</summary>
    internal sealed class PassThroughBatchTransformer : IPipelineTransformer<IReadOnlyList<int>, IReadOnlyList<int>>
    {
        public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<StageResult<IReadOnlyList<int>>> TransformAsync(
            ProcessingEnvelope<IReadOnlyList<int>> envelope,
            CancellationToken ct = default) =>
            ValueTask.FromResult(StageResult<IReadOnlyList<int>>.Success(envelope.Payload));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
