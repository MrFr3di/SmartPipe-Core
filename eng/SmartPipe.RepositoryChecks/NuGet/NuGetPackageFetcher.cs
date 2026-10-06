using System.Buffers;
using System.Collections.Concurrent;
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

internal interface INuGetPartialPathProvider
{
    string GetCandidatePath(string finalPath);
}

internal interface INuGetPartialFileCreator
{
    FileStream CreateNew(string path);
}

internal sealed class PartialFileCollisionException : IOException
{
    public PartialFileCollisionException(string path, IOException innerException)
        : base($"Partial package path already exists: {Path.GetFileName(path)}", innerException)
    {
    }
}

internal sealed class NuGetPackageFetcher : INuGetPackageFetcher
{
    public const long DefaultMaxPackageSizeBytes = 100L * 1024 * 1024;

    private const int MaximumAttempts = 3;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PublicationGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly INuGetServiceIndexClient _serviceIndexClient;
    private readonly INuGetRetryClock _retryClock;
    private readonly long _maxPackageSizeBytes;
    private readonly INuGetPartialPathProvider _partialPathProvider;
    private readonly INuGetPartialFileCreator _partialFileCreator;

    public NuGetPackageFetcher(
        HttpClient httpClient,
        INuGetServiceIndexClient serviceIndexClient,
        INuGetRetryClock? retryClock = null,
        long maxPackageSizeBytes = DefaultMaxPackageSizeBytes,
        INuGetPartialPathProvider? partialPathProvider = null,
        INuGetPartialFileCreator? partialFileCreator = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(serviceIndexClient);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPackageSizeBytes);

        _httpClient = httpClient;
        _serviceIndexClient = serviceIndexClient;
        _retryClock = retryClock ?? new SystemNuGetRetryClock();
        _maxPackageSizeBytes = maxPackageSizeBytes;
        _partialPathProvider = partialPathProvider ?? new UniquePartialPathProvider();
        _partialFileCreator = partialFileCreator ?? new SystemPartialFileCreator();
    }

    public async Task<string> FetchAsync(
        string packageId,
        string version,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(packageId, version);
        var destinationFullPath = GetCanonicalDestinationPath(destinationDirectory);
        var normalizedPackageId = packageId.ToLowerInvariant();
        var normalizedVersion = version.ToLowerInvariant();
        var finalPath = Path.GetFullPath(Path.Combine(
            destinationFullPath,
            $"{normalizedPackageId}.{normalizedVersion}.nupkg"));
        EnsureContained(destinationFullPath, finalPath);
        if (File.Exists(finalPath))
        {
            return finalPath;
        }

        Directory.CreateDirectory(destinationFullPath);
        var packageBaseAddress = await _serviceIndexClient
            .GetPackageBaseAddressAsync(cancellationToken)
            .ConfigureAwait(false);
        var packageUri = BuildPackageUri(packageBaseAddress, packageId, version);

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
                        response.Dispose();
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
        ValidateIdentity(packageId, version);

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

        string? ownedPartialPath = null;
        try
        {
            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var ownedPartial = OpenOwnedPartial(finalPath);
            ownedPartialPath = ownedPartial.Path;
            await using var destination = ownedPartial.Stream;

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
            var publicationGate = PublicationGates.GetOrAdd(finalPath, static _ => new SemaphoreSlim(1, 1));
            await publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(ownedPartialPath, finalPath, overwrite: true);
            }
            finally
            {
                publicationGate.Release();
            }

            return finalPath;
        }
        finally
        {
            if (ownedPartialPath is not null)
            {
                File.Delete(ownedPartialPath);
            }
        }
    }

    private (string Path, FileStream Stream) OpenOwnedPartial(string finalPath)
    {
        const int maximumCandidateAttempts = 16;
        var destinationDirectory = Path.GetDirectoryName(finalPath)!;
        PartialFileCollisionException? lastCollision = null;
        for (var attempt = 0; attempt < maximumCandidateAttempts; attempt++)
        {
            var candidate = Path.GetFullPath(_partialPathProvider.GetCandidatePath(finalPath));
            EnsureContained(destinationDirectory, candidate);
            if (string.Equals(candidate, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateConfigurationException("Partial package path must differ from the final path.");
            }

            try
            {
                var stream = _partialFileCreator.CreateNew(candidate);
                return (candidate, stream);
            }
            catch (PartialFileCollisionException exception)
            {
                lastCollision = exception;
            }
        }

        throw new RepositoryCheckException(
            ExitCodes.UsageOrConfigurationError,
            "Unable to allocate a unique partial package path after 16 collisions.",
            lastCollision!);
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

    private static void ValidateIdentity(string packageId, string version)
    {
        if (!IsSafePackageId(packageId))
        {
            throw CreateConfigurationException("NuGet package ID has invalid or unsafe syntax.");
        }

        if (!IsSafeVersion(version))
        {
            throw CreateConfigurationException("NuGet package version has invalid or unsafe syntax.");
        }
    }

    private static bool IsSafePackageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 100
            || Path.IsPathRooted(value)
            || value is "." or ".."
            || value.Contains("..", StringComparison.Ordinal)
            || value[0] is '.' or '-' or '+'
            || value[^1] is '.' or '-' or '+')
        {
            return false;
        }

        foreach (var character in value)
        {
            var isAsciiLetterOrDigit = character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9';
            if (!isAsciiLetterOrDigit
                && character is not '.' and not '-' and not '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafeVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 100
            || Path.IsPathRooted(value)
            || value.Any(char.IsControl)
            || value.Contains('/')
            || value.Contains('\\'))
        {
            return false;
        }

        var buildSeparator = value.IndexOf('+');
        if (buildSeparator != value.LastIndexOf('+'))
        {
            return false;
        }

        var versionAndPrerelease = buildSeparator < 0 ? value : value[..buildSeparator];
        var buildMetadata = buildSeparator < 0 ? null : value[(buildSeparator + 1)..];
        var prereleaseSeparator = versionAndPrerelease.IndexOf('-');
        var numericVersion = prereleaseSeparator < 0
            ? versionAndPrerelease
            : versionAndPrerelease[..prereleaseSeparator];
        var prerelease = prereleaseSeparator < 0
            ? null
            : versionAndPrerelease[(prereleaseSeparator + 1)..];
        var numericParts = numericVersion.Split('.');
        return numericParts.Length is >= 1 and <= 4
            && numericParts.All(static part => part.Length > 0 && part.All(char.IsAsciiDigit))
            && IsValidVersionLabel(prerelease)
            && IsValidVersionLabel(buildMetadata);
    }

    private static bool IsValidVersionLabel(string? label)
    {
        if (label is null)
        {
            return true;
        }

        return label.Length > 0
            && label.Split('.').All(static part =>
                part.Length > 0
                && part.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static string GetCanonicalDestinationPath(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw CreateConfigurationException("NuGet package destination directory is required.");
        }

        try
        {
            return Path.GetFullPath(destinationDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new RepositoryCheckException(
                ExitCodes.UsageOrConfigurationError,
                "NuGet package destination directory is invalid.",
                exception);
        }
    }

    private static void EnsureContained(string destinationDirectory, string candidatePath)
    {
        var relativePath = Path.GetRelativePath(destinationDirectory, candidatePath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw CreateConfigurationException("NuGet package path escapes its destination directory.");
        }
    }

    private static RepositoryCheckException CreateConfigurationException(string message)
    {
        return new RepositoryCheckException(ExitCodes.UsageOrConfigurationError, message);
    }

    private sealed class SystemNuGetRetryClock : INuGetRetryClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            return Task.Delay(delay, cancellationToken);
        }
    }

    private sealed class UniquePartialPathProvider : INuGetPartialPathProvider
    {
        public string GetCandidatePath(string finalPath)
        {
            return $"{finalPath}.{Guid.NewGuid():N}.partial";
        }
    }

    private sealed class SystemPartialFileCreator : INuGetPartialFileCreator
    {
        private const int ErrorFileExists = unchecked((int)0x80070050);
        private const int ErrorAlreadyExists = unchecked((int)0x800700B7);
        private const int UnixExist = 17;
        private const int UnixExistAsWin32HResult = unchecked((int)0x80070011);

        public FileStream CreateNew(string path)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException exception) when (IsCollision(exception))
            {
                throw new PartialFileCollisionException(path, exception);
            }
        }

        private static bool IsCollision(IOException exception)
        {
            return exception.HResult is ErrorFileExists
                or ErrorAlreadyExists
                or UnixExist
                or UnixExistAsWin32HResult;
        }
    }
}
