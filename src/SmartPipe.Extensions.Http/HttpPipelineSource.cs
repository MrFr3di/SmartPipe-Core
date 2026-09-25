#nullable enable

using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http;

internal sealed class HttpPipelineSource<T> : IPipelineSource<T>
{
    private readonly HttpClientSource _clients;
    private readonly Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> _requestFactory;
    private readonly HttpResponseReader<T> _responseReader;
    private readonly HttpSourceOptionsSnapshot _options;
    private readonly PipelineActivationContext _activationContext;

    public HttpPipelineSource(
        HttpClientSource clients,
        Func<PipelineActivationContext, CancellationToken, ValueTask<HttpRequestMessage>> requestFactory,
        HttpResponseReader<T> responseReader,
        HttpSourceOptionsSnapshot options,
        PipelineActivationContext activationContext)
    {
        _clients = clients;
        _requestFactory = requestFactory;
        _responseReader = responseReader;
        _options = options;
        _activationContext = activationContext;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ProcessingEnvelope<T>> ReadEnvelopesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        HttpClient? client = null;
        var ownsClient = false;
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        HttpBodyCancellationScope? bodyCancellation = null;
        IAsyncEnumerator<T>? responseEnumerator = null;
        Exception? primaryFailure = null;
        ulong traceId = 0;

        try
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                client = _clients.Acquire(out ownsClient);

                ct.ThrowIfCancellationRequested();
                request = await _requestFactory(_activationContext, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The HTTP request factory returned null.");
                HttpRequestValidation.Validate(request, client);

                ct.ThrowIfCancellationRequested();
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    ct).ConfigureAwait(false);

                bodyCancellation = new HttpBodyCancellationScope(ct, _options.BodyTimeout, _activationContext.TimeProvider);
                await HttpResponseStatus.ThrowIfUnsuccessfulAsync(
                    response,
                    _options.OperationName,
                    _options.ResponsePolicy,
                    bodyCancellation).ConfigureAwait(false);

                bodyCancellation.Token.ThrowIfCancellationRequested();
                var responseValues = _responseReader(response, bodyCancellation.Token)
                    ?? throw new InvalidOperationException("The HTTP response reader returned null.");
                responseEnumerator = responseValues.GetAsyncEnumerator(bodyCancellation.Token)
                    ?? throw new InvalidOperationException("The HTTP response reader returned a null enumerator.");
            }
            catch (Exception exception)
            {
                primaryFailure = bodyCancellation?.Translate(exception) ?? exception;
                ExceptionDispatchInfo.Throw(primaryFailure);
                throw;
            }

            while (true)
            {
                ProcessingEnvelope<T> envelope;
                try
                {
                    bodyCancellation.Token.ThrowIfCancellationRequested();
                    if (!await responseEnumerator.MoveNextAsync().ConfigureAwait(false))
                        break;

                    envelope = ProcessingEnvelope<T>.Create(
                        responseEnumerator.Current,
                        _activationContext.PipelineKey.Value,
                        _activationContext.RunId.ToString("N"),
                        unchecked(++traceId));
                }
                catch (Exception exception)
                {
                    primaryFailure = bodyCancellation?.Translate(exception) ?? exception;
                    ExceptionDispatchInfo.Throw(primaryFailure);
                    throw;
                }

                yield return envelope;
            }
        }
        finally
        {
            // Reverse ownership order: the reader's borrow ends before the response, which owns the body stream.
            var cleanupFailures = new List<Exception>();
            if (responseEnumerator is not null)
            {
                try
                {
                    await responseEnumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    if (primaryFailure is not null || bodyCancellation?.IsLateCancellation(exception) != true)
                        cleanupFailures.Add(exception);
                }
            }

            HttpCleanup.AddDisposeFailure(bodyCancellation, cleanupFailures);
            HttpCleanup.AddDisposeFailure(response, cleanupFailures);
            HttpCleanup.AddDisposeFailure(request, cleanupFailures);
            if (ownsClient)
                HttpCleanup.AddDisposeFailure(client, cleanupFailures);
            HttpCleanup.ThrowIfNeeded(primaryFailure, cleanupFailures);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
