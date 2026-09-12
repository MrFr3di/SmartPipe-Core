using System.Reflection;
using System.Text;
using SmartPipe.Core;
using SmartPipe.Extensions.Csv;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv.Tests;

public sealed class CsvStrictSinkTests
{
    [Fact]
    public async Task FileSink_CreateWritesHeaderAndBoundedRecords()
    {
        var path = CreatePath();
        try
        {
            var sink = await ActivateSinkAsync(
                CsvPipelineComponents.FileSink<Person>(path, new CsvSinkOptions()));
            await sink.InitializeAsync();
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(new Person { Name = "Alice", Age = 41 }));
            await sink.DisposeAsync();

            Assert.Equal("Name,Age\r\nAlice,41\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public async Task FileSink_AppendDoesNotDuplicateHeader()
    {
        var path = CreatePath();
        await File.WriteAllTextAsync(path, "Name,Age\r\nAlice,41\r\n", new UTF8Encoding(false));
        try
        {
            var sink = await ActivateSinkAsync(
                CsvPipelineComponents.FileSink<Person>(
                    path,
                    new CsvSinkOptions { OpenMode = CsvFileOpenMode.Append }));
            await sink.InitializeAsync();
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(new Person { Name = "Bob", Age = 42 }));
            await sink.DisposeAsync();

            Assert.Equal("Name,Age\r\nAlice,41\r\nBob,42\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public async Task FileSink_AppendToBomOnlyFileDoesNotDuplicateBom()
    {
        var path = CreatePath();
        var encoding = new UTF8Encoding(false, true);
        var existingBom = new UTF8Encoding(true, true).GetPreamble();
        await File.WriteAllBytesAsync(path, existingBom);
        try
        {
            var sink = await ActivateSinkAsync(
                CsvPipelineComponents.FileSink<Person>(
                    path,
                    new CsvSinkOptions
                    {
                        Encoding = encoding,
                        EmitByteOrderMark = true,
                        OpenMode = CsvFileOpenMode.Append,
                    }));
            await sink.InitializeAsync();
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(new Person { Name = "Alice", Age = 41 }));
            await sink.DisposeAsync();

            var expected = existingBom
                .Concat(encoding.GetBytes("Name,Age\r\nAlice,41\r\n"))
                .ToArray();
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public async Task FileSink_OversizeRecordDoesNotPublishPartialBytes()
    {
        var path = CreatePath();
        try
        {
            var sink = await ActivateSinkAsync(
                CsvPipelineComponents.FileSink<Person>(
                    path,
                    new CsvSinkOptions { MaxRecordSizeCharacters = 32 }));
            await sink.InitializeAsync();

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                    new Person { Name = new string('x', 128), Age = 1 })));
            await sink.DisposeAsync();

            Assert.Equal("Name,Age\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public async Task FileSink_FormulaThrowRejectsCellBeforePublication()
    {
        var path = CreatePath();
        try
        {
            var sink = await ActivateSinkAsync(
                CsvPipelineComponents.FileSink<Person>(
                    path,
                    new CsvSinkOptions { FormulaInjectionMode = CsvFormulaInjectionMode.Throw }));
            await sink.InitializeAsync();

            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                    new Person { Name = "=SUM(1,1)", Age = 1 })));
            await sink.DisposeAsync();

            Assert.Equal("Name,Age\r\n", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public async Task FileSink_PartialWriteFailureRollsBackToRecordCheckpoint()
    {
        var stream = new FaultInjectingStream { FailNextWriteAfterBytes = 3 };
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            FlushEveryRecords = 1,
        });

        await sink.InitializeAsync();
        var failure = await Assert.ThrowsAsync<TestWriteException>(async () =>
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                new Person { Name = "Alice", Age = 41 })));

        Assert.Equal("primary-write", failure.Message);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task FileSink_FlushFailureRollsBackToRecordCheckpoint()
    {
        var stream = new FaultInjectingStream { FailNextFlush = true };
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            FlushEveryRecords = 1,
        });

        await sink.InitializeAsync();
        var failure = await Assert.ThrowsAsync<TestFlushException>(async () =>
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                new Person { Name = "Alice", Age = 41 })));

        Assert.Equal("primary-flush", failure.Message);
        Assert.Equal(0, stream.Length);
        Assert.Equal(0, stream.Position);
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task FileSink_RollbackFailureAggregatesPrimaryBeforeCleanup()
    {
        var stream = new FaultInjectingStream
        {
            FailNextWriteAfterBytes = 2,
            FailNextRollback = true,
        };
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            FlushEveryRecords = 1,
        });

        await sink.InitializeAsync();
        var failure = await Assert.ThrowsAsync<AggregateException>(async () =>
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                new Person { Name = "Alice", Age = 41 })));

        Assert.Collection(
            failure.InnerExceptions,
            primary => Assert.IsType<TestWriteException>(primary),
            cleanup => Assert.IsType<TestRollbackException>(cleanup));
        await sink.DisposeAsync();
    }

    [Fact]
    public async Task FileSink_ConcurrentDisposeIsSingleFlightAndFlushesTailOnce()
    {
        var stream = new FaultInjectingStream { BlockDispose = true };
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            FlushEveryRecords = 100,
        });

        await sink.InitializeAsync();
        await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
            new Person { Name = "Alice", Age = 41 }));
        Assert.Equal(0, stream.FlushCalls);

        var first = sink.DisposeAsync().AsTask();
        await stream.DisposeStarted.Task;
        var second = sink.DisposeAsync().AsTask();

        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(1, stream.DisposeCalls);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        stream.ReleaseDispose();
        await Task.WhenAll(first, second);
        await sink.DisposeAsync();
        Assert.Equal(1, stream.FlushCalls);
        Assert.Equal(1, stream.DisposeCalls);
    }

    [Fact]
    public async Task FileSink_CancellationAndDisposeRaceRollsBackAndCompletes()
    {
        var stream = new FaultInjectingStream { BlockNextWrite = true };
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            FlushEveryRecords = 100,
        });
        using var cancellation = new CancellationTokenSource();

        await sink.InitializeAsync();
        var write = sink.WriteAsync(
            ProcessingEnvelope<Person>.Create(new Person { Name = "Alice", Age = 41 }),
            cancellation.Token).AsTask();
        await stream.WriteStarted.Task;
        cancellation.Cancel();
        var dispose = sink.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await write);
        await dispose;
        Assert.Equal(0, stream.LengthAtDispose);
        Assert.Equal(1, stream.DisposeCalls);
    }

    [Fact]
    public async Task FileSink_AppendFailurePreservesPreexistingBytes()
    {
        var existing = Encoding.UTF8.GetBytes("existing\r\n");
        var stream = new FaultInjectingStream(existing);
        var sink = CreateInjectedSink(stream, new CsvSinkOptions
        {
            HasHeaderRecord = false,
            OpenMode = CsvFileOpenMode.Append,
            FlushEveryRecords = 1,
        });

        await sink.InitializeAsync();
        stream.FailNextWriteAfterBytes = 2;
        await Assert.ThrowsAsync<TestWriteException>(async () =>
            await sink.WriteAsync(ProcessingEnvelope<Person>.Create(
                new Person { Name = "Alice", Age = 41 })));

        Assert.Equal(existing, stream.ToArray());
        await sink.DisposeAsync();
    }

    private static StrictCsvFileSink<Person> CreateInjectedSink(
        Stream stream,
        CsvSinkOptions options) =>
        new(
            "injected.csv",
            CsvSinkOptionsSnapshot.Create(options),
            CsvMapRegistration<Person>.Auto,
            CancellationToken.None,
            _ => stream);

    private static async Task<IPipelineSink<Person>> ActivateSinkAsync(
        PipelineComponent<IPipelineSink<Person>> descriptor)
    {
        var activator = descriptor.GetType().GetProperty(
            "Activator",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(descriptor) as Delegate;
        Assert.NotNull(activator);
        var valueTask = activator!.DynamicInvoke(
            new PipelineActivationContext(new PipelineKey("csv-sink"), Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        Assert.NotNull(valueTask);
        var task = (Task)valueTask!.GetType().GetMethod("AsTask")!.Invoke(valueTask, null)!;
        await task;
        return Assert.IsAssignableFrom<IPipelineSink<Person>>(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private static string CreatePath() =>
        Path.Combine(Path.GetTempPath(), $"smartpipe-csv-sink-{Guid.NewGuid():N}.csv");

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class Person
    {
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private sealed class TestWriteException(string message) : IOException(message);
    private sealed class TestFlushException(string message) : IOException(message);
    private sealed class TestRollbackException(string message) : IOException(message);

    private sealed class FaultInjectingStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly TaskCompletionSource _disposeRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int? FailNextWriteAfterBytes { get; set; }
        public bool FailNextFlush { get; set; }
        public bool FailNextRollback { get; set; }
        public bool BlockDispose { get; set; }
        public bool BlockNextWrite { get; set; }
        public int FlushCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public long LengthAtDispose { get; private set; } = -1;
        public TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FaultInjectingStream(byte[]? initialBytes = null)
        {
            _inner = new MemoryStream();
            if (initialBytes is not null)
            {
                _inner.Write(initialBytes);
                _inner.Position = _inner.Length;
            }
        }

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushCalls++;
            if (FailNextFlush)
            {
                FailNextFlush = false;
                return Task.FromException(new TestFlushException("primary-flush"));
            }

            return _inner.FlushAsync(cancellationToken);
        }

        public void ReleaseDispose() => _disposeRelease.TrySetResult();

        public override async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            LengthAtDispose = _inner.Length;
            DisposeStarted.TrySetResult();
            if (BlockDispose)
                await _disposeRelease.Task;
            await _inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value)
        {
            if (FailNextRollback)
            {
                FailNextRollback = false;
                throw new TestRollbackException("rollback");
            }

            _inner.SetLength(value);
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (BlockNextWrite)
            {
                BlockNextWrite = false;
                WriteStarted.TrySetResult();
                await TaskCompletionSourceWithoutResult.Never.Task.WaitAsync(cancellationToken);
            }

            if (FailNextWriteAfterBytes is int count)
            {
                FailNextWriteAfterBytes = null;
                await _inner.WriteAsync(buffer[..Math.Min(count, buffer.Length)], cancellationToken);
                throw new TestWriteException("primary-write");
            }

            await _inner.WriteAsync(buffer, cancellationToken);
        }

        private static class TaskCompletionSourceWithoutResult
        {
            public static readonly TaskCompletionSource Never =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
