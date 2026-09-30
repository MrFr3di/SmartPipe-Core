namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Validates and snapshots operation names shared by every PostgreSQL option record.</summary>
internal static class PostgreSqlOperationName
{
    internal const int MaxLength = 64;

    internal static string Validate(string? operationName, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(operationName, parameterName);
        if (string.IsNullOrWhiteSpace(operationName))
            throw new ArgumentException(PostgreSqlErrorMessages.OperationNameBlank, parameterName);
        if (operationName.Length > MaxLength)
            throw new ArgumentException(PostgreSqlErrorMessages.OperationNameTooLong, parameterName);
        return operationName;
    }
}
