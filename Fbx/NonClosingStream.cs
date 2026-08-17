namespace SECmd.Fbx
{
    /// <summary>
    /// Passes every operation through to an underlying stream, but ignores
    /// disposal.
    /// </summary>
    /// <remarks>
    /// MeshIO's parsers and writers dispose the stream they were handed. That is
    /// fine for its own file-based entry points, but wrong for a caller that owns
    /// the stream and wants to keep reading from or writing to it — including the
    /// common case of round-tripping through a <see cref="MemoryStream"/>.
    /// Interposing this lets us dispose the parser or writer, so its own buffers are
    /// released, without closing the caller's stream underneath it.
    /// </remarks>
    internal sealed class NonClosingStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override int ReadByte() => _inner.ReadByte();

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);

        public override void WriteByte(byte value) => _inner.WriteByte(value);

        protected override void Dispose(bool disposing)
        {
            // Deliberately does not touch the inner stream.
            Flush();
        }
    }
}
