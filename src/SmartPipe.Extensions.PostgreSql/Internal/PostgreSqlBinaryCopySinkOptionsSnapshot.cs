namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>Validated, immutable snapshot of <see cref="PostgreSqlBinaryCopySinkOptions"/>.</summary>
internal sealed record PostgreSqlBinaryCopySinkOptionsSnapshot(string OperationName, int MaxRowsPerBatch)
{
    internal static PostgreSqlBinaryCopySinkOptionsSnapshot Create(PostgreSqlBinaryCopySinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var operationName = PostgreSqlOperationName.Validate(
            options.OperationName,
            $"{nameof(options)}.{nameof(options.OperationName)}");

        if (options.MaxRowsPerBatch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                $"{nameof(options)}.{nameof(options.MaxRowsPerBatch)}",
                options.MaxRowsPerBatch,
                PostgreSqlErrorMessages.MaxRowsPerBatchPositive);
        }

        return new(operationName, options.MaxRowsPerBatch);
    }
}
