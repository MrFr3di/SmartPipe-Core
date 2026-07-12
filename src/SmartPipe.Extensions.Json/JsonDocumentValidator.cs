using System.Buffers;
using System.Text.Json;

namespace SmartPipe.Extensions;

internal static class JsonDocumentValidator
{
    internal const int SegmentSize = 8192;

    public static async ValueTask ValidateAsync(
        Stream stream,
        int maxDepth,
        string path,
        CancellationToken ct,
        ArrayPool<byte>? pool = null)
    {
        pool ??= ArrayPool<byte>.Shared;
        var buffer = new SegmentedBuffer(pool);
        var state = new JsonReaderState(new JsonReaderOptions
        {
            MaxDepth = maxDepth,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        try
        {
            while (true)
            {
                var segment = pool.Rent(SegmentSize);
                int read;
                try
                {
                    read = await stream.ReadAsync(segment.AsMemory(0, SegmentSize), ct).ConfigureAwait(false);
                }
                catch
                {
                    pool.Return(segment);
                    throw;
                }
                var final = read == 0;
                if (read != 0)
                    buffer.Append(segment, read);
                else
                    pool.Return(segment);
                long bytesConsumed;
                JsonReaderState nextState;
                {
                    var sequence = buffer.Sequence;
                    var reader = new Utf8JsonReader(sequence, final, state);
                    while (reader.Read()) { }
                    bytesConsumed = reader.BytesConsumed;
                    nextState = reader.CurrentState;
                }
                state = nextState;
                buffer.Advance(bytesConsumed);
                if (final)
                    break;
            }
        }
        catch (JsonException exception)
        {
            throw new JsonException($"Invalid JSON document in '{path}': {exception.Message}", exception);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private sealed class SegmentedBuffer(ArrayPool<byte> pool) : IDisposable
    {
        private Segment? _head;
        private Segment? _tail;
        private int _headIndex;

        public ReadOnlySequence<byte> Sequence => _head is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(_head, _headIndex, _tail!, _tail!.Memory.Length);

        public void Append(byte[] buffer, int length)
        {
            var segment = new Segment(buffer, length, pool);
            if (_tail is null)
                _head = _tail = segment;
            else
                _tail = _tail.Append(segment);
        }

        public void Advance(long count)
        {
            while (count != 0 && _head is not null)
            {
                var available = _head.Memory.Length - _headIndex;
                if (count < available)
                {
                    _headIndex += checked((int)count);
                    return;
                }
                count -= available;
                var consumed = _head;
                _head = _head.NextSegment;
                consumed.Return();
                _headIndex = 0;
            }
            if (_head is null)
                _tail = null;
        }

        public void Dispose()
        {
            while (_head is not null)
            {
                var segment = _head;
                _head = segment.NextSegment;
                segment.Return();
            }
            _tail = null;
            _headIndex = 0;
        }
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        private readonly byte[] _buffer;
        private readonly ArrayPool<byte> _pool;

        public Segment(byte[] buffer, int length, ArrayPool<byte> pool)
        {
            _buffer = buffer;
            _pool = pool;
            Memory = buffer.AsMemory(0, length);
        }

        public Segment? NextSegment => (Segment?)Next;

        public Segment Append(Segment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
            return next;
        }

        public void Return() => _pool.Return(_buffer);
    }
}
