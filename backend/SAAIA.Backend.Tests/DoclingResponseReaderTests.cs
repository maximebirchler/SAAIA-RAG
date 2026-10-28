using System.Runtime.InteropServices;
using Xunit;

public sealed class DoclingResponseReaderTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(70_013)]
    public async Task Response_owns_the_accumulator_array_without_a_final_entity_copy(int length)
    {
        var source = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        using var content = new ByteArrayContent(source);
        var accumulator = new ObservedBuffer();

        ReadOnlyMemory<byte> result = await DoclingResponseReader.ReadAsync(content, Math.Max(1, length),
            CancellationToken.None, () => accumulator);

        Assert.True(accumulator.Disposed);
        Assert.True(MemoryMarshal.TryGetArray(result, out var view));
        Assert.Same(accumulator.GetBuffer(), view.Array);
        Assert.Equal(0, view.Offset);
        Assert.Equal(length, view.Count);
        Assert.True(result.Span.SequenceEqual(source));
        if (length > 0) Assert.True(view.Array!.Length > result.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2L)]
    public async Task Observed_size_limit_refuses_unknown_or_inaccurate_content_length(long? declaredLength)
    {
        using var content = new StreamContent(new ChunkStream([1, 2, 3, 4, 5], maximumRead: 2));
        content.Headers.ContentLength = declaredLength;
        var accumulator = new ObservedBuffer();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DoclingResponseReader.ReadAsync(
            content, 4, CancellationToken.None, () => accumulator));
        Assert.Equal("Docling response exceeds the configured 4 byte limit.", error.Message);
        Assert.True(accumulator.Disposed);
    }

    [Fact]
    public async Task Oversized_declared_length_refuses_before_allocating_or_reading_body()
    {
        using var source = new ChunkStream([1], maximumRead: 1);
        using var content = new StreamContent(source);
        content.Headers.ContentLength = 100;
        var created = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => DoclingResponseReader.ReadAsync(content, 4,
            CancellationToken.None, () => { created = true; return new(); }));
        Assert.False(created);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Chunked_body_keeps_all_bytes_within_exact_limit()
    {
        byte[] expected = [0, 255, 195, 137, 10, 9, 1, 2, 3];
        using var content = new StreamContent(new ChunkStream(expected, maximumRead: 2));
        Assert.Null(content.Headers.ContentLength);
        ReadOnlyMemory<byte> actual = await DoclingResponseReader.ReadAsync(content, expected.Length, CancellationToken.None);
        Assert.True(actual.Span.SequenceEqual(expected));
    }

    [Fact]
    public async Task Cancellation_after_a_partial_read_disposes_buffer_and_returns_no_partial_entity()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new ChunkStream([1, 2, 3, 4], maximumRead: 2, afterRead: cancellation.Cancel);
        using var content = new StreamContent(source);
        var accumulator = new ObservedBuffer();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DoclingResponseReader.ReadAsync(content, 4,
            cancellation.Token, () => accumulator));
        Assert.True(accumulator.Disposed);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task Reused_nonempty_accumulator_is_refused_instead_of_mixing_entities()
    {
        using var content = new ByteArrayContent([1]);
        var accumulator = new ObservedBuffer();
        accumulator.WriteByte(42);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DoclingResponseReader.ReadAsync(content, 4,
            CancellationToken.None, () => accumulator));
        Assert.Equal("docling_response_buffer_must_be_empty_writable", error.Message);
        Assert.True(accumulator.Disposed);
    }

    private sealed class ObservedBuffer : MemoryStream
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ChunkStream(byte[] source, int maximumRead, Action? afterRead = null) : Stream
    {
        private int _position;
        internal int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(maximumRead, buffer.Length), source.Length - _position);
            if (count > 0)
            {
                source.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                Reads++;
                afterRead?.Invoke();
            }
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
