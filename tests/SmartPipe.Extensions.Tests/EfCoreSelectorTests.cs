using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SmartPipe.Core;
using SmartPipe.Extensions.Selectors;
using Xunit;

namespace SmartPipe.Extensions.Tests;

public class EfCoreSelectorTests
{
    public class TestEntity { public int Id { get; set; } public string Name { get; set; } = ""; }
    
    private class TestDbContext : DbContext
    {
        public DbSet<TestEntity> TestEntities { get; set; } = null!;
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseInMemoryDatabase(Guid.NewGuid().ToString());
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenDbContextIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new EfCoreSelector<TestEntity>(null!));
    }

    [Fact]
    public void Constructor_WithLogger_SetsProperties()
    {
        using var db = new TestDbContext();
        var mockLogger = new Mock<ILogger<EfCoreSelector<TestEntity>>>();
        
        var selector = new EfCoreSelector<TestEntity>(db, mockLogger.Object);
        
        Assert.NotNull(selector);
    }

    [Fact]
    public async Task ReadAsync_WithEntities_ShouldStreamAll()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReadAsync_EmptyTable_ShouldReturnEmpty()
    {
        await using var db = new TestDbContext();
        await db.Database.EnsureCreatedAsync();
        var selector = new EfCoreSelector<TestEntity>(db);
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_WithQuery_ShouldFilterResults()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" },
            new TestEntity { Id = 3, Name = "Three" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        selector.WithQuery(set => set.Where(e => e.Id > 1));
        
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
        results.Should().OnlyContain(r => r.Payload.Id > 1);
    }

    [Fact]
    public async Task ReadAsync_WithQuery_ShouldOrderResults()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 3, Name = "Three" },
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        selector.WithQuery(set => set.OrderBy(e => e.Id));
        
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(3);
        results[0].Payload.Id.Should().Be(1);
        results[1].Payload.Id.Should().Be(2);
        results[2].Payload.Id.Should().Be(3);
    }

    [Fact]
    public async Task WithQuery_ReturnsSameSelector()
    {
        await using var db = new TestDbContext();
        var selector = new EfCoreSelector<TestEntity>(db);
        
        var result = selector.WithQuery(set => set.Where(e => e.Id > 0));
        
        Assert.Same(selector, result);
    }

    [Fact]
    public async Task InitializeAsync_CompletesWithoutError()
    {
        await using var db = new TestDbContext();
        var selector = new EfCoreSelector<TestEntity>(db);
        
        // Should not throw
        await selector.InitializeAsync();
        
        Assert.True(true);
    }

    [Fact]
    public async Task ReadAsync_LogsInformation_WhenLoggerProvided()
    {
        await using var db = new TestDbContext();
        db.TestEntities.Add(new TestEntity { Id = 1, Name = "Test" });
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<EfCoreSelector<TestEntity>>>();
        var selector = new EfCoreSelector<TestEntity>(db, mockLogger.Object);
        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("EFCore source completed")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ReadAsync_ThrowsCancellation_WhenTokenCancelled()
    {
        await using var db = new TestDbContext();
        db.TestEntities.Add(new TestEntity { Id = 1, Name = "Test" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ctx in selector.ReadAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithoutError()
    {
        await using var db = new TestDbContext();
        var selector = new EfCoreSelector<TestEntity>(db);
        
        await selector.DisposeAsync();
        
        Assert.True(true); // If we get here, test passed
    }

    [Fact]
    public async Task ReadAsync_WithCancellationTokenAlreadyCancelled_ShouldThrow()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ctx in selector.ReadAsync(cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task ReadAsync_WhenDbContextDisposed_ShouldThrow()
    {
        var db = new TestDbContext();
        db.TestEntities.Add(new TestEntity { Id = 1, Name = "Test" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        
        // Dispose the DbContext before reading
        await db.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var ctx in selector.ReadAsync())
            {
            }
        });
    }

    [Fact]
    public async Task ReadAsync_WithQuery_ChainedFilters_ShouldWork()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "Alice" },
            new TestEntity { Id = 2, Name = "Bob" },
            new TestEntity { Id = 3, Name = "Charlie" },
            new TestEntity { Id = 4, Name = "Alice" });
        await db.SaveChangesAsync();

        var selector = new EfCoreSelector<TestEntity>(db);
        selector.WithQuery(set => set.Where(e => e.Name == "Alice").OrderBy(e => e.Id));

        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
        results[0].Payload.Id.Should().Be(1);
        results[1].Payload.Id.Should().Be(4);
    }

    [Fact]
    public async Task ReadAsync_WithQuery_SelectProjection_ShouldWork()
    {
        await using var db = new TestDbContext();
        db.TestEntities.AddRange(
            new TestEntity { Id = 1, Name = "One" },
            new TestEntity { Id = 2, Name = "Two" });
        await db.SaveChangesAsync();

        // Test that WithQuery can be used with different IQueryable returns
        var selector = new EfCoreSelector<TestEntity>(db);
        selector.WithQuery(set => set.Where(e => e.Id > 0));

        var results = new List<ProcessingContext<TestEntity>>();

        await foreach (var ctx in selector.ReadAsync())
            results.Add(ctx);

        results.Should().HaveCount(2);
    }
}
