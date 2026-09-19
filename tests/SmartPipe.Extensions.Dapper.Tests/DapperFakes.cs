using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Dapper.Tests;

/// <summary>Records the ordered interaction of the recording fakes.</summary>
internal sealed class DapperTestJournal
{
    private readonly List<string> _entries = [];
    private readonly object _sync = new();

    public void Record(string entry)
    {
        lock (_sync)
            _entries.Add(entry);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public int CountOf(string entry) => Snapshot().Count(candidate => candidate == entry);

    public string Describe() => string.Join(" > ", Snapshot());

    public int IndexOf(string entry) => Snapshot().ToList().IndexOf(entry);
}

internal sealed class RecordingTestFailure(string message) : InvalidOperationException(message);

/// <summary>A mutable mapping target for Dapper runtime row mapping.</summary>
internal sealed class TestPerson
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>A recording <see cref="DbConnection"/> that can inject acquisition and release failures.</summary>
internal sealed class RecordingDbConnection(DapperTestJournal journal) : DbConnection
{
    private readonly List<RecordingDbCommand> _commands = [];
    private ConnectionState _state = ConnectionState.Closed;

    public string ConnectionText { get; set; } = "Data Source=recording";

    public int OpenCount { get; private set; }

    public int CloseCount { get; private set; }

    public int DisposeCount { get; private set; }

    public CancellationToken? LastOpenToken { get; private set; }

    public CancellationToken? LastBeginTransactionToken { get; private set; }

    public Exception? OpenFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public Exception? BeginTransactionFailure { get; set; }

    public int AffectedRows { get; set; } = 1;

    public DbDataReader? Reader { get; set; }

    /// <summary>Gets or sets a fault copied into every command created after it is set.</summary>
    public Exception? NextExecuteNonQueryFailure { get; set; }

    /// <summary>Gets or sets a fault copied into every command created after it is set.</summary>
    public Exception? NextExecuteReaderFailure { get; set; }

    /// <summary>Gets or sets a commit fault copied into every transaction begun after it is set.</summary>
    public Exception? NextCommitFailure { get; set; }

    /// <summary>Gets or sets a rollback fault copied into every transaction begun after it is set.</summary>
    public Exception? NextRollbackFailure { get; set; }

    /// <summary>Gets or sets a release fault copied into every transaction begun after it is set.</summary>
    public Exception? NextTransactionDisposeFailure { get; set; }

    public RecordingDbTransaction? LastTransaction { get; private set; }

    public IReadOnlyList<RecordingDbCommand> Commands
    {
        get
        {
            lock (_commands)
                return _commands.ToArray();
        }
    }

    public RecordingDbCommand SingleCommand => Assert.Single(Commands);

    [AllowNull]
    public override string ConnectionString
    {
        get => ConnectionText;
        set => ConnectionText = value ?? string.Empty;
    }

    public override string Database => "recording";

    public override string DataSource => "recording";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }

    public override void Close()
    {
        CloseCount++;
        _state = ConnectionState.Closed;
    }

    public override void Open()
    {
        OpenCount++;
        journal.Record("connection-open");
        if (OpenFailure is not null)
            throw OpenFailure;
        _state = ConnectionState.Open;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        LastOpenToken = cancellationToken;
        Open();
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        journal.Record("begin-transaction");
        if (BeginTransactionFailure is not null)
            throw BeginTransactionFailure;
        var transaction = new RecordingDbTransaction(this, isolationLevel, journal)
        {
            CommitFailure = NextCommitFailure,
            RollbackFailure = NextRollbackFailure,
            DisposeFailure = NextTransactionDisposeFailure,
        };
        LastTransaction = transaction;
        return transaction;
    }

    protected override ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        LastBeginTransactionToken = cancellationToken;
        return new ValueTask<DbTransaction>(BeginDbTransaction(isolationLevel));
    }

    protected override DbCommand CreateDbCommand()
    {
        journal.Record("create-command");
        var command = new RecordingDbCommand(this, journal)
        {
            AffectedRows = AffectedRows,
            Reader = Reader,
            ExecuteNonQueryFailure = NextExecuteNonQueryFailure,
            ExecuteReaderFailure = NextExecuteReaderFailure,
        };
        lock (_commands)
            _commands.Add(command);
        return command;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        DisposeCount++;
        journal.Record("connection-dispose");
        if (DisposeFailure is not null)
            throw DisposeFailure;
    }
}

/// <summary>A recording <see cref="DbCommand"/> that captures text, tokens and execution counts.</summary>
internal sealed class RecordingDbCommand(DbConnection connection, DapperTestJournal journal) : DbCommand
{
    private readonly RecordingDbParameterCollection _parameters = new(journal);
    private DbConnection? _connection = connection;
    private DbTransaction? _transaction;
    private string _commandText = string.Empty;

    public int ExecuteReaderCount { get; private set; }

    public int ExecuteNonQueryCount { get; private set; }

    public int DisposeCount { get; private set; }

    public CancellationToken? LastExecuteReaderToken { get; private set; }

    public CancellationToken? LastExecuteNonQueryToken { get; private set; }

    public DbTransaction? AssignedTransaction => _transaction;

    public Exception? ExecuteReaderFailure { get; set; }

    public Exception? ExecuteNonQueryFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public int AffectedRows { get; set; } = 1;

    public DbDataReader? Reader { get; set; }

    public IReadOnlyList<string> ParameterNames => _parameters.Names;

    public IReadOnlyList<object?> ParameterValues => _parameters.Values;

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            journal.Record("command-text");
        }
    }

    /// <summary>Gets or sets the command timeout. The default differs from every test option value.</summary>
    public override int CommandTimeout { get; set; } = 17;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value;
            if (value is not null)
                journal.Record("command-transaction");
        }
    }

    public override void Cancel() { }

    public override int ExecuteNonQuery()
    {
        ExecuteNonQueryCount++;
        journal.Record("execute-nonquery");
        if (ExecuteNonQueryFailure is not null)
            throw ExecuteNonQueryFailure;
        return AffectedRows;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        LastExecuteNonQueryToken = cancellationToken;
        return Task.FromResult(ExecuteNonQuery());
    }

    public override object? ExecuteScalar() => throw new NotSupportedException();

    public override void Prepare() { }

    protected override DbParameter CreateDbParameter() => new RecordingDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        ExecuteReaderCount++;
        journal.Record("execute-reader");
        if (ExecuteReaderFailure is not null)
            throw ExecuteReaderFailure;
        return Reader ?? throw new InvalidOperationException("No reader is configured for this recording command.");
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        LastExecuteReaderToken = cancellationToken;
        if (ExecuteReaderFailure is not null)
        {
            ExecuteReaderCount++;
            journal.Record("execute-reader");
            return Task.FromException<DbDataReader>(ExecuteReaderFailure);
        }

        try
        {
            return Task.FromResult(ExecuteDbDataReader(behavior));
        }
        catch (Exception exception)
        {
            return Task.FromException<DbDataReader>(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        DisposeCount++;
        journal.Record("command-dispose");
        if (DisposeFailure is not null)
            throw DisposeFailure;
    }
}

internal sealed class RecordingDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}

internal sealed class RecordingDbParameterCollection(DapperTestJournal journal) : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public IReadOnlyList<string> Names => _parameters.Select(parameter => parameter.ParameterName).ToArray();

    public IReadOnlyList<object?> Values => _parameters.Select(parameter => parameter.Value).ToArray();

    public override int Count => _parameters.Count;

    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        Record(value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var value in values)
            Add(value!);
    }

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains((DbParameter)value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter => parameter.ParameterName == parameterName);

    public override void Insert(int index, object value)
    {
        _parameters.Insert(index, (DbParameter)value);
        Record(value);
    }

    public override void Remove(object value) => _parameters.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _parameters.RemoveAt(index);
    }

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) =>
        _parameters[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index >= 0)
            _parameters[index] = value;
        else
            Add(value);
    }

    private void Record(object value)
    {
        var parameter = (DbParameter)value;
        journal.Record($"parameter:{parameter.ParameterName}={parameter.Value}");
    }
}

/// <summary>A recording <see cref="DbTransaction"/> that captures commit, rollback and release order.</summary>
internal sealed class RecordingDbTransaction(
    DbConnection connection,
    IsolationLevel isolationLevel,
    DapperTestJournal journal) : DbTransaction
{
    public override IsolationLevel IsolationLevel { get; } = isolationLevel;

    protected override DbConnection DbConnection { get; } = connection;

    public int CommitCount { get; private set; }

    public int RollbackCount { get; private set; }

    public int DisposeCount { get; private set; }

    public CancellationToken? LastCommitToken { get; private set; }

    public CancellationToken? LastRollbackToken { get; private set; }

    public Exception? CommitFailure { get; set; }

    public Exception? RollbackFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public override void Commit()
    {
        CommitCount++;
        journal.Record("commit");
        if (CommitFailure is not null)
            throw CommitFailure;
    }

    public override Task CommitAsync(CancellationToken cancellationToken)
    {
        LastCommitToken = cancellationToken;
        try
        {
            Commit();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public override void Rollback()
    {
        RollbackCount++;
        journal.Record("rollback");
        if (RollbackFailure is not null)
            throw RollbackFailure;
    }

    public override Task RollbackAsync(CancellationToken cancellationToken)
    {
        LastRollbackToken = cancellationToken;
        try
        {
            Rollback();
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        DisposeCount++;
        journal.Record("transaction-dispose");
        if (DisposeFailure is not null)
            throw DisposeFailure;
    }
}

/// <summary>A recording <see cref="DbDataReader"/> that captures per-read tokens and release order.</summary>
internal sealed class RecordingDbDataReader : DbDataReader
{
    private readonly List<string> _columns;
    private readonly List<object?[]> _rows;
    private readonly DapperTestJournal _journal;
    private int _index = -1;
    private bool _disposed;

    public RecordingDbDataReader(DapperTestJournal journal, IReadOnlyList<string> columns, params object?[][] rows)
    {
        _journal = journal;
        _columns = [.. columns];
        _rows = [.. rows];
    }

    public int ReadCount { get; private set; }

    public int DisposeCount { get; private set; }

    public List<CancellationToken> ReadTokens { get; } = [];

    /// <summary>Gets or sets a hook that runs before every <see cref="ReadAsync"/> call.</summary>
    public Action<int>? BeforeRead { get; set; }

    /// <summary>Gets or sets a fault injected instead of advancing.</summary>
    public Exception? ReadFailure { get; set; }

    public Exception? DisposeFailure { get; set; }

    public override int Depth => 0;

    public override int FieldCount => _columns.Count;

    public override bool HasRows => _rows.Count > 0;

    public override bool IsClosed => _disposed;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        ReadCount++;
        _journal.Record("reader-read");
        if (ReadFailure is not null)
            throw ReadFailure;
        if (_index + 1 >= _rows.Count)
            return false;
        _index++;
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        ReadTokens.Add(cancellationToken);
        BeforeRead?.Invoke(ReadCount);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    public override string GetName(int ordinal) => _columns[ordinal];

    public override int GetOrdinal(string name) => _columns.IndexOf(name);

    public override Type GetFieldType(int ordinal)
    {
        foreach (var row in _rows)
        {
            if (row[ordinal] is { } value and not DBNull)
                return value.GetType();
        }

        return typeof(object);
    }

    public override object GetValue(int ordinal) => _rows[_index][ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _columns.Count);
        for (var index = 0; index < count; index++)
            values[index] = GetValue(index);
        return count;
    }

    public override T GetFieldValue<T>(int ordinal) => (T)GetValue(ordinal);

    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) =>
        Task.FromResult(GetFieldValue<T>(ordinal));

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override int GetInt32(int ordinal) => (int)GetValue(ordinal);

    public override long GetInt64(int ordinal) => (long)GetValue(ordinal);

    public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);

    public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);

    public override char GetChar(int ordinal) => (char)GetValue(ordinal);

    public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);

    public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);

    public override double GetDouble(int ordinal) => (double)GetValue(ordinal);

    public override float GetFloat(int ordinal) => (float)GetValue(ordinal);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => (short)GetValue(ordinal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override bool NextResult() => false;

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(false);

    public override IEnumerator GetEnumerator() => _rows.GetEnumerator();

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        DisposeCount++;
        _disposed = true;
        _journal.Record("reader-dispose");
        if (DisposeFailure is not null)
            throw DisposeFailure;
    }
}

/// <summary>A <see cref="DbDataSource"/> that hands out recording connections.</summary>
internal sealed class RecordingDbDataSource(Func<RecordingDbConnection> connectionFactory) : DbDataSource
{
    public int CreateConnectionCount { get; private set; }

    public override string ConnectionString => "Data Source=recording";

    protected override DbConnection CreateDbConnection()
    {
        CreateConnectionCount++;
        return connectionFactory();
    }
}

/// <summary>A <see cref="DbDataSource"/> that wraps a real SQLite connection.</summary>
internal sealed class SqliteDbDataSource(string connectionString) : DbDataSource
{
    public int CreateConnectionCount { get; private set; }

    public override string ConnectionString { get; } = connectionString;

    protected override DbConnection CreateDbConnection()
    {
        CreateConnectionCount++;
        return new SqliteConnection(ConnectionString);
    }
}

/// <summary>Activates descriptors and builds the shared per-run test inputs.</summary>
internal static class DapperTestActivation
{
    public static PipelineActivationContext CreateContext(string key = "dapper-test") =>
        new(new PipelineKey(key), Guid.NewGuid());

    public static async Task<TComponent> ActivateAsync<TComponent>(
        PipelineComponent<TComponent> descriptor,
        PipelineActivationContext context,
        CancellationToken cancellationToken = default)
        where TComponent : class
    {
        var activatorProperty = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(activatorProperty);
        var activator = Assert.IsAssignableFrom<Delegate>(activatorProperty!.GetValue(descriptor));
        var valueTask = activator.DynamicInvoke(context, cancellationToken);
        Assert.NotNull(valueTask);
        var asTask = valueTask!.GetType().GetMethod("AsTask", Type.EmptyTypes);
        Assert.NotNull(asTask);
        var task = Assert.IsAssignableFrom<Task>(asTask!.Invoke(valueTask, null));
        await task;
        return Assert.IsAssignableFrom<TComponent>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    public static ProcessingEnvelope<T> Envelope<T>(T payload) =>
        ProcessingEnvelope<T>.Create(
            payload,
            "dapper-test",
            Guid.NewGuid().ToString("N"),
            traceId: 1);

    public static async Task<List<ProcessingEnvelope<T>>> ReadAllAsync<T>(
        IPipelineSource<T> source,
        CancellationToken cancellationToken)
    {
        var envelopes = new List<ProcessingEnvelope<T>>();
        await foreach (var envelope in source.ReadEnvelopesAsync(cancellationToken))
            envelopes.Add(envelope);
        return envelopes;
    }
}

/// <summary>A borrowed logger factory that records formatted messages.</summary>
internal sealed class RecordingLoggerFactory : ILoggerFactory
{
    public List<string> Messages { get; } = [];

    public List<Exception?> Exceptions { get; } = [];

    public int CreateLoggerCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public void AddProvider(ILoggerProvider provider) { }

    public ILogger CreateLogger(string categoryName)
    {
        CreateLoggerCalls++;
        return new RecordingLogger(Messages, Exceptions);
    }

    public void Dispose() => DisposeCalls++;

    private sealed class RecordingLogger(List<string> messages, List<Exception?> exceptions) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            exceptions.Add(exception);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
