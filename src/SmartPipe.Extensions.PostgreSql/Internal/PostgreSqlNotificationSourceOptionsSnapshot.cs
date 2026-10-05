namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Validated, immutable snapshot of <see cref="PostgreSqlNotificationSourceOptions"/>.</summary>
internal sealed record PostgreSqlNotificationSourceOptionsSnapshot(string OperationName, int BufferCapacity)
{
    internal static PostgreSqlNotificationSourceOptionsSnapshot Create(PostgreSqlNotificationSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var operationName = PostgreSqlOperationName.Validate(
            options.OperationName,
            nameof(options));

        if (options.BufferCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BufferCapacity,
                PostgreSqlErrorMessages.BufferCapacityPositive);
        }

        return new(operationName, options.BufferCapacity);
    }
}
