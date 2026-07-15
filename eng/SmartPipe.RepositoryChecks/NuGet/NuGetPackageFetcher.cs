using System.Buffers;
using System.Net;
using SmartPipe.RepositoryChecks.Infrastructure;

namespace SmartPipe.RepositoryChecks.NuGet;

internal interface INuGetPackageFetcher
{
    Task<string> FetchAsync(
        string packageId,
        string version,
        string destinationDirectory,
        CancellationToken cancellationToken);
}

internal interface INuGetRetryClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class NuGetPackageFetcher : INuGetPackageFetcher
{
    public const long DefaultMaxPackageSizeBytes = 100L * 1024 * 1024;

    private const int MaximumAttempts = 3;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly INuGetServiceIndexClient _serviceIndexClient;
    private readonly INuGetRetryClock _retryClock;
    private readonly long _maxPackageSizeBytes;

    public NuGetPackageFetcher(
        HttpClient httpClient,
        INuGetServiceIndexClient serviceIndexClient,
        INuGetRetryClock? retryClock = null,
        long maxPackageSizeBytes = DefaultMaxPackageSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(serviceIndexClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPackageSizeBytes);

        _httpClient = httpClient;
        _serviceIndexClient = serviceIndexClient;
        _retryClock = retryClock ?? new SystemNuGetRetryClock();
        _maxPackageSizeBytes = maxPackageSizeBytes;
    }

    public async Task<string> FetchAsync(
        string packageId,
        string version,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);
        var packageBaseAddress = await _serviceIndexClient
            .GetPackageBaseAddressAsync(cancellationToken)
            .ConfigureAwait(false);
        var packageUri = BuildPackageUri(packageBaseAddress, packageId, version);
        var normalizedPackageId = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        var finalPath = Path.Combine(
            destinationDirectory,
            $"{normalizedPackageId}.{normalizedVersion}.nupkg");

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw CreateExternalSourceException(packageId, version, "could not be downloaded", exception);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    if (IsRetryable(response.StatusCode) && attempt < MaximumAttempts)
                    {
                        var delay = GetRetryDelay(response, attempt);
                        await _retryClock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw CreateExternalSourceException(
                        packageId,
                        version,
                        $"returned HTTP {(int)response.StatusCode}");
                }

                return await DownloadAtomicallyAsync(
                    response,
                    packageId,
                    version,
                    finalPath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("NuGet package retry loop completed unexpectedly.");
    }

    internal static Uri BuildPackageUri(Uri packageBaseAddress, string packageId, string version)
    {
        ArgumentNullException.ThrowIfNull(packageBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var normalizedPackageId = Uri.EscapeDataString(packageId.ToLowerInvariant());
        var normalizedVersion = Uri.EscapeDataString(version.ToLowerInvariant());
        var relativePath = $"{normalizedPackageId}/{normalizedVersion}/{normalizedPackageId}.{normalizedVersion}.nupkg";
        var normalizedBaseAddress = new Uri(packageBaseAddress.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(normalizedBaseAddress, relativePath);
    }

    private async Task<string> DownloadAtomicallyAsync(
        HttpResponseMessage response,
        string packageId,
        string version,
        string finalPath,
        CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > _maxPackageSizeBytes)
        {
            throw CreateIntegrityException(packageId, version, "declared size exceeds the 100 MiB safety limit");
        }

        var partialPath = $"{finalPath}.{Guid.NewGuid():N}.partial";
        try
        {
            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long totalBytes = 0;
            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > _maxPackageSizeBytes)
                    {
                        throw CreateIntegrityException(packageId, version, "streamed size exceeds the 100 MiB safety limit");
                    }

                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (contentLength.HasValue && totalBytes != contentLength.Value)
            {
                throw CreateIntegrityException(packageId, version, "downloaded size does not match Content-Length");
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Close();
            File.Move(partialPath, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            File.Delete(partialPath);
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requestedDelay = retryAfter?.Delta;
        if (!requestedDelay.HasValue && retryAfter?.Date is { } retryDate)
        {
            requestedDelay = retryDate - _retryClock.UtcNow;
        }

        if (!requestedDelay.HasValue || requestedDelay <= TimeSpan.Zero)
        {
            requestedDelay = TimeSpan.FromSeconds(attempt);
        }

        return requestedDelay > MaximumRetryDelay ? MaximumRetryDelay : requestedDelay.Value;
    }

    private static bool IsRetryable(HttpStatusCode statusCode)
    {
        var numericStatusCode = (int)statusCode;
        return statusCode is HttpStatusCode.RequestTimeout or (HttpStatusCode)429
            || numericStatusCode is >= 500 and <= 599;
    }

    private static RepositoryCheckException CreateExternalSourceException(
        string packageId,
        string version,
        string detail,
        Exception? innerException = null)
    {
        var message = $"NuGet package {packageId} {version} {detail}.";
        return innerException is null
            ? new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, message)
            : new RepositoryCheckException(ExitCodes.ExternalSourceUnavailable, message, innerException);
    }

    private static RepositoryCheckException CreateIntegrityException(
        string packageId,
        string version,
        string detail)
    {
        return new RepositoryCheckException(
            ExitCodes.IntegrityOrSignatureFailure,
            $"NuGet package {packageId} {version} failed integrity validation: {detail}.");
    }

    private sealed class SystemNuGetRetryClock : INuGetRetryClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }
}
