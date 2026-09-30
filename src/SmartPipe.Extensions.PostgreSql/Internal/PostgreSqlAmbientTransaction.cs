using System.Transactions;

namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Enforces the provider contract that SmartPipe components never participate in ambient transactions.
/// Npgsql auto-enlists opened connections when <see cref="Transaction.Current"/> is not null, which would make
/// "one batch envelope equals one completed COPY" untrue.
/// </summary>
internal static class PostgreSqlAmbientTransaction
{
    /// <summary>Throws when an ambient transaction is active for the current thread or async flow.</summary>
    internal static void Reject()
    {
        if (Transaction.Current is not null)
            throw new InvalidOperationException(PostgreSqlErrorMessages.AmbientTransactionRejected);
    }
}
