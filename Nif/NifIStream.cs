using System.Buffers.Binary;
using System.Text;

namespace SECmd.Nif
{
    /// <summary>
    /// What the stream needs to know about the file it is reading, and the one
    /// callback it makes back into the model.
    /// </summary>
    public interface INifStreamContext
    {
        /// <summary>The packed file version, once the header string has established it.</summary>
        uint Version { get; }

        /// <summary>
        /// Called when the header string is read, to let the model settle on a
        /// version before any other field is decoded.
        /// </summary>
        bool SetHeaderString(string value, uint peekedVersion);
    }

    /// <summary>
    /// Reads a single <see cref="NifValue"/> from a stream, according to its type.
    /// </summary>
    /// <remarks>
    /// This is the port of NifSkope's NifIStream, and it is deliberately the only
    /// place that knows how a type becomes bytes. Everything above it — arrays,
    /// structs, blocks, conditions — is a tree walk that eventually calls
    /// <see cref="Read"/> on a leaf.
    ///
    /// Three pieces of state depend on the file version and are re-derived by
    /// <see cref="Initialise"/> whenever the version changes:
    /// booleans shrink from 32 to 8 bits after 4.0.0.2, links are stored off by one
    /// before 3.3.0.13, and strings become indices into a header table from
    /// 20.1.0.3 onward.
    /// </remarks>
    public sealed class NifIStream
    {
        /// <summary>The longest string the reader will accept, as a sanity bound.</summary>
        private const int MaxStringLength = 0x8000;

        /// <summary>
        /// NIF strings are bytes, and Latin-1 is the only encoding that maps every
        /// byte to a character and back. Decoding as UTF-8 would make an unusual
        /// byte sequence unrepresentable and corrupt the file on save.
        /// </summary>
        private static readonly Encoding StringEncoding = Encoding.Latin1;

        private readonly Stream _stream;
        private readonly INifStreamContext _context;
        private readonly byte[] _buffer = new byte[64];

        private bool _bool32Bit;
        private bool _linkAdjust;
        private bool _stringAdjust;
        private bool _bigEndian;

        public NifIStream(INifStreamContext context, Stream stream)
        {
            _context = context;
            _stream = stream;
            Initialise();
        }

        /// <summary>True once a big-endian file version has been seen.</summary>
        public bool IsBigEndian => _bigEndian;

        /// <summary>
        /// Re-derives the version-dependent decoding rules. Called at construction
        /// and again as soon as the header string names a version.
        /// </summary>
        public void Initialise()
        {
            uint version = _context.Version;

            _bool32Bit = version <= 0x04000002;
            _linkAdjust = version < 0x0303000D;
            _stringAdjust = version >= 0x14010003;
            _bigEndian = false;
        }

        /// <summary>Rewinds to the start of the file, as NifSkope does after sniffing the version.</summary>
        public void Reset() => _stream.Position = 0;

        /// <summary>Reads one value of its already-established type. False on a short read.</summary>
        public bool Read(ref NifValue value)
        {
            switch (value.Type)
            {
                case NifValueType.Bool:
                    if (_bool32Bit)
                    {
                        if (!TryReadUInt32(out uint b32))
                            return false;

                        value.SetCount(b32);
                    }
                    else
                    {
                        if (!TryReadByte(out byte b8))
                            return false;

                        value.SetCount(b8);
                    }

                    return true;

                case NifValueType.Byte:
                {
                    if (!TryReadByte(out byte v))
                        return false;

                    value.SetCount(v);
                    return true;
                }

                case NifValueType.Word:
                case NifValueType.Short:
                case NifValueType.Flags:
                case NifValueType.BlockTypeIndex:
                {
                    if (!TryReadUInt16(out ushort v))
                        return false;

                    value.SetCount(v);
                    return true;
                }

                case NifValueType.StringOffset:
                case NifValueType.Int:
                case NifValueType.UInt:
                case NifValueType.StringIndex:
                {
                    if (!TryReadUInt32(out uint v))
                        return false;

                    value.SetCount(v);
                    return true;
                }

                case NifValueType.ULittle32:
                {
                    // Explicitly little-endian regardless of the file's byte order.
                    if (!TryFill(4))
                        return false;

                    value.SetCount(BinaryPrimitives.ReadUInt32LittleEndian(_buffer));
                    return true;
                }

                case NifValueType.Int64:
                case NifValueType.UInt64:
                case NifValueType.BSVertexDesc:
                {
                    if (!TryReadUInt64(out ulong v))
                        return false;

                    if (value.Type == NifValueType.BSVertexDesc)
                        value.Set(new BSVertexDesc(v));
                    else
                        value.SetCount(v);

                    return true;
                }

                case NifValueType.Link:
                case NifValueType.UpLink:
                {
                    if (!TryReadUInt32(out uint v))
                        return false;

                    int link = unchecked((int)v);

                    // Before 3.3.0.13 a link is stored one higher than the block it
                    // names, with 0 meaning null.
                    if (_linkAdjust)
                        link--;

                    value.SetLink(link);
                    return true;
                }

                case NifValueType.Float:
                {
                    if (!TryReadUInt32(out uint bits))
                        return false;

                    value.SetCount(bits);
                    return true;
                }

                case NifValueType.Hfloat:
                {
                    if (!TryReadUInt16(out ushort half))
                        return false;

                    value.SetFloat(NifPack.HalfToFloat(half));
                    return true;
                }

                case NifValueType.Normbyte:
                {
                    if (!TryReadByte(out byte v))
                        return false;

                    value.SetFloat(NifPack.ByteToSNorm(v));
                    return true;
                }

                case NifValueType.ByteVector3:
                {
                    if (!TryFill(3))
                        return false;

                    value.Set(new NifVector3(
                        NifPack.ByteToSNorm(_buffer[0]),
                        NifPack.ByteToSNorm(_buffer[1]),
                        NifPack.ByteToSNorm(_buffer[2])));
                    return true;
                }

                case NifValueType.UshortVector3:
                {
                    if (!TryReadUInt16(out ushort x) || !TryReadUInt16(out ushort y) || !TryReadUInt16(out ushort z))
                        return false;

                    value.Set(new NifVector3(x, y, z));
                    return true;
                }

                case NifValueType.HalfVector3:
                {
                    if (!TryReadUInt16(out ushort x) || !TryReadUInt16(out ushort y) || !TryReadUInt16(out ushort z))
                        return false;

                    value.Set(new NifVector3(
                        NifPack.HalfToFloat(x),
                        NifPack.HalfToFloat(y),
                        NifPack.HalfToFloat(z)));
                    return true;
                }

                case NifValueType.HalfVector2:
                {
                    if (!TryReadUInt16(out ushort x) || !TryReadUInt16(out ushort y))
                        return false;

                    value.Set(new NifVector2(NifPack.HalfToFloat(x), NifPack.HalfToFloat(y)));
                    return true;
                }

                case NifValueType.Vector2:
                {
                    if (!TryReadSingle(out float x) || !TryReadSingle(out float y))
                        return false;

                    value.Set(new NifVector2(x, y));
                    return true;
                }

                case NifValueType.Vector3:
                {
                    if (!TryReadSingle(out float x) || !TryReadSingle(out float y) || !TryReadSingle(out float z))
                        return false;

                    value.Set(new NifVector3(x, y, z));
                    return true;
                }

                case NifValueType.Vector4:
                {
                    if (!TryReadSingle(out float x) || !TryReadSingle(out float y)
                        || !TryReadSingle(out float z) || !TryReadSingle(out float w))
                        return false;

                    value.Set(new NifVector4(x, y, z, w));
                    return true;
                }

                case NifValueType.Quat:
                {
                    // nif.xml's Quaternion is stored w first.
                    if (!TryReadSingle(out float w) || !TryReadSingle(out float x)
                        || !TryReadSingle(out float y) || !TryReadSingle(out float z))
                        return false;

                    value.Set(new NifQuat(w, x, y, z));
                    return true;
                }

                case NifValueType.QuatXYZW:
                {
                    // hkQuaternion is stored w last.
                    if (!TryReadSingle(out float x) || !TryReadSingle(out float y)
                        || !TryReadSingle(out float z) || !TryReadSingle(out float w))
                        return false;

                    value.Set(new NifQuat(w, x, y, z));
                    return true;
                }

                case NifValueType.Matrix:
                {
                    var m = new NifMatrix33();

                    if (!TryReadSingle(out m.M11) || !TryReadSingle(out m.M12) || !TryReadSingle(out m.M13)
                        || !TryReadSingle(out m.M21) || !TryReadSingle(out m.M22) || !TryReadSingle(out m.M23)
                        || !TryReadSingle(out m.M31) || !TryReadSingle(out m.M32) || !TryReadSingle(out m.M33))
                        return false;

                    value.Set(m);
                    return true;
                }

                case NifValueType.Matrix4:
                {
                    var m = new NifMatrix44();

                    if (!TryReadSingle(out m.M11) || !TryReadSingle(out m.M12) || !TryReadSingle(out m.M13) || !TryReadSingle(out m.M14)
                        || !TryReadSingle(out m.M21) || !TryReadSingle(out m.M22) || !TryReadSingle(out m.M23) || !TryReadSingle(out m.M24)
                        || !TryReadSingle(out m.M31) || !TryReadSingle(out m.M32) || !TryReadSingle(out m.M33) || !TryReadSingle(out m.M34)
                        || !TryReadSingle(out m.M41) || !TryReadSingle(out m.M42) || !TryReadSingle(out m.M43) || !TryReadSingle(out m.M44))
                        return false;

                    value.Set(m);
                    return true;
                }

                case NifValueType.Color3:
                {
                    if (!TryReadSingle(out float r) || !TryReadSingle(out float g) || !TryReadSingle(out float b))
                        return false;

                    value.Set(new NifColor3(r, g, b));
                    return true;
                }

                case NifValueType.Color4:
                {
                    if (!TryReadSingle(out float r) || !TryReadSingle(out float g)
                        || !TryReadSingle(out float b) || !TryReadSingle(out float a))
                        return false;

                    value.Set(new NifColor4(r, g, b, a));
                    return true;
                }

                case NifValueType.ByteColor4:
                {
                    if (!TryFill(4))
                        return false;

                    value.Set(new NifColor4(
                        _buffer[0] / 255f,
                        _buffer[1] / 255f,
                        _buffer[2] / 255f,
                        _buffer[3] / 255f));
                    return true;
                }

                case NifValueType.Triangle:
                {
                    if (!TryReadUInt16(out ushort a) || !TryReadUInt16(out ushort b) || !TryReadUInt16(out ushort c))
                        return false;

                    value.Set(new NifTriangle(a, b, c));
                    return true;
                }

                case NifValueType.SizedString:
                case NifValueType.Text:
                    return ReadSizedString(ref value);

                case NifValueType.ShortString:
                {
                    if (!TryReadByte(out byte length))
                        return false;

                    // The stored length counts a NUL terminator, so the text stops
                    // at the first NUL rather than filling the field.
                    return ReadStringOfLength(ref value, length, stopAtNul: true);
                }

                case NifValueType.String:
                case NifValueType.FilePath:
                    // From 20.1.0.3 these are indices into the header's string table;
                    // before that they are inline sized strings.
                    if (_stringAdjust)
                    {
                        value.ChangeType(NifValueType.StringIndex);

                        if (!TryReadUInt32(out uint index))
                            return false;

                        value.SetCount(index);
                        return true;
                    }

                    value.ChangeType(NifValueType.SizedString);
                    return ReadSizedString(ref value);

                case NifValueType.HeaderString:
                    return ReadHeaderString(ref value);

                case NifValueType.LineString:
                    return ReadLineString(ref value, 255);

                case NifValueType.Char8String:
                {
                    // A fixed eight-byte field, NUL-padded.
                    if (!TryFill(8))
                        return false;

                    int length = _buffer.AsSpan(0, 8).IndexOf((byte)0);
                    value.Set(StringEncoding.GetString(_buffer, 0, length < 0 ? 8 : length));
                    return true;
                }

                case NifValueType.FileVersion:
                    return ReadFileVersion(ref value);

                case NifValueType.ByteArray:
                {
                    if (!TryReadUInt32(out uint rawLength))
                        return false;

                    int length = unchecked((int)rawLength);
                    if (length < 0)
                        return false;

                    byte[] bytes = new byte[length];
                    if (!TryReadExact(bytes))
                        return false;

                    value.Set(bytes);
                    return true;
                }

                case NifValueType.StringPalette:
                {
                    if (!TryReadUInt32(out uint rawLength))
                        return false;

                    int length = unchecked((int)rawLength);
                    if (length is < 0 or > 0xFFFF)
                        return false;

                    byte[] bytes = new byte[length];
                    if (!TryReadExact(bytes))
                        return false;

                    value.Set(bytes);

                    // A trailing length that duplicates the one above; consumed and
                    // discarded, as NifSkope does.
                    return TryReadUInt32(out _);
                }

                case NifValueType.ByteMatrix:
                {
                    if (!TryReadUInt32(out uint rawWidth) || !TryReadUInt32(out uint rawHeight))
                        return false;

                    int width = unchecked((int)rawWidth);
                    int height = unchecked((int)rawHeight);

                    if (width < 0 || height < 0 || (long)width * height > int.MaxValue)
                        return false;

                    var matrix = new ByteMatrix(width, height);
                    if (!TryReadExact(matrix.Data))
                        return false;

                    value.Set(matrix);
                    return true;
                }

                case NifValueType.Blob:
                {
                    // A blob's size is fixed when the item is created, not stored.
                    byte[] blob = value.AsByteArray();
                    return blob.Length == 0 || TryReadExact(blob);
                }

                case NifValueType.None:
                    return true;

                default:
                    throw new NifFormatException($"no read rule for value type {value.Type}");
            }
        }

        // --- composite readers ------------------------------------------------

        private bool ReadSizedString(ref NifValue value)
        {
            if (!TryReadUInt32(out uint rawLength))
                return false;

            int length = unchecked((int)rawLength);

            if (length < 0 || length > MaxStringLength)
                return false;

            return ReadStringOfLength(ref value, length);
        }

        /// <summary>
        /// Reads <paramref name="length"/> bytes as text.
        /// </summary>
        /// <param name="stopAtNul">
        /// True for the field types whose stored length includes a NUL terminator.
        /// Length-prefixed strings deliberately do not stop, so that the prefix
        /// stays authoritative and the bytes round-trip unchanged.
        /// </param>
        private bool ReadStringOfLength(ref NifValue value, int length, bool stopAtNul = false)
        {
            if (length == 0)
            {
                value.Set(string.Empty);
                return true;
            }

            byte[] bytes = new byte[length];
            if (!TryReadExact(bytes))
                return false;

            int count = length;

            if (stopAtNul)
            {
                int nul = Array.IndexOf(bytes, (byte)0);

                if (nul >= 0)
                    count = nul;
            }

            value.Set(StringEncoding.GetString(bytes, 0, count));
            return true;
        }

        /// <summary>
        /// Reads the newline-terminated banner at the very start of the file, and
        /// hands it to the model so the version is settled before anything else.
        /// </summary>
        private bool ReadHeaderString(ref NifValue value)
        {
            if (!ReadLineString(ref value, 80))
                return false;

            // Peek at the version that follows, so the model can accept files whose
            // banner does not spell out a version.
            uint peeked = 0;
            long mark = _stream.Position;

            if (TryFill(4))
                peeked = BinaryPrimitives.ReadUInt32LittleEndian(_buffer);

            _stream.Position = mark;

            // NeoSteam writes a sentinel in place of a version.
            if (peeked == 0x08F35232)
                peeked = 0x0A010000;
            else if (peeked < 0x04000000)
                peeked = 0;

            bool accepted = _context.SetHeaderString(value.AsString(), peeked);

            // The version may have just changed, so the decoding rules must be
            // re-derived before any further field is read.
            Initialise();
            return accepted;
        }

        private bool ReadLineString(ref NifValue value, int maxLength)
        {
            var bytes = new List<byte>(64);

            for (int i = 0; i < maxLength; i++)
            {
                int c = _stream.ReadByte();

                if (c < 0)
                    return false;

                if (c == '\n')
                {
                    value.Set(StringEncoding.GetString(bytes.ToArray()));
                    return true;
                }

                bytes.Add((byte)c);
            }

            // Ran past the limit without finding a terminator.
            return false;
        }

        private bool ReadFileVersion(ref NifValue value)
        {
            if (!TryFill(4))
                return false;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(_buffer);

            // From 20.0.0.4 the version is followed by an endianness byte.
            if (_context.Version >= 0x14000004)
            {
                long mark = _stream.Position;
                int endian = _stream.ReadByte();
                _stream.Position = mark;

                if (endian >= 0)
                    _bigEndian = endian == 0;
            }

            // NeoSteam again.
            if (version == 0x08F35232)
                version = 0x0A010000;

            value.SetCount(version);
            return true;
        }

        // --- primitives -------------------------------------------------------

        private bool TryReadByte(out byte value)
        {
            int b = _stream.ReadByte();
            value = (byte)b;
            return b >= 0;
        }

        private bool TryReadUInt16(out ushort value)
        {
            if (!TryFill(2))
            {
                value = 0;
                return false;
            }

            value = _bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(_buffer)
                : BinaryPrimitives.ReadUInt16LittleEndian(_buffer);
            return true;
        }

        private bool TryReadUInt32(out uint value)
        {
            if (!TryFill(4))
            {
                value = 0;
                return false;
            }

            value = _bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(_buffer)
                : BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
            return true;
        }

        private bool TryReadUInt64(out ulong value)
        {
            if (!TryFill(8))
            {
                value = 0;
                return false;
            }

            value = _bigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(_buffer)
                : BinaryPrimitives.ReadUInt64LittleEndian(_buffer);
            return true;
        }

        private bool TryReadSingle(out float value)
        {
            if (!TryReadUInt32(out uint bits))
            {
                value = 0f;
                return false;
            }

            value = BitConverter.UInt32BitsToSingle(bits);
            return true;
        }

        private bool TryFill(int count) => TryReadExact(_buffer.AsSpan(0, count));

        private bool TryReadExact(Span<byte> destination)
        {
            int offset = 0;

            while (offset < destination.Length)
            {
                int read = _stream.Read(destination[offset..]);

                if (read <= 0)
                    return false;

                offset += read;
            }

            return true;
        }
    }
}
