namespace SmartPipe.Extensions.PostgreSql.Internal;

/// <summary>
/// Combines the run-scoped activation token with the per-call token so that activation cancellation and caller
/// cancellation both interrupt provider work, without allocating a linked source when one token is enough.
/// </summary>
internal static class PostgreSqlLinkedCancellation
{
    internal static CancellationTokenSource? Create(CancellationToken activation, CancellationToken requested, out CancellationToken effective)
    {
        if (!activation.CanBeCanceled)
        {
            effective = requested;
            return null;
        }

        if (!requested.CanBeCanceled || requested == activation)
        {
            effective = activation;
            return null;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(activation, requested);
        effective = linked.Token;
        return linked;
    }
}
