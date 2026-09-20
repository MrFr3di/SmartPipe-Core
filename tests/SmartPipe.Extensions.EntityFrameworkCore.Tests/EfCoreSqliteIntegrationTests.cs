#nullable enable

using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartPipe.Core;
using SmartPipe.Extensions.EntityFrameworkCore.Runtime;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

/// <summary>Proves relational behaviour against the real SQLite provider.</summary>
/// <remarks>
/// The database lives in a shared-cache in-memory database that stays alive because one keep-alive
/// connection is held open for the whole test, so every run's own connection observes the seeded rows.
/// </remarks>
public sealed class EfCoreSqliteIntegrationTests
{
    [Fact]
    public async Task NoTrackingIsTheDefaultAndTrackingIsExplicit()
    {
        await using var database = await SqliteDatabase.CreateAsync();

        var defaultEntries = await TrackedEntryCountAsync(database, new EfCoreQueryOptions { OperationName = "default" });
        var trackingEntries = await TrackedEntryCountAsync(
            database,
            new EfCoreQueryOptions { OperationName = "tracking", TrackingMode = EfCoreQueryTrackingMode.Tracking });

        defaultEntries.Should().Be(0);
        trackingEntries.Should().Be(2);
    }

    [Fact]
    public async Task PreserveQuery_KeepsTheCallersTrackingOperatorWhileTheDefaultOverridesIt()
    {
        await using var database = await SqliteDatabase.CreateAsync();

        var preserved = await TrackedEntryCountAsync(
            database,
            new EfCoreQueryOptions { OperationName = "preserve", TrackingMode = EfCoreQueryTrackingMode.PreserveQuery },
            applyCallerTracking: true);
        var overridden = await TrackedEntryCountAsync(
            database,
            new EfCoreQueryOptions { OperationName = "override" },
            applyCallerTracking: true);

        preserved.Should().Be(2);
        overridden.Should().Be(0);
    }

    [Fact]
    public async Task NoTrackingWithIdentityResolution_ReturnsOneInstancePerKey()
    {
        await using var database = await SqliteDatabase.CreateAsync();

        var withIdentity = await DuplicateProjectionAsync(
            database,
            EfCoreQueryTrackingMode.NoTrackingWithIdentityResolution);
        var withoutIdentity = await DuplicateProjectionAsync(database, EfCoreQueryTrackingMode.NoTracking);

        withIdentity.Should().HaveCount(4);
        ReferenceEquals(withIdentity[0], withIdentity[1]).Should().BeTrue();
        withoutIdentity.Should().HaveCount(4);
        ReferenceEquals(withoutIdentity[0], withoutIdentity[1]).Should().BeFalse();
    }

    [Fact]
    public async Task QueryableSource_StreamsSeededRowsInOrder()
    {
        await using var database = await SqliteDatabase.CreateAsync();
        var factory = new SqliteContextFactory(database.Options);
        await using var source = CreateQuerySource(
            factory,
            (context, activation) => context.Rows.OrderBy(row => row.Id));

        var items = await SourceReader.ReadAllAsync(source);

        items.Select(item => item.Name).Should().Equal("Ada", "Grace");
        factory.Contexts.Should().ContainSingle();
    }

    [Fact]
    public async Task EarlyBreak_DisposesTheRealContext()
    {
        await using var database = await SqliteDatabase.CreateAsync();
        var factory = new SqliteContextFactory(database.Options);
        await using var source = CreateQuerySource(
            factory,
            (context, activation) => context.Rows.OrderBy(row => row.Id));

        await foreach (var envelope in source.ReadEnvelopesAsync())
        {
            envelope.Payload.Id.Should().Be(1);
            break;
        }

        var context = factory.Contexts[0];
        var act = () => context.Rows.Count();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task ConcurrentRuns_UseDistinctRealContexts()
    {
        await using var database = await SqliteDatabase.CreateAsync();
        var factory = new SqliteContextFactory(database.Options);
        var component = EfCorePipelineComponents.QuerySource<SqliteContext, SqliteRow>(
            factory.CreateAsync,
            (context, activation) => context.Rows.OrderBy(row => row.Id),
            new EfCoreQueryOptions { OperationName = "sqlite-concurrent" });
        var definition = PipelineDefinitionBuilder
            .From(new PipelineKey("sp220-11-sqlite-concurrent"), component)
            .Build();

        await using var first = await definition.StartAsync();
        await using var second = await definition.StartAsync();
        await first.Completion;
        await second.Completion;

        factory.Contexts.Should().HaveCount(2);
        factory.Contexts[0].Should().NotBeSameAs(factory.Contexts[1]);
    }

    [Fact]
    public async Task CompiledPath_ReturnsAScalarFromTheRealProvider()
    {
        await using var database = await SqliteDatabase.CreateAsync();
        var factory = new SqliteContextFactory(database.Options);
        await using var source = new EfCoreCompiledQuerySource<SqliteContext, int>(
            factory.CreateAsync,
            (context, activation, ct) => CountRowsAsync(context, ct),
            EfCoreOptionsSnapshot.Create(new EfCoreCompiledQueryOptions { OperationName = "sqlite-compiled" }),
            TestActivation.Create("sp220-11-sqlite-compiled"),
            loggerFactory: null,
            activationCancellationToken: default);

        var items = await SourceReader.ReadAllAsync(source);

        items.Should().Equal(2);
    }

    [Fact]
    public async Task Tracking_OverridesACallerAppliedNoTrackingOperator()
    {
        await using var database = await SqliteDatabase.CreateAsync();

        var tracked = await TrackedEntryCountAsync(
            database,
            new EfCoreQueryOptions { OperationName = "tracking-override", TrackingMode = EfCoreQueryTrackingMode.Tracking },
            applyCallerNoTracking: true);
        var preserved = await TrackedEntryCountAsync(
            database,
            new EfCoreQueryOptions { OperationName = "preserve-no-tracking", TrackingMode = EfCoreQueryTrackingMode.PreserveQuery },
            applyCallerNoTracking: true);

        tracked.Should().Be(2);
        preserved.Should().Be(0);
    }

    private static async Task<int> TrackedEntryCountAsync(
        SqliteDatabase database,
        EfCoreQueryOptions options,
        bool applyCallerTracking = false,
        bool applyCallerNoTracking = false)
    {
        var factory = new SqliteContextFactory(database.Options);
        await using var source = CreateQuerySource(
            factory,
            (context, activation) => applyCallerTracking
                ? context.Rows.AsTracking().OrderBy(row => row.Id)
                : applyCallerNoTracking
                    ? context.Rows.AsNoTracking().OrderBy(row => row.Id)
                    : context.Rows.OrderBy(row => row.Id),
            options);

        var maxTracked = 0;
        await foreach (var _ in source.ReadEnvelopesAsync())
            maxTracked = Math.Max(maxTracked, factory.Contexts[0].ChangeTracker.Entries().Count());

        return maxTracked;
    }

    private static async Task<List<SqliteRow>> DuplicateProjectionAsync(
        SqliteDatabase database,
        EfCoreQueryTrackingMode trackingMode)
    {
        var factory = new SqliteContextFactory(database.Options);
        await using var source = CreateQuerySource(
            factory,
            (context, activation) => context.Rows
                .SelectMany(row => context.Rows, (outer, inner) => outer)
                .OrderBy(row => row.Id),
            new EfCoreQueryOptions { OperationName = "identity-resolution", TrackingMode = trackingMode });

        return await SourceReader.ReadAllAsync(source);
    }

    private static EfCoreQuerySource<SqliteContext, SqliteRow> CreateQuerySource(
        SqliteContextFactory factory,
        Func<SqliteContext, PipelineActivationContext, IQueryable<SqliteRow>> queryFactory,
        EfCoreQueryOptions? options = null) =>
        new(
            factory.CreateAsync,
            queryFactory,
            EfCoreOptionsSnapshot.Create(options ?? new EfCoreQueryOptions { OperationName = "sqlite-query" }),
            TestActivation.Create("sp220-11-sqlite-query"),
            loggerFactory: null,
            activationCancellationToken: default);

    private static async IAsyncEnumerable<int> CountRowsAsync(
        SqliteContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await context.Rows.CountAsync(cancellationToken);
    }

    private sealed class SqliteDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _keepAlive;

        private SqliteDatabase(SqliteConnection keepAlive, DbContextOptions<SqliteContext> options)
        {
            _keepAlive = keepAlive;
            Options = options;
        }

        internal DbContextOptions<SqliteContext> Options { get; }

        internal static async Task<SqliteDatabase> CreateAsync()
        {
            var connectionString = $"Data Source=file:sp220-11-{Guid.NewGuid():N}?mode=memory&cache=shared";
            var keepAlive = new SqliteConnection(connectionString);
            await keepAlive.OpenAsync();
            await using (var command = keepAlive.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE "Rows" (Id INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL);
                    INSERT INTO "Rows" (Id, Name) VALUES (1, 'Ada');
                    INSERT INTO "Rows" (Id, Name) VALUES (2, 'Grace');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connectionString).Options;
            return new SqliteDatabase(keepAlive, options);
        }

        public async ValueTask DisposeAsync() => await _keepAlive.DisposeAsync();
    }

    private sealed class SqliteContextFactory(DbContextOptions<SqliteContext> options)
    {
        internal List<SqliteContext> Contexts { get; } = [];

        internal ValueTask<SqliteContext> CreateAsync(PipelineActivationContext activation, CancellationToken ct)
        {
            var context = new SqliteContext(options);
            Contexts.Add(context);
            return ValueTask.FromResult(context);
        }
    }

    private sealed class SqliteContext(DbContextOptions<SqliteContext> options) : DbContext(options)
    {
        public DbSet<SqliteRow> Rows => Set<SqliteRow>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<SqliteRow>().ToTable("Rows");
    }

    private sealed class SqliteRow
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
