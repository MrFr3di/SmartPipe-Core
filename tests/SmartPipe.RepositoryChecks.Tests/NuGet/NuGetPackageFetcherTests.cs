using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SmartPipe.RepositoryChecks.Infrastructure;
using SmartPipe.RepositoryChecks.NuGet;

namespace SmartPipe.RepositoryChecks.Tests.NuGet;

public sealed class NuGetPackageFetcherTests
{
    private static readonly Uri PackageBaseAddress = new("https://packages.example.test/v3-flatcontainer/");

    [Theory]
    [InlineData(
        "SmartPipe.Extensions.Json",
        "2.1.2",
        "smartpipe.extensions.json/2.1.2/smartpipe.extensions.json.2.1.2.nupkg")]
    public void BuildPackageUri_UsesLowercaseFlatContainerPath(
        string packageId,
        string version,
        string expectedRelativePath)
    {
        var result = NuGetPackageFetcher.BuildPackageUri(PackageBaseAddress, packageId, version);

        Assert.Equal(new Uri(PackageBaseAddress, expectedRelativePath), result);
    }

    [Theory]
    [InlineData("../escape", "1.0.0")]
    [InlineData("package/name", "1.0.0")]
    [InlineData("package\\name", "1.0.0")]
    [InlineData("C:\\rooted", "1.0.0")]
    [InlineData("package\nname", "1.0.0")]
    [InlineData("package", "../1.0.0")]
    [InlineData("package", "1/0/0")]
    [InlineData("package", "1\\0\\0")]
    [InlineData("package", "C:\\1.0.0")]
    [InlineData("package", "1.0.0\r")]
    [InlineData("package", "not-a-version")]
    [InlineData("package", "1.0_beta")]
    public async Task FetchAsync_RejectsUnsafeIdentityBeforeFileOrNetworkIo(string packageId, string version)
    {
        var serviceIndex = new StubServiceIndexClient();
        using var httpClient = CreateHttpClient((_, _) =>
            throw new InvalidOperationException("HTTP must not be reached."));
        var fetcher = new NuGetPackageFetcher(httpClient, serviceIndex, new RecordingRetryClock());
        using var directory = new TemporaryDirectory();
        var missingDestination = System.IO.Path.Combine(directory.Path, "must-not-be-created");

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync(packageId, version, missingDestination, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.UsageOrConfigurationError, exception.ExitCode);
        Assert.Equal(0, serviceIndex.Calls);
        Assert.False(Directory.Exists(missingDestination));
    }

    [Fact]
    public async Task FetchAsync_ThrowsExternalSourceErrorWithIdentity_AndDoesNotRetry404()
    {
        var attempts = 0;
        using var httpClient = CreateHttpClient((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var retryClock = new RecordingRetryClock();
        var fetcher = CreateFetcher(httpClient, retryClock);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync(
                "SmartPipe.Extensions.Json",
                "2.1.2",
                directory.Path,
                TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
        Assert.Contains("SmartPipe.Extensions.Json", exception.Message, StringComparison.Ordinal);
        Assert.Contains("2.1.2", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, attempts);
        Assert.Empty(retryClock.Delays);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_RejectsContentLengthOverLimit_AndRemovesPartial()
    {
        using var httpClient = CreateHttpClient((_, _) =>
        {
            var response = CreatePackageResponse("small body");
            response.Content.Headers.ContentLength = 11;
            return Task.FromResult(response);
        });
        var fetcher = CreateFetcher(httpClient, maxPackageSizeBytes: 10);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_RejectsStreamOverLimit_WhenContentLengthIsAbsent()
    {
        using var httpClient = CreateHttpClient((_, _) =>
            Task.FromResult(CreatePackageResponse("eleven bytes", includeContentLength: false)));
        var fetcher = CreateFetcher(httpClient, maxPackageSizeBytes: 10);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_RejectsDishonestContentLength_WhenStreamExceedsLimit()
    {
        using var httpClient = CreateHttpClient((_, _) =>
        {
            var response = CreatePackageResponse("eleven bytes", includeContentLength: false);
            response.Content.Headers.ContentLength = 5;
            return Task.FromResult(response);
        });
        var fetcher = CreateFetcher(httpClient, maxPackageSizeBytes: 10);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_RejectsTruncatedContent_WhenContentLengthIsDishonest()
    {
        using var httpClient = CreateHttpClient((_, _) =>
        {
            var response = CreatePackageResponse("short", includeContentLength: false);
            response.Content.Headers.ContentLength = 10;
            return Task.FromResult(response);
        });
        var fetcher = CreateFetcher(httpClient, maxPackageSizeBytes: 20);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.IntegrityOrSignatureFailure, exception.ExitCode);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_RemovesPartial_WhenCancelledDuringStreaming()
    {
        var stream = new CancellationBlockingStream();
        using var httpClient = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            }));
        var fetcher = CreateFetcher(httpClient);
        using var directory = new TemporaryDirectory();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var fetchTask = fetcher.FetchAsync("Package", "1.0.0", directory.Path, cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetchTask);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_Retries503_AndSucceedsOnSecondAttempt()
    {
        var attempts = 0;
        using var httpClient = CreateHttpClient((_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                unavailable.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
                return Task.FromResult(unavailable);
            }

            return Task.FromResult(CreatePackageResponse("package bytes"));
        });
        var retryClock = new RecordingRetryClock();
        var fetcher = CreateFetcher(httpClient, retryClock);
        using var directory = new TemporaryDirectory();

        var result = await fetcher.FetchAsync(
            "Package",
            "1.0.0",
            directory.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromSeconds(30)], retryClock.Delays);
        Assert.Equal("package bytes", await File.ReadAllTextAsync(result, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
    }

    [Fact]
    public async Task FetchAsync_DisposesRetryResponseBeforeDelay()
    {
        var attempts = 0;
        var retryContent = new DisposalTrackingContent();
        using var httpClient = CreateHttpClient((_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = retryContent }
                : CreatePackageResponse("package bytes"));
        });
        var retryClock = new RecordingRetryClock
        {
            OnDelay = () => Assert.True(retryContent.IsDisposed),
        };
        var fetcher = CreateFetcher(httpClient, retryClock);
        using var directory = new TemporaryDirectory();

        await fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken);

        Assert.True(retryContent.IsDisposed);
    }

    [Fact]
    public async Task FetchAsync_ExhaustsThreeAttempts_AndLeavesNoFile()
    {
        var attempts = 0;
        using var httpClient = CreateHttpClient((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
        });
        var retryClock = new RecordingRetryClock();
        var fetcher = CreateFetcher(httpClient, retryClock);
        using var directory = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.ExternalSourceUnavailable, exception.ExitCode);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retryClock.Delays.Count);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_ReturnsExistingFinalFileWithoutDownloadOrVerificationClaim()
    {
        using var directory = new TemporaryDirectory();
        var finalPath = System.IO.Path.Combine(directory.Path, "package.1.0.0.nupkg");
        await File.WriteAllTextAsync(finalPath, "unverified", TestContext.Current.CancellationToken);
        var serviceIndex = new StubServiceIndexClient();
        using var httpClient = CreateHttpClient((_, _) =>
            throw new InvalidOperationException("HTTP must not be reached."));
        var fetcher = new NuGetPackageFetcher(httpClient, serviceIndex, new RecordingRetryClock());

        var result = await fetcher.FetchAsync(
            "Package",
            "1.0.0",
            directory.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(finalPath, result);
        Assert.Equal("unverified", await File.ReadAllTextAsync(result, TestContext.Current.CancellationToken));
        Assert.Equal(0, serviceIndex.Calls);
    }

    [Fact]
    public async Task FetchAsync_RetriesPartialCollision_AndDeletesOnlyOwnedCandidate()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(CreatePackageResponse("fresh")));
        using var directory = new TemporaryDirectory();
        var finalPath = System.IO.Path.Combine(directory.Path, "package.1.0.0.nupkg");
        var collidingPath = $"{finalPath}.collision.partial";
        var ownedPath = $"{finalPath}.owned.partial";
        await File.WriteAllTextAsync(collidingPath, "other fetch owns this", TestContext.Current.CancellationToken);
        var candidates = new SequencePartialPathProvider(collidingPath, ownedPath);
        var fetcher = new NuGetPackageFetcher(
            httpClient,
            new StubServiceIndexClient(),
            new RecordingRetryClock(),
            NuGetPackageFetcher.DefaultMaxPackageSizeBytes,
            candidates);

        var result = await fetcher.FetchAsync(
            "Package",
            "1.0.0",
            directory.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(result, TestContext.Current.CancellationToken));
        Assert.Equal("other fetch owns this", await File.ReadAllTextAsync(collidingPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ownedPath));
        Assert.Equal(2, candidates.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_RetriesCollisionEvenWhenCandidateDisappearsBeforeClassification()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(CreatePackageResponse("fresh")));
        using var directory = new TemporaryDirectory();
        var finalPath = System.IO.Path.Combine(directory.Path, "package.1.0.0.nupkg");
        var vanishedCollision = $"{finalPath}.vanished.partial";
        var ownedPath = $"{finalPath}.owned.partial";
        var candidates = new SequencePartialPathProvider(vanishedCollision, ownedPath);
        var creator = new DelegatePartialFileCreator((path, attempt) =>
        {
            if (attempt == 1)
            {
                File.WriteAllText(path, "collision");
                File.Delete(path);
                throw new PartialFileCollisionException(path, new IOException("Synthetic collision."));
            }

            return CreateRealPartial(path);
        });
        var fetcher = CreateFetcher(httpClient, candidates, creator);

        var result = await fetcher.FetchAsync(
            "Package",
            "1.0.0",
            directory.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal("fresh", await File.ReadAllTextAsync(result, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(vanishedCollision));
        Assert.Equal(2, creator.Attempts);
    }

    [Fact]
    public async Task FetchAsync_StopsAfterSixteenPartialCollisions()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(CreatePackageResponse("fresh")));
        using var directory = new TemporaryDirectory();
        var creator = new DelegatePartialFileCreator((path, _) =>
            throw new PartialFileCollisionException(path, new IOException("Synthetic collision.")));
        var fetcher = CreateFetcher(httpClient, new GuidPartialPathProvider(), creator);

        var exception = await Assert.ThrowsAsync<RepositoryCheckException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCodes.UsageOrConfigurationError, exception.ExitCode);
        Assert.Equal(16, creator.Attempts);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task FetchAsync_DoesNotRetryArbitraryPartialCreationIoFailure()
    {
        using var httpClient = CreateHttpClient((_, _) => Task.FromResult(CreatePackageResponse("fresh")));
        using var directory = new TemporaryDirectory();
        var expected = new IOException("Disk is unavailable.");
        var creator = new DelegatePartialFileCreator((_, _) => throw expected);
        var fetcher = CreateFetcher(httpClient, new GuidPartialPathProvider(), creator);

        var actual = await Assert.ThrowsAsync<IOException>(
            () => fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(1, creator.Attempts);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentFetchesUseDistinctPartialFiles()
    {
        var gate = new ConcurrentResponseGate(expectedReaders: 2);
        using var httpClient = CreateHttpClient((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(gate.CreateStream()),
            }));
        var fetcher = CreateFetcher(httpClient);
        using var directory = new TemporaryDirectory();

        var first = fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken);
        var second = fetcher.FetchAsync("Package", "1.0.0", directory.Path, TestContext.Current.CancellationToken);
        await gate.AllReadersStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var partials = Directory.EnumerateFiles(directory.Path, "*.partial").ToArray();
        gate.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, partials.Length);
        Assert.Equal(2, partials.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(Directory.EnumerateFiles(directory.Path, "*.partial"));
        Assert.Equal("package", await File.ReadAllTextAsync(results[0], TestContext.Current.CancellationToken));
    }

    private static NuGetPackageFetcher CreateFetcher(
        HttpClient httpClient,
        INuGetRetryClock? retryClock = null,
        long maxPackageSizeBytes = NuGetPackageFetcher.DefaultMaxPackageSizeBytes)
    {
        return new NuGetPackageFetcher(
            httpClient,
            new StubServiceIndexClient(),
            retryClock ?? new RecordingRetryClock(),
            maxPackageSizeBytes);
    }

    private static NuGetPackageFetcher CreateFetcher(
        HttpClient httpClient,
        INuGetPartialPathProvider partialPathProvider,
        INuGetPartialFileCreator partialFileCreator)
    {
        return new NuGetPackageFetcher(
            httpClient,
            new StubServiceIndexClient(),
            new RecordingRetryClock(),
            NuGetPackageFetcher.DefaultMaxPackageSizeBytes,
            partialPathProvider,
            partialFileCreator);
    }

    private static HttpClient CreateHttpClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        return new HttpClient(new StubHttpMessageHandler(responseFactory));
    }

    private static HttpResponseMessage CreatePackageResponse(string content, bool includeContentLength = true)
    {
        HttpContent httpContent = includeContentLength
            ? new ByteArrayContent(Encoding.UTF8.GetBytes(content))
            : new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(content)));
        if (!includeContentLength)
        {
            httpContent.Headers.ContentLength = null;
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = httpContent };
    }

    private sealed class StubServiceIndexClient : INuGetServiceIndexClient
    {
        public int Calls { get; private set; }

        public Task<Uri> GetPackageBaseAddressAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(PackageBaseAddress);
        }
    }

    private sealed class RecordingRetryClock : INuGetRetryClock
    {
        public List<TimeSpan> Delays { get; } = [];

        public Action? OnDelay { get; init; }

        public DateTimeOffset UtcNow => new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnDelay?.Invoke();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class SequencePartialPathProvider(params string[] paths) : INuGetPartialPathProvider
    {
        private readonly Queue<string> _paths = new(paths);

        public int RequestCount { get; private set; }

        public string GetCandidatePath(string finalPath)
        {
            RequestCount++;
            return _paths.Dequeue();
        }
    }

    private sealed class GuidPartialPathProvider : INuGetPartialPathProvider
    {
        public string GetCandidatePath(string finalPath) => $"{finalPath}.{Guid.NewGuid():N}.partial";
    }

    private sealed class DelegatePartialFileCreator(
        Func<string, int, FileStream> create) : INuGetPartialFileCreator
    {
        public int Attempts { get; private set; }

        public FileStream CreateNew(string path)
        {
            Attempts++;
            return create(path, Attempts);
        }
    }

    private static FileStream CreateRealPartial(string path)
    {
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private sealed class DisposalTrackingContent : ByteArrayContent
    {
        public DisposalTrackingContent()
            : base("retry"u8.ToArray())
        {
        }

        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responseFactory(request, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"smartpipe-fetch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class CancellationBlockingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
                completion);
            return await completion.Task.ConfigureAwait(false);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ConcurrentResponseGate(int expectedReaders)
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;

        public TaskCompletionSource AllReadersStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream CreateStream() => new GatedStream(this);

        public void Release() => _release.TrySetResult();

        private async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readers) == expectedReaders)
            {
                AllReadersStarted.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var bytes = "package"u8;
            bytes.CopyTo(buffer.Span);
            return bytes.Length;
        }

        private sealed class GatedStream(ConcurrentResponseGate owner) : Stream
        {
            private bool _read;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_read)
                {
                    return ValueTask.FromResult(0);
                }

                _read = true;
                return owner.ReadAsync(buffer, cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
