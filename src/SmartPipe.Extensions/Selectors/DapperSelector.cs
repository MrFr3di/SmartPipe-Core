using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Dapper;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Selectors;

/// <summary>
/// High-performance Dapper SQL data source. Streams rows directly from database.
/// Supports parameterized queries and command timeout.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public class DapperSelector<T> : IPipelineSource<T>, IDisposable
{
    private readonly IDbConnection _connection;
    private readonly string _sql;
    private readonly object? _parameters;
    private readonly int _commandTimeout;
    private readonly ILogger<DapperSelector<T>>? _logger;
    private readonly bool _leaveOpen;
    private IDataReader? _reader;
    private int _disposed;

    /// <summary>Create Dapper source for given SQL query.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="commandTimeout">Command timeout in seconds (default: 30).</param>
    /// <summary>
    /// Initializes a new instance of the <see cref="DapperSelector{T}"/> class.
    /// </summary>
    /// <param name="connection">The database connection used to execute the query.</param>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="parameters">The parameters passed to the SQL statement.</param>
    /// <param name="commandTimeout">The command timeout, in seconds.</param>
    /// <param name="logger">Optional logger.</param>
    [SuppressMessage(
        "ApiDesign",
        "RS0027:Public API with optional parameter(s) should have the most parameters amongst its public overloads",
        Justification = "The shipped constructor is preserved for binary compatibility; the DbConnection overload adds explicit ownership without optional parameters."
    )]
    public DapperSelector(
        IDbConnection connection,
        string sql,
        object? parameters = null,
        int commandTimeout = 30,
        ILogger<DapperSelector<T>>? logger = null
    )
        : this(connection, sql, parameters, commandTimeout, logger, leaveOpen: true)
    {
    }

    /// <summary>Create Dapper source for given SQL query.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="leaveOpen">Whether to leave the injected connection open when disposing the source.</param>
    /// <param name="commandTimeout">Command timeout in seconds (default: 30).</param>
    /// <summary>
    /// Initializes a selector that executes a SQL statement against a database connection.
    /// </summary>
    /// <param name="connection">The connection to use for executing the query.</param>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="parameters">The parameters passed to the SQL statement.</param>
    /// <param name="leaveOpen">Whether to leave the connection open when the selector is disposed.</param>
    /// <param name="commandTimeout">The command timeout, in seconds.</param>
    /// <param name="logger">Optional logger.</param>
    public DapperSelector(
        DbConnection connection,
        string sql,
        object? parameters,
        bool leaveOpen,
        int commandTimeout,
        ILogger<DapperSelector<T>>? logger
    )
        : this(connection, sql, parameters, commandTimeout, logger, leaveOpen)
    {
    }

    /// <summary>
    /// Initializes a new instance of the Dapper selector.
    /// </summary>
    /// <param name="connection">The database connection to use.</param>
    /// <param name="sql">The SQL statement to execute.</param>
    /// <param name="parameters">The parameters to pass to the command.</param>
    /// <param name="commandTimeout">The command timeout, in seconds.</param>
    /// <param name="logger">The logger used to report selector activity.</param>
    /// <param name="leaveOpen">Whether to leave the connection open when the selector is disposed.</param>
    private DapperSelector(
        IDbConnection connection,
        string sql,
        object? parameters,
        int commandTimeout,
        ILogger<DapperSelector<T>>? logger,
        bool leaveOpen
    )
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _parameters = parameters;
        _commandTimeout = commandTimeout;
        _logger = logger;
        _leaveOpen = leaveOpen;
    }

    /// <summary>
    /// Opens the underlying connection if needed.
    /// </summary>
    /// <param name="ct">A token that cancels the operation before or during opening.</param>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_connection.State == ConnectionState.Open)
            return;

        if (_connection is DbConnection dbConnection)
        {
            await dbConnection.OpenAsync(ct).ConfigureAwait(false);
            return;
        }

        _connection.Open();
    }

    /// <summary>
    /// Streams envelopes for each row returned by the configured SQL query.
    /// </summary>
    /// <param name="ct">A cancellation token used to stop reading rows.</param>
    /// <returns>The sequence of envelopes produced from the query results.</returns>
    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();

        var command = new CommandDefinition(
            _sql,
            _parameters,
            commandTimeout: _commandTimeout,
            cancellationToken: ct
        );

        if (_connection is DbConnection dbConnection)
        {
            await foreach (var envelope in ReadDbConnectionAsync(dbConnection, command, ct))
                yield return envelope;
        }
        else
        {
            foreach (var envelope in ReadLegacyConnection(command, ct))
                yield return envelope;
        }

        _logger?.LogInformation("Dapper source completed. SQL: {Sql}", _sql);
    }

    /// <summary>
    /// Reads rows from a database connection and maps them to processing envelopes.
    /// </summary>
    /// <param name="connection">The database connection to read from.</param>
    /// <param name="command">The Dapper command to execute.</param>
    /// <param name="ct">The cancellation token to observe while reading rows.</param>
    /// <returns>The mapped processing envelopes for each returned row.</returns>
    private async IAsyncEnumerable<ProcessingEnvelope<T>> ReadDbConnectionAsync(
        DbConnection connection,
        CommandDefinition command,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct
    )
    {
        var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
        Interlocked.Exchange(ref _reader, reader);

        try
        {
            var mapper = RowMapper.Create(reader);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = mapper.Map(reader);
                yield return ProcessingEnvelope<T>.Create(row);
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _reader, null, reader), reader))
                await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads result rows from a legacy database connection.
    /// </summary>
    /// <param name="command">The Dapper command to execute.</param>
    /// <param name="ct">The cancellation token to observe while reading rows.</param>
    /// <returns>An enumerable of envelopes containing the mapped rows.</returns>
    private IEnumerable<ProcessingEnvelope<T>> ReadLegacyConnection(
        CommandDefinition command,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var reader = _connection.ExecuteReader(command);
        Interlocked.Exchange(ref _reader, reader);

        try
        {
            var mapper = RowMapper.Create(reader);

            while (!reader.IsClosed)
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.Read())
                    break;

                yield return ProcessingEnvelope<T>.Create(mapper.Map(reader));
            }
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _reader, null, reader), reader))
                reader.Dispose();
        }
    }

    /// <summary>
    /// Asynchronously disposes the active reader and, optionally, the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var reader = Interlocked.Exchange(ref _reader, null);
        if (reader is DbDataReader dbReader)
            await dbReader.DisposeAsync().ConfigureAwait(false);
        else
            reader?.Dispose();

        if (!_leaveOpen)
        {
            if (_connection is DbConnection dbConnection)
                await dbConnection.DisposeAsync().ConfigureAwait(false);
            else
                _connection.Dispose();
        }
    }

    /// <summary>
    /// Releases the active reader and, when configured, the underlying connection.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref _reader, null)?.Dispose();

        if (!_leaveOpen)
            _connection.Dispose();
    }

    private sealed class RowMapper
    {
        private readonly PropertyBinding[] _bindings;

        /// <summary>
        /// Creates a row mapper from the supplied property bindings.
        /// </summary>
        /// <param name="bindings">The property bindings to apply when mapping a row.</param>
        private RowMapper(PropertyBinding[] bindings)
        {
            _bindings = bindings;
        }

        /// <summary>
        /// Creates a row mapper for the current record schema.
        /// </summary>
        /// <param name="record">The record whose column names are used to match writable properties on <typeparamref name="T"/>.</param>
        /// <returns>A mapper configured to populate <typeparamref name="T"/> from matching columns.</returns>
        public static RowMapper Create(IDataRecord record)
        {
            var writableProperties = typeof(T)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanWrite)
                .ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);

            var bindings = new List<PropertyBinding>();
            for (var ordinal = 0; ordinal < record.FieldCount; ordinal++)
            {
                if (writableProperties.TryGetValue(record.GetName(ordinal), out var property))
                    bindings.Add(new PropertyBinding(ordinal, property));
            }

            return new RowMapper(bindings.ToArray());
        }

        /// <summary>
        /// Maps the current record to a new <typeparamref name="T"/> instance.
        /// </summary>
        /// <param name="record">The data record to map.</param>
        /// <returns>A new <typeparamref name="T"/> populated from the record values.</returns>
        public T Map(IDataRecord record)
        {
            var instance = Activator.CreateInstance<T>();

            foreach (var binding in _bindings)
            {
                if (!record.IsDBNull(binding.Ordinal))
                    binding.Property.SetValue(instance, record.GetValue(binding.Ordinal));
            }

            return instance;
        }
    }

    private readonly record struct PropertyBinding(int Ordinal, PropertyInfo Property);
}
