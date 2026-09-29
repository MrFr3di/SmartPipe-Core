#nullable enable

using System.Collections;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Tests;

/// <summary>Entity type used by the provider-neutral doubles.</summary>
internal sealed class TestRow
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A queryable that is also an async sequence, so the source can enumerate it without a provider.</summary>
/// <remarks>
/// The double records how often the sequence was created, advanced, and disposed, and it can be configured
/// to fail at a chosen point. It deliberately does not implement an Entity Framework Core query provider,
/// which keeps these tests provider-neutral.
/// </remarks>
internal sealed class RecordingQueryable<T> : IQueryable<T>, IAsyncEnumerable<T>
{
    private readonly IReadOnlyList<T> _items;

    internal RecordingQueryable(IReadOnlyList<T> items, string identity)
    {
        _items = items;
        Identity = identity;
    }

    internal string Identity { get; }

    internal int EnumeratorCount { get; private set; }

    internal int MoveNextCount { get; private set; }

    internal int DisposeCount { get; private set; }

    internal Exception? MoveNextFailure { get; init; }

    internal Exception? DisposeFailure { get; init; }

    public Type ElementType => typeof(T);

    public Expression Expression => Expression.Constant(this);

    public IQueryProvider Provider => new RecordingQueryProvider(this);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        EnumeratorCount++;
        return new Enumerator(this);
    }

    private sealed class Enumerator : IAsyncEnumerator<T>
    {
        private readonly RecordingQueryable<T> _owner;
        private int _index = -1;

        internal Enumerator(RecordingQueryable<T> owner) => _owner = owner;

        public T Current => _owner._items[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            _owner.MoveNextCount++;
            if (_owner.MoveNextFailure is not null)
                return ValueTask.FromException<bool>(_owner.MoveNextFailure);

            _index++;
            return ValueTask.FromResult(_index < _owner._items.Count);
        }

        public ValueTask DisposeAsync()
        {
            _owner.DisposeCount++;
            if (_owner.DisposeFailure is not null)
                return ValueTask.FromException(_owner.DisposeFailure);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingQueryProvider : IQueryProvider
    {
        private readonly RecordingQueryable<T> _owner;

        internal RecordingQueryProvider(RecordingQueryable<T> owner) => _owner = owner;

        public IQueryable CreateQuery(Expression expression) => _owner;

        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
            throw new NotSupportedException("The provider-neutral double never rewrites a query.");

        public object? Execute(Expression expression) => _owner._items;

        public TResult Execute<TResult>(Expression expression) =>
            throw new NotSupportedException("The provider-neutral double never executes a query.");
    }
}

/// <summary>A context double that counts creation and disposal and can fail on disposal.</summary>
internal sealed class TestDbContext : DbContext
{
    internal TestDbContext(DbContextOptions<TestDbContext> options, bool failOnDispose = false)
        : base(options)
    {
        FailOnDispose = failOnDispose;
    }

    internal bool FailOnDispose { get; }

    internal int DisposeCount { get; private set; }

    internal bool Disposed { get; private set; }

    public DbSet<TestRow> Rows => Set<TestRow>();

    public override void Dispose()
    {
        DisposeCount++;
        Disposed = true;
        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        DisposeCount++;
        Disposed = true;
        if (FailOnDispose)
            throw new InvalidOperationException("The context double failed to dispose.");

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An async context factory double that records every call and can fail or return null.</summary>
internal sealed class RecordingContextFactory
{
    private readonly Func<TestDbContext> _create;

    internal RecordingContextFactory(Func<TestDbContext>? create = null) =>
        _create = create ?? (() => new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().Options));

    internal int CallCount { get; private set; }

    internal List<TestDbContext> Contexts { get; } = [];

    internal Exception? Failure { get; init; }

    internal bool ReturnNull { get; init; }

    internal ValueTask<TestDbContext> CreateAsync(PipelineActivationContext activation, CancellationToken ct)
    {
        CallCount++;
        if (Failure is not null)
            throw Failure;
        if (ReturnNull)
            return ValueTask.FromResult<TestDbContext>(null!);

        var context = _create();
        Contexts.Add(context);
        return ValueTask.FromResult(context);
    }
}

/// <summary>A logger factory that captures every rendered message for payload-free log assertions.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    internal List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _messages;

        internal RecordingLogger(List<string> messages) => _messages = messages;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _messages.Add(formatter(state, exception));
    }
}

/// <summary>Builds the activation context used by every provider-neutral test.</summary>
internal static class TestActivation
{
    internal static PipelineActivationContext Create(string key = "sp220-11-tests") =>
        new(new PipelineKey(key), Guid.NewGuid());
}

/// <summary>Reads every envelope from a source without a pipeline runtime.</summary>
internal static class SourceReader
{
    internal static async Task<List<T>> ReadAllAsync<T>(IPipelineSource<T> source, CancellationToken ct = default)
    {
        var items = new List<T>();
        await foreach (var envelope in source.ReadEnvelopesAsync(ct))
            items.Add(envelope.Payload);
        return items;
    }
}
