using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using SmartPipe.Core;

namespace SmartPipe.Extensions.PostgreSql.Tests.Integration;

/// <summary>Bounded, clearly labelled guards. They exist only to turn a hang into a visible failure.</summary>
internal static class PostgreSqlTestGuard
{
    /// <summary>Guard for a whole scenario, including a 100k-row COPY.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>Guard for one protocol step or one delivery.</summary>
    internal static readonly TimeSpan Short = TimeSpan.FromSeconds(25);

    /// <summary>Bounded observation window used to assert that a future action does not happen.</summary>
    internal static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(750);
}

/// <summary>
/// Activates a <see cref="PipelineComponent{TComponent}"/> exactly the way the runtime does, without taking a
/// dependency on runtime internals: the descriptor's lazy activator is invoked with a real activation context.
/// </summary>
internal static class PostgreSqlComponentActivation
{
    internal static async Task<TComponent> ActivateAsync<TComponent>(
        PipelineComponent<TComponent> descriptor,
        CancellationToken ct = default)
        where TComponent : class
    {
        var activatorProperty = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activatorProperty);

        var activator = Assert.IsAssignableFrom<Delegate>(activatorProperty!.GetValue(descriptor));
        var pending = activator.DynamicInvoke(
            new PipelineActivationContext(new PipelineKey("postgresql-integration"), Guid.NewGuid()),
            ct);
        Assert.NotNull(pending);

        var asTask = pending!.GetType().GetMethod("AsTask", Type.EmptyTypes);
        Assert.NotNull(asTask);
        var task = Assert.IsAssignableFrom<Task>(asTask!.Invoke(pending, null));
        await task.WaitAsync(PostgreSqlTestGuard.Timeout, ct).ConfigureAwait(false);
        return Assert.IsAssignableFrom<TComponent>(task.GetType().GetProperty("Result")!.GetValue(task));
    }
}

/// <summary>Asserts the Core failure-precedence policy: the primary failure stays first, cleanup failures follow it.</summary>
internal static class PostgreSqlFailureAssert
{
    /// <summary>Returns the primary failure, unwrapping a combined primary-plus-cleanup failure.</summary>
    internal static Exception Primary(Exception failure) =>
        failure is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions[0]
            : failure;

    internal static TException PrimaryOf<TException>(Exception failure)
        where TException : Exception
    {
        var primary = Primary(failure);
        Assert.IsAssignableFrom<TException>(primary);
        return (TException)primary;
    }

    internal static IReadOnlyList<Exception> Tree(Exception failure)
    {
        var results = new List<Exception>();
        Visit(failure);
        return results;

        void Visit(Exception exception)
        {
            results.Add(exception);
            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    Visit(inner);
            }
            else if (exception.InnerException is not null)
            {
                Visit(exception.InnerException);
            }
        }
    }

    internal static void ContainsMessage(Exception failure, string expected) =>
        Assert.True(
            Tree(failure).Any(exception => exception.Message.Contains(expected, StringComparison.Ordinal)),
            $"Expected the failure tree to contain \"{expected}\" but it was: {Describe(failure)}");

    internal static void DoesNotContainMessage(Exception failure, string unexpected) =>
        Assert.False(
            Tree(failure).Any(exception => exception.Message.Contains(unexpected, StringComparison.Ordinal)),
            $"Expected the failure tree not to contain \"{unexpected}\" but it was: {Describe(failure)}");

    internal static string Describe(Exception failure) =>
        string.Join(" -> ", Tree(failure).Select(exception => $"{exception.GetType().Name}: {exception.Message}"));
}

/// <summary>
/// Deterministic gate with asynchronous continuations, so a scenario can hold a component inside a protocol step
/// without sleeping.
/// </summary>
internal static class PostgreSqlGate
{
    internal static TaskCompletionSource Create() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static TaskCompletionSource<T> Create<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Captures everything a component logs: the formatted message, the structured state values and the exception.
/// </summary>
/// <remarks>
/// The formatted-message-only view is not enough for the "the payload is never logged" requirement, because a
/// component could log the payload as a structured property whose name never appears in the default format string.
/// </remarks>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly List<string> _entries = [];

    private readonly Lock _sync = new();

    internal IReadOnlyList<string> Entries
    {
        get
        {
            lock (_sync)
                return [.. _entries];
        }
    }

    internal bool Contains(string text) =>
        Entries.Any(entry => entry.Contains(text, StringComparison.Ordinal));

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Record(string entry)
    {
        lock (_sync)
            _entries.Add(entry);
    }

    private sealed class RecordingLogger(RecordingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var builder = new StringBuilder()
                .Append(category)
                .Append('|')
                .Append(logLevel)
                .Append('|')
                .Append(formatter(state, exception));

            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                    builder.Append('|').Append(key).Append('=').Append(value);
            }

            if (state is not null)
                builder.Append("|state=").Append(state);

            if (exception is not null)
                builder.Append("|exception=").Append(exception);

            owner.Record(builder.ToString());
        }
    }
}

/// <summary>A row exchanged with the real server over binary COPY in both directions.</summary>
/// <param name="Id">Integer column.</param>
/// <param name="Name">Text column; null exercises the NULL path.</param>
/// <param name="Amount">Numeric column.</param>
/// <param name="Day">Date column mapped to <see cref="DateOnly"/>.</param>
/// <param name="AtTime">Time column mapped to <see cref="TimeOnly"/>.</param>
/// <param name="Stamp">Timestamp without time zone.</param>
/// <param name="Instant">Timestamp with time zone.</param>
/// <param name="Document">JSONB column read and written as raw text with an explicit provider type.</param>
internal sealed record CopyRow(
    int? Id,
    string? Name,
    decimal? Amount,
    DateOnly? Day,
    TimeOnly? AtTime,
    DateTime? Stamp,
    DateTime? Instant,
    string? Document);

/// <summary>The shared COPY table shape and the row callbacks used by every COPY scenario.</summary>
internal static class PostgreSqlCopyShape
{
    internal const int ColumnCount = 8;

    internal const string TableDefinition =
        "id integer, name text, amount numeric(20,4), day date, at_time time, stamp timestamp, instant timestamptz, document jsonb";

    internal const string ColumnList = "id, name, amount, day, at_time, stamp, instant, document";

    internal static string CopyToCommand(string quotedTable) =>
        $"COPY (SELECT {ColumnList} FROM {quotedTable} ORDER BY id) TO STDOUT (FORMAT BINARY)";

    internal static string CopyFromCommand(string quotedTable) =>
        $"COPY {quotedTable} ({ColumnList}) FROM STDIN (FORMAT BINARY)";

    /// <summary>
    /// Reads exactly one row. The callback asserts that the source reported the expected column count and that the
    /// callback consumed exactly that many columns, so a mismatch can never shift the remaining columns silently.
    /// </summary>
    internal static async ValueTask<CopyRow> ReadRowAsync(
        NpgsqlBinaryExporter exporter,
        int columnCount,
        CancellationToken ct)
    {
        Assert.Equal(ColumnCount, columnCount);

        var consumed = 0;
        var id = await ReadStructAsync<int>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var name = await ReadReferenceAsync<string>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var amount = await ReadStructAsync<decimal>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var day = await ReadStructAsync<DateOnly>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var atTime = await ReadStructAsync<TimeOnly>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var stamp = await ReadStructAsync<DateTime>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var instant = await ReadStructAsync<DateTime>(exporter, ct).ConfigureAwait(false);
        consumed++;
        var document = await ReadJsonAsync(exporter, ct).ConfigureAwait(false);
        consumed++;

        Assert.Equal(columnCount, consumed);
        return new CopyRow(id, name, amount, day, atTime, stamp, instant, document);
    }

    internal static async ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, CopyRow row, CancellationToken ct)
    {
        await WriteStructAsync(importer, row.Id, ct).ConfigureAwait(false);
        await WriteReferenceAsync(importer, row.Name, ct).ConfigureAwait(false);
        await WriteStructAsync(importer, row.Amount, ct).ConfigureAwait(false);
        await WriteStructAsync(importer, row.Day, ct).ConfigureAwait(false);
        await WriteStructAsync(importer, row.AtTime, ct).ConfigureAwait(false);
        await WriteStructAsync(importer, row.Stamp, ct).ConfigureAwait(false);
        await WriteStructAsync(importer, row.Instant, ct).ConfigureAwait(false);

        if (row.Document is null)
            await importer.WriteNullAsync(ct).ConfigureAwait(false);
        else
            await importer.WriteAsync(row.Document, NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
    }

    private static async ValueTask<T?> ReadStructAsync<T>(NpgsqlBinaryExporter exporter, CancellationToken ct)
        where T : struct
    {
        if (exporter.IsNull)
        {
            await exporter.SkipAsync(ct).ConfigureAwait(false);
            return null;
        }

        return await exporter.ReadAsync<T>(ct).ConfigureAwait(false);
    }

    private static async ValueTask<T?> ReadReferenceAsync<T>(NpgsqlBinaryExporter exporter, CancellationToken ct)
        where T : class
    {
        if (exporter.IsNull)
        {
            await exporter.SkipAsync(ct).ConfigureAwait(false);
            return null;
        }

        return await exporter.ReadAsync<T>(ct).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ReadJsonAsync(NpgsqlBinaryExporter exporter, CancellationToken ct)
    {
        if (exporter.IsNull)
        {
            await exporter.SkipAsync(ct).ConfigureAwait(false);
            return null;
        }

        // JSONB is deliberately read as raw text with the provider type stated explicitly.
        return await exporter.ReadAsync<string>(NpgsqlDbType.Jsonb, ct).ConfigureAwait(false);
    }

    private static async ValueTask WriteStructAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        CancellationToken ct)
        where T : struct
    {
        if (value is null)
        {
            await importer.WriteNullAsync(ct).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value.Value, ct).ConfigureAwait(false);
    }

    private static async ValueTask WriteReferenceAsync<T>(
        NpgsqlBinaryImporter importer,
        T? value,
        CancellationToken ct)
        where T : class
    {
        if (value is null)
        {
            await importer.WriteNullAsync(ct).ConfigureAwait(false);
            return;
        }

        await importer.WriteAsync(value, ct).ConfigureAwait(false);
    }
}

/// <summary>Small enumeration helpers shared by the COPY and notification scenarios.</summary>
internal static class PostgreSqlEnumeration
{
    internal static async Task<T> FirstPayloadAsync<T>(IPipelineSource<T> source, CancellationToken ct)
    {
        await foreach (var envelope in source.ReadEnvelopesAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            return envelope.Payload;

        throw new InvalidOperationException("The source completed without producing an envelope.");
    }

    internal static async Task<int> CountAsync<T>(IPipelineSource<T> source, CancellationToken ct)
    {
        var count = 0;
        await foreach (var _ in source.ReadEnvelopesAsync(ct).WithCancellation(ct).ConfigureAwait(false))
            count++;

        return count;
    }
}
