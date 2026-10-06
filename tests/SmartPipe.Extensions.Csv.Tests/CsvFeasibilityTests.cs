using System.Globalization;
using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;
using SmartPipe.Extensions.Csv.Internal;

namespace SmartPipe.Extensions.Csv.Tests;

public sealed class CsvFeasibilityTests
{
    [Fact]
    public async Task CsvHelperAsyncRead_UsesOnlyBridgeAsyncReadsAndOnePersistentContext()
    {
        const string csv = "Name,Note\r\nAlice,plain\r\nBob,second\r\n";
        using var input = new TrackingTextReader(csv, chunkSize: 1);
        using var bridge = CreateBridge(input, TestContext.Current.CancellationToken);
        using var reader = new CsvReader(bridge, CreateConfiguration(), leaveOpen: true);
        var context = reader.Context;

        var rows = new List<CsvRow>();
        await foreach (var row in reader.GetRecordsAsync<CsvRow>(TestContext.Current.CancellationToken))
            rows.Add(row);

        Assert.Equal(["Alice", "Bob"], rows.Select(row => row.Name));
        Assert.Equal(["plain", "second"], rows.Select(row => row.Note));
        Assert.Same(context, reader.Context);
        Assert.True(bridge.AsyncReadCalls > 0);
        Assert.Equal(0, bridge.SyncReadCalls);
        Assert.True(input.AsyncReadCalls > 0);
        Assert.Equal(0, input.SyncReadCalls);
    }

    [Fact]
    public async Task BridgeCorpus_AgreesWithDirectCsvHelperForMultilineAndDoubledQuotes()
    {
        const string csv = "Name,Note\nAlice,\"line one\nline \"\"two\"\"\"\nBob,plain\n";

        var expected = await ParseDirectAsync(csv);
        using var input = new TrackingTextReader(csv, chunkSize: 1);
        using var bridge = CreateBridge(input, TestContext.Current.CancellationToken);
        using var reader = new CsvReader(bridge, CreateConfiguration(), leaveOpen: true);
        var actual = new List<CsvRow>();
        await foreach (var row in reader.GetRecordsAsync<CsvRow>(TestContext.Current.CancellationToken))
            actual.Add(row);

        Assert.Equal(expected.Select(row => (row.Name, row.Note)), actual.Select(row => (row.Name, row.Note)));
    }

    [Fact]
    public async Task CsvHelperAsyncRead_CancellationInterruptsUnderlyingFramingRead()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new BlockingTextReader();
        using var bridge = CreateBridge(input, cancellation.Token);
        using var reader = new CsvReader(bridge, CreateConfiguration(), leaveOpen: true);
        var consume = ConsumeAsync(reader, cancellation.Token);

        await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await consume);
        Assert.Equal(0, input.SyncReadCalls);
        Assert.Equal(0, bridge.SyncReadCalls);
    }

    [Fact]
    public async Task Bridge_PerCallCancellationReachesUnderlyingFramingRead()
    {
        using var callCancellation = new CancellationTokenSource();
        using var input = new BlockingTextReader();
        using var bridge = CreateBridge(input, CancellationToken.None);
        var pending = bridge.ReadAsync(new char[32], callCancellation.Token).AsTask();

        await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        callCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(callCancellation.Token, input.LastCancellationToken);
    }

    [Fact]
    public async Task Framer_RejectsRecordBeforeRetainingBeyondCharacterBound()
    {
        using var input = new StringReader("header\nthis-record-is-too-long\n");
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 8,
            maxFieldSizeCharacters: 8,
            maxColumnCount: 4,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
    }

    [Fact]
    public async Task Framer_RejectsFieldBeforeRetainingBeyondFieldBound()
    {
        using var input = new StringReader("header\nfield-too-long,1\n");
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 64,
            maxFieldSizeCharacters: 6,
            maxColumnCount: 4,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
    }

    [Fact]
    public async Task Framer_RejectsColumnBeforeRetainingBeyondColumnBound()
    {
        using var input = new StringReader("h1,h2\n1,2,3\n");
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 64,
            maxFieldSizeCharacters: 64,
            maxColumnCount: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
    }

    [Fact]
    public async Task Framer_AcceptsExactLimitsAndRejectsEachPlusOne()
    {
        await AssertBoundaryAsync("abcd\n", record: 5, field: 4, columns: 1, shouldPass: true);
        await AssertBoundaryAsync("abcde\n", record: 5, field: 5, columns: 1, shouldPass: false);
        await AssertBoundaryAsync("abcd\n", record: 8, field: 4, columns: 1, shouldPass: true);
        await AssertBoundaryAsync("abcde\n", record: 8, field: 4, columns: 1, shouldPass: false);
        await AssertBoundaryAsync("a,b,c\n", record: 8, field: 4, columns: 3, shouldPass: true);
        await AssertBoundaryAsync("a,b,c,d\n", record: 8, field: 4, columns: 3, shouldPass: false);
    }

    [Fact]
    public async Task Framer_OversizeRecordDiscardsToBoundaryAndRecoversNextRecord()
    {
        const int maxRecordSize = 16;
        var csv = $"header\n{new string('x', 100_000)}\nnext\n";
        using var input = new StringReader(csv);
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: maxRecordSize,
            maxFieldSizeCharacters: maxRecordSize,
            maxColumnCount: 4,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());

        var next = await framer.ReadAsync();
        Assert.Equal("next\n", next!.Value.Text);
        var peakProperty = typeof(CsvLogicalRecordFramer).GetProperty(
            "PeakRetainedCharacters",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(peakProperty);
        Assert.InRange(Assert.IsType<int>(peakProperty!.GetValue(framer)), 0, maxRecordSize);
    }

    [Fact]
    public async Task Framer_ColumnOverflowDiscardsToBoundaryAndRecoversNextRecord()
    {
        using var input = new StringReader("h1,h2\n1,2,3,4\nnext\n");
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 64,
            maxFieldSizeCharacters: 64,
            maxColumnCount: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());

        var next = await framer.ReadAsync();
        Assert.Equal("next\n", next!.Value.Text);
    }

    [Fact]
    public async Task Framer_AmbiguousQuotedEofIsUnrecoverable()
    {
        using var input = new StringReader("header\n\"unterminated");
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 64,
            maxFieldSizeCharacters: 64,
            maxColumnCount: 4,
            cancellationToken: TestContext.Current.CancellationToken);

        _ = await framer.ReadAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
    }

    private static CsvBoundedRecordTextReader CreateBridge(TextReader input, CancellationToken cancellationToken)
    {
        var framer = new CsvLogicalRecordFramer(
            input,
            delimiter: ',',
            quote: '"',
            maxRecordSizeCharacters: 1024,
            maxFieldSizeCharacters: 512,
            maxColumnCount: 4,
            cancellationToken);
        return new CsvBoundedRecordTextReader(framer, cancellationToken);
    }

    private static async Task AssertBoundaryAsync(
        string text,
        int record,
        int field,
        int columns,
        bool shouldPass)
    {
        using var input = new StringReader(text);
        var framer = new CsvLogicalRecordFramer(
            input, ',', '"', record, field, columns, TestContext.Current.CancellationToken);
        if (shouldPass)
            Assert.Equal(text, (await framer.ReadAsync())!.Value.Text);
        else
            await Assert.ThrowsAsync<InvalidDataException>(async () => await framer.ReadAsync());
    }

    private static CsvConfiguration CreateConfiguration() => new(CultureInfo.InvariantCulture)
    {
        Mode = CsvMode.RFC4180,
        Escape = '"',
        HasHeaderRecord = true,
        IgnoreBlankLines = true,
        ExceptionMessagesContainRawData = false,
    };

    private static async Task<List<CsvRow>> ParseDirectAsync(string csv)
    {
        using var input = new StringReader(csv);
        using var reader = new CsvReader(input, CreateConfiguration(), leaveOpen: true);
        var rows = new List<CsvRow>();
        await foreach (var row in reader.GetRecordsAsync<CsvRow>(TestContext.Current.CancellationToken))
            rows.Add(row);
        return rows;
    }

    private static async Task ConsumeAsync(CsvReader reader, CancellationToken cancellationToken)
    {
        await foreach (var _ in reader.GetRecordsAsync<CsvRow>(cancellationToken))
        {
        }
    }

    private sealed class TrackingTextReader(string text, int chunkSize) : TextReader
    {
        private readonly string _text = text;
        private readonly int _chunkSize = chunkSize;
        private int _offset;

        public int AsyncReadCalls { get; private set; }
        public int SyncReadCalls { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            SyncReadCalls++;
            throw new InvalidOperationException("Synchronous reads are not part of the async bridge.");
        }

        public override int Read(Span<char> buffer)
        {
            SyncReadCalls++;
            throw new InvalidOperationException("Synchronous reads are not part of the async bridge.");
        }

        public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
            ReadAsync(buffer.AsMemory(index, count), CancellationToken.None).AsTask();

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            AsyncReadCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == _text.Length || buffer.Length == 0)
                return ValueTask.FromResult(0);

            var length = Math.Min(Math.Min(_chunkSize, buffer.Length), _text.Length - _offset);
            _text.AsMemory(_offset, length).CopyTo(buffer);
            _offset += length;
            return ValueTask.FromResult(length);
        }
    }

    private sealed class BlockingTextReader : TextReader
    {
        private CancellationTokenRegistration _registration;

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SyncReadCalls { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            SyncReadCalls++;
            throw new InvalidOperationException("Synchronous reads are not part of the async bridge.");
        }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            LastCancellationToken = cancellationToken;
            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return new ValueTask<int>(completion.Task);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _registration.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class CsvRow
    {
        public string Name { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
    }
}
