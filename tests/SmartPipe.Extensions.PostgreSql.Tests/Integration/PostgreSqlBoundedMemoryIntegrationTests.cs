using Npgsql;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>
/// Collection with parallelization disabled for the bounded-memory scenarios.
/// </summary>
/// <remarks>
/// <see cref="GC.GetTotalAllocatedBytes(bool)"/> and <see cref="GC.GetTotalMemory(bool)"/> are process-wide, so the
/// measurements are only meaningful when no other test class allocates at the same time. Running this single
/// collection on its own is the documented reason for the grouping.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlBoundedMemoryCollection
{
    public const string Name = "postgresql-bounded-memory";
}

/// <summary>
/// Asserts that the COPY source streams instead of materialising the result set.
/// </summary>
/// <remarks>
/// Only ceilings are asserted: an exact allocation figure would be a property of the runtime, not of the component.
/// The bounded-memory invariant is proved by the sampled peak retained heap of a full 100k-row stream and by the allocation of
/// a consumer that stops after ten rows; the cumulative allocation is additionally checked against a derived per-row
/// budget, because cumulative allocation alone is a throughput metric and cannot show whether the result was
/// materialised. Every row carries a 500-character text value, so eager reading allocates roughly 100 MB of strings
/// before the first envelope and exceeds the early-break budget even if the mapper retains only their lengths.
/// The full-stream check separately bounds retained memory without depending on when the GC collects dead strings.
/// </remarks>
[Collection(PostgreSqlBoundedMemoryCollection.Name)]
public sealed class PostgreSqlBoundedMemoryIntegrationTests(PostgreSqlIntegrationDatabase database)
    : IClassFixture<PostgreSqlIntegrationDatabase>
{
    private const int RowCount = 100_000;

    private const int PayloadCharacters = 500;

    /// <summary>
    /// Ceiling for retained managed memory while a 100k-row COPY streams. The early-break allocation ceiling below
    /// detects eager materialisation even when mapped rows discard their original text payloads.
    /// </summary>
    private const long PeakHeapCeiling = 40L * 1024 * 1024;

    /// <summary>
    /// Per-row allocation budget. Cumulative allocation is a throughput metric, not a materialisation proof, so this
    /// budget is not fitted to an observed run: it is the stated allowance for one envelope, one transient text value
    /// and the provider's per-row buffers, and it only fails a pathological per-row cost. The materialisation proof
    /// is the peak-heap ceiling above plus the early-break ceiling below.
    /// </summary>
    private const long PerRowAllocationBudget = 4L * 1024;

    /// <summary>Ceiling for a consumer that stops after ten rows. The whole result set cannot fit here.</summary>
    private const long EarlyBreakAllocationCeiling = 8L * 1024 * 1024;

    [Fact]
    public async Task CopyOut_HundredThousandRows_StreamWithinTheHeapCeiling()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateSeedTableAsync(ct);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var peakHeap = 0L;
        var count = 0;
        var outOfOrderRows = 0;

        await using var source = await CreateSourceAsync(table);
        await source.InitializeAsync(ct);
        await foreach (var envelope in source.ReadEnvelopesAsync(ct).WithCancellation(ct))
        {
            count++;
            if (envelope.Payload.Id != count)
                outOfOrderRows++;

            if (count % 5_000 == 0)
            {
                // Measure live rows rather than garbage awaiting the runner-dependent GC schedule.
                // Collection cannot hide materialized rows: the enumerator still retains them.
                peakHeap = Math.Max(peakHeap, GC.GetTotalMemory(forceFullCollection: true));
            }
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        await source.DisposeAsync();

        Assert.Equal(RowCount, count);
        Assert.Equal(0, outOfOrderRows);
        Assert.True(
            peakHeap < PeakHeapCeiling,
            $"The retained managed heap reached {peakHeap:N0} bytes while streaming {RowCount} rows, above the "
            + $"{PeakHeapCeiling:N0}-byte ceiling, which means rows were accumulated instead of streamed.");
        Assert.True(
            allocated < RowCount * PerRowAllocationBudget,
            $"Streaming {RowCount} rows allocated {allocated:N0} bytes in total, above the derived budget of "
            + $"{PerRowAllocationBudget:N0} bytes per row ({RowCount * PerRowAllocationBudget:N0} bytes).");
    }

    [Fact]
    public async Task CopyOut_EarlyBreakAfterTenRows_DoesNotMaterialiseTheWholeResult()
    {
        database.RequireServer();
        var ct = TestContext.Current.CancellationToken;
        var table = await CreateSeedTableAsync(ct);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var consumed = 0;

        await using var source = await CreateSourceAsync(table);
        await source.InitializeAsync(ct);
        await foreach (var envelope in source.ReadEnvelopesAsync(ct).WithCancellation(ct))
        {
            Assert.Equal(consumed + 1, envelope.Payload.Id);
            consumed++;
            if (consumed == 10)
                break;
        }

        var allocated = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        await source.DisposeAsync();

        Assert.Equal(10, consumed);
        Assert.True(
            allocated < EarlyBreakAllocationCeiling,
            $"Reading ten rows of a {RowCount}-row COPY allocated {allocated:N0} bytes, above the "
            + $"{EarlyBreakAllocationCeiling:N0}-byte ceiling, which means the source read the whole result before "
            + "the first envelope was consumed.");
    }

    private async Task<string> CreateSeedTableAsync(CancellationToken ct)
    {
        var table = database.NewTable("mem");
        await database.ExecuteAsync($"CREATE TABLE {table} ({PostgreSqlCopyShape.TableDefinition})", ct);
        await database.ExecuteAsync(
            $"INSERT INTO {table} (id, name) SELECT g, repeat('x', {PayloadCharacters}) || g FROM generate_series(1, {RowCount}) g",
            ct);
        return table;
    }

    private async Task<IPipelineSource<(int Id, int NameLength)>> CreateSourceAsync(string table)
    {
        var descriptor = PostgreSqlPipelineComponents.BinaryCopySource(
            database.DataSource,
            PostgreSqlCopyShape.CopyToCommand(table),
            ReadLeanRowAsync,
            new PostgreSqlBinaryCopySourceOptions { ExpectedColumnCount = PostgreSqlCopyShape.ColumnCount });

        return await PostgreSqlComponentActivation.ActivateAsync(descriptor);
    }

    /// <summary>
    /// The measuring reader stays lean on purpose: no assertion and no record per row, so the measured allocation is
    /// dominated by the source's own streaming behaviour rather than by the scenario.
    /// </summary>
    private static async ValueTask<(int Id, int NameLength)> ReadLeanRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken ct)
    {
        var id = await exporter.ReadAsync<int>(ct);
        var name = exporter.IsNull ? null : await exporter.ReadAsync<string>(ct);
        for (var column = 2; column < columnCount; column++)
            await exporter.SkipAsync(ct);

        return (id, name?.Length ?? 0);
    }
}
