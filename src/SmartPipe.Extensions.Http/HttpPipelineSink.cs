#nullable enable

using System.Net.Http;
using System.Runtime.ExceptionServices;
using SmartPipe.Core;

namespace SmartPipe.Extensions.Http;

internal sealed class HttpPipelineSink<T> : IPipelineSink<T>
{
    private readonly HttpClientSource _clients;
    private readonly HttpRequestFactory<T> _requestFactory;
    private readonly Func<ProcessingEnvelope<T>, string?>? _idempotencyKeySelector;
    private readonly HttpSinkOptionsSnapshot _options;
    private readonly TimeProvider _timeProvider;

    public HttpPipelineSink(
        HttpClientSource clients,
        HttpRequestFactory<T> requestFactory,
        Func<ProcessingEnvelope<T>, string?>? idempotencyKeySelector,
        HttpSinkOptionsSnapshot options,
        TimeProvider timeProvider)
    {
        _clients = clients;
        _requestFactory = requestFactory;
        _idempotencyKeySelector = idempotencyKeySelector;
        _options = options;
        _timeProvider = timeProvider;
    }

    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async ValueTask WriteAsync(ProcessingEnvelope<T> envelope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Payload is null)
            return;

        HttpClient? client = null;
        var ownsClient = false;
        HttpRequestMessage? request = null;
        HttpResponseMessage? response = null;
        HttpBodyCancellationScope? bodyCancellation = null;
        Exception? primaryFailure = null;

        try
        {
            ct.ThrowIfCancellationRequested();
            var idempotencyKey = _idempotencyKeySelector?.Invoke(envelope);
            if (idempotencyKey is not null)
                HttpRequestValidation.ValidateIdempotencyKey(idempotencyKey);

            ct.ThrowIfCancellationRequested();
            client = _clients.Acquire(out ownsClient);

            ct.ThrowIfCancellationRequested();
            request = await _requestFactory(envelope, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The HTTP request factory returned null.");
            HttpRequestValidation.Validate(request, client);
            HttpRequestValidation.AddIdempotencyKey(
                request,
                _options.IdempotencyHeaderName,
                idempotencyKey);

            ct.ThrowIfCancellationRequested();
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            bodyCancellation = new HttpBodyCancellationScope(ct, _options.BodyTimeout, _timeProvider);
            await HttpResponseStatus.ThrowIfUnsuccessfulAsync(
                response,
                _options.OperationName,
                _options.ResponsePolicy,
                bodyCancellation).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            primaryFailure = bodyCancellation?.Translate(exception) ?? exception;
            ExceptionDispatchInfo.Throw(primaryFailure);
            throw;
        }
        finally
        {
            var cleanupFailures = new List<Exception>();
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
