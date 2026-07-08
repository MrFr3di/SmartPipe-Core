using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    private readonly Func<DbDataReader, T>? _dbDataReaderMapper;
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
    [RequiresUnreferencedCode("The default DapperSelector mapper uses reflection over T. Use the DbDataReader mapper overload for trimming and NativeAOT.")]
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
    [RequiresUnreferencedCode("The default DapperSelector mapper uses reflection over T. Use the DbDataReader mapper overload for trimming and NativeAOT.")]
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

    /// <summary>Create Dapper source with an explicit row mapper.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="mapper">Mapper from the active data reader row to the output value.</param>
    public DapperSelector(
        DbConnection connection,
        string sql,
        Func<DbDataReader, T> mapper)
        : this(connection, sql, mapper, parameters: null, leaveOpen: true, commandTimeout: 30, logger: null)
    {
    }

    /// <summary>Create Dapper source with an explicit row mapper and connection ownership.</summary>
    /// <param name="connection">Database connection.</param>
    /// <param name="sql">SQL query to execute.</param>
    /// <param name="mapper">Mapper from the active data reader row to the output value.</param>
    /// <param name="parameters">Optional query parameters.</param>
    /// <param name="leaveOpen">Whether to leave the injected connection open when disposing the source.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="logger">Optional logger.</param>
    public DapperSelector(
        DbConnection connection,
        string sql,
        Func<DbDataReader, T> mapper,
        object? parameters,
        bool leaveOpen,
        int commandTimeout,
        ILogger<DapperSelector<T>>? logger)
        : this(connection, sql, parameters, commandTimeout, logger, leaveOpen, mapper)
    {
    }

    private DapperSelector(
        IDbConnection connection,
        string sql,
        object? parameters,
        int commandTimeout,
        ILogger<DapperSelector<T>>? logger,
        bool leaveOpen,
        Func<DbDataReader, T>? dbDataReaderMapper = null
    )
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
        _parameters = parameters;
        _commandTimeout = commandTimeout;
        _logger = logger;
        _leaveOpen = leaveOpen;
        _dbDataReaderMapper = dbDataReaderMapper;
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
            var mapper = _dbDataReaderMapper;
            var reflectionMapper = mapper is null ? RowMapper.Create(reader) : null;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = mapper is null
                    ? reflectionMapper!.Map(reader)
                    : mapper(reader);
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
                var name = record.GetName(ordinal);
                if (writableProperties.TryGetValue(name, out var property))
                    bindings.Add(new PropertyBinding(ordinal, name, property));
            }

            return new RowMapper(bindings.ToArray());
        }

        public T Map(IDataRecord record)
        {
            var instance = Activator.CreateInstance<T>();

            foreach (var binding in _bindings)
            {
                if (!record.IsDBNull(binding.Ordinal))
                {
                    var value = ConvertValue(
                        record.GetValue(binding.Ordinal),
                        binding.Property.PropertyType,
                        binding.ColumnName,
                        binding.Property.Name);
                    binding.Property.SetValue(instance, value);
                }
            }

            return instance;
        }

        private static object? ConvertValue(
            object value,
            Type propertyType,
            string columnName,
            string propertyName)
        {
            var targetType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            var valueType = value.GetType();
            if (targetType.IsAssignableFrom(valueType))
                return value;

            try
            {
                if (targetType.IsEnum)
                {
                    return value is string name
                        ? Enum.Parse(targetType, name, ignoreCase: true)
                        : Enum.ToObject(
                            targetType,
                            Convert.ChangeType(
                                value,
                                Enum.GetUnderlyingType(targetType),
                                CultureInfo.InvariantCulture));
                }

                if (targetType == typeof(Guid) && value is string guid)
                    return Guid.Parse(guid);

                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}' value of type '{valueType.FullName}' cannot be mapped to property '{propertyName}' of type '{propertyType.FullName}'.",
                    ex);
            }
        }
    }

    private readonly record struct PropertyBinding(int Ordinal, string ColumnName, PropertyInfo Property);
}
