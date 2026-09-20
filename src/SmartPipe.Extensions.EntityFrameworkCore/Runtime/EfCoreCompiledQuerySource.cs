#nullable enable

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SmartPipe.Core;

namespace SmartPipe.Extensions.EntityFrameworkCore.Runtime;

/// <summary>Streams one caller-provided compiled async sequence per run over exactly one owned context.</summary>
/// <remarks>
/// The result type is intentionally unconstrained, so scalar and struct results are supported, and there is
/// no tracking operator because the compiled delegate already fixes the query shape. Everything else —
/// context ownership, enumerator-then-context release, single-enumeration enforcement and primary-first
/// cleanup — is owned by <see cref="EfCoreSourceBase{TContext,TResult}"/>.
/// </remarks>
internal sealed class EfCoreCompiledQuerySource<TContext, TResult> : EfCoreSourceBase<TContext, TResult>
    where TContext : DbContext
{
    private readonly Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> _compiledQuery;

    internal EfCoreCompiledQuerySource(
        Func<PipelineActivationContext, CancellationToken, ValueTask<TContext>> createContext,
        Func<TContext, PipelineActivationContext, CancellationToken, IAsyncEnumerable<TResult>> compiledQuery,
        EfCoreOptionsSnapshot options,
        PipelineActivationContext activation,
        ILoggerFactory? loggerFactory,
        CancellationToken activationCancellationToken)
        : base(createContext, options, activation, loggerFactory, activationCancellationToken, "compiled query")
    {
        _compiledQuery = compiledQuery;
    }

    /// <summary>Invokes the caller's compiled sequence once and acquires its single enumerator.</summary>
    protected override IAsyncEnumerator<TResult> AcquireEnumerator(CancellationToken cancellationToken)
    {
        var sequence = _compiledQuery(Context, Activation, cancellationToken)
            ?? throw new InvalidOperationException("The Entity Framework Core compiled query returned no async sequence.");

        return sequence.GetAsyncEnumerator(cancellationToken);
    }
}
