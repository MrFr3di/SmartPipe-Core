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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

    /// <summary>Dispose reader and optionally the connection (synchronous).</summary>
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

        private RowMapper(PropertyBinding[] bindings)
        {
            _bindings = bindings;
        }

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
