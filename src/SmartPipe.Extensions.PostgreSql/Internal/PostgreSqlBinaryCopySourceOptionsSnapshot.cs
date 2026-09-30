namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Validated, immutable snapshot of <see cref="PostgreSqlBinaryCopySourceOptions"/>.</summary>
internal sealed record PostgreSqlBinaryCopySourceOptionsSnapshot(string OperationName, int? ExpectedColumnCount)
{
    internal static PostgreSqlBinaryCopySourceOptionsSnapshot Create(PostgreSqlBinaryCopySourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var operationName = PostgreSqlOperationName.Validate(
            options.OperationName,
            $"{nameof(options)}.{nameof(options.OperationName)}");

        if (options.ExpectedColumnCount is int expectedColumnCount && expectedColumnCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(options)}.{nameof(options.ExpectedColumnCount)}",
                options.ExpectedColumnCount,
                PostgreSqlErrorMessages.ExpectedColumnCountPositive);
        }

        return new(operationName, options.ExpectedColumnCount);
    }
}
