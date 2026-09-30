using Npgsql;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Validates the borrowed provider infrastructure and configuration SQL shared by every factory.</summary>
internal static class PostgreSqlArguments
{
    internal static NpgsqlDataSource DataSource(NpgsqlDataSource? dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource, nameof(dataSource));
        return dataSource;
    }

    internal static NpgsqlDataSource NonMultiplexingDataSource(NpgsqlDataSource? dataSource, string message)
    {
        var validated = DataSource(dataSource);
        if (new NpgsqlConnectionStringBuilder(validated.ConnectionString).Multiplexing)
            throw new ArgumentException(message, nameof(dataSource));
        return validated;
    }

    internal static string CopyCommand(string? copyCommand, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(copyCommand, parameterName);
        if (string.IsNullOrWhiteSpace(copyCommand))
            throw new ArgumentException(PostgreSqlErrorMessages.CopyCommandBlank, parameterName);
        return copyCommand;
    }
}
