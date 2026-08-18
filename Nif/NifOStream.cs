using System.Buffers.Binary;
using System.Text;

namespace SECmd.Nif
{
    /// <summary>
    /// Writes a single <see cref="NifValue"/> back to a stream, mirroring
    /// <see cref="NifIStream"/>.
    /// </summary>
    /// <remarks>
    /// A port of NifSkope's NifOStream. Every case here is the exact inverse of the
    /// corresponding case in the reader, which is what makes a load/save round trip
    /// byte-identical. Where the two are *not* symmetric it is because the format
    /// itself is not, and those places are commented.
    /// </remarks>
    public sealed class NifOStream
    {
        private static readonly Encoding StringEncoding = Encoding.Latin1;

        private readonly Stream _stream;
        private readonly INifStreamContext _context;
        private readonly byte[] _buffer = new byte[64];

        private bool _bool32Bit;
        private bool _linkAdjust;
        private bool _stringAdjust;
        private bool _bigEndian;

        /// <summary>
        /// The banner the file was loaded with. NeoSteam files carry a sentinel in
        /// place of their version, and it has to be put back on save.
        /// </summary>
        public string HeaderString { get; set; } = string.Empty;

        public NifOStream(INifStreamContext context, Stream stream)
        {
            _context = context;
            _stream = stream;
            Initialise();
        }

        public void Initialise()
        {
            uint version = _context.Version;

            _bool32Bit = version <= 0x04000002;
            _linkAdjust = version < 0x0303000D;
            _stringAdjust = version >= 0x14010003;
        }

        /// <summary>Matches the byte order the file was read with.</summary>
        public void SetBigEndian(bool bigEndian) => _bigEndian = bigEndian;

        public bool Write(in NifValue value)
        {
            switch (value.Type)
            {
                case NifValueType.Bool:
                    if (_bool32Bit)
                        WriteUInt32(value.ToUInt());
                    else
                        WriteByte((byte)value.ToUInt());

                    return true;

                case NifValueType.Byte:
                    WriteByte((byte)value.ToUInt());
                    return true;

                case NifValueType.Word:
                case NifValueType.Short:
                case NifValueType.Flags:
                case NifValueType.BlockTypeIndex:
                    WriteUInt16((ushort)value.ToUInt());
                    return true;

                case NifValueType.StringOffset:
                case NifValueType.Int:
                case NifValueType.UInt:
                case NifValueType.StringIndex:
                    WriteUInt32(value.ToUInt());
                    return true;

                case NifValueType.ULittle32:
                    // Explicitly little-endian regardless of the file's byte order.
                    BinaryPrimitives.WriteUInt32LittleEndian(_buffer, value.ToUInt());
                    _stream.Write(_buffer, 0, 4);
                    return true;

                case NifValueType.Int64:
                case NifValueType.UInt64:
                    WriteUInt64(value.ToUInt64());
                    return true;

                case NifValueType.BSVertexDesc:
                    WriteUInt64(value.Get<BSVertexDesc>().Value);
                    return true;

                case NifValueType.Link:
                case NifValueType.UpLink:
                {
                    int link = value.ToLink();

                    if (_linkAdjust)
                        link++;

                    WriteUInt32(unchecked((uint)link));
                    return true;
                }

                case NifValueType.Float:
                    WriteUInt32(BitConverter.SingleToUInt32Bits(value.ToFloat()));
                    return true;

                case NifValueType.Hfloat:
                    WriteUInt16(NifPack.FloatToHalf(value.ToFloat()));
                    return true;

                case NifValueType.Normbyte:
                    WriteByte(NifPack.SNormToByte(value.ToFloat()));
                    return true;

                case NifValueType.ByteVector3:
                {
                    var v = value.Get<NifVector3>();
                    _buffer[0] = NifPack.SNormToByte(v.X);
                    _buffer[1] = NifPack.SNormToByte(v.Y);
                    _buffer[2] = NifPack.SNormToByte(v.Z);
                    _stream.Write(_buffer, 0, 3);
                    return true;
                }

                case NifValueType.UshortVector3:
                {
                    var v = value.Get<NifVector3>();
                    WriteUInt16((ushort)MathF.Round(v.X));
                    WriteUInt16((ushort)MathF.Round(v.Y));
                    WriteUInt16((ushort)MathF.Round(v.Z));
                    return true;
                }

                case NifValueType.HalfVector3:
                {
                    var v = value.Get<NifVector3>();
                    WriteUInt16(NifPack.FloatToHalf(v.X));
                    WriteUInt16(NifPack.FloatToHalf(v.Y));
                    WriteUInt16(NifPack.FloatToHalf(v.Z));
                    return true;
                }

                case NifValueType.HalfVector2:
                {
                    var v = value.Get<NifVector2>();
                    WriteUInt16(NifPack.FloatToHalf(v.X));
                    WriteUInt16(NifPack.FloatToHalf(v.Y));
                    return true;
                }

                case NifValueType.Vector2:
                {
                    var v = value.Get<NifVector2>();
                    WriteSingle(v.X);
                    WriteSingle(v.Y);
                    return true;
                }

                case NifValueType.Vector3:
                {
                    var v = value.Get<NifVector3>();
                    WriteSingle(v.X);
                    WriteSingle(v.Y);
                    WriteSingle(v.Z);
                    return true;
                }

                case NifValueType.Vector4:
                {
                    var v = value.Get<NifVector4>();
                    WriteSingle(v.X);
                    WriteSingle(v.Y);
                    WriteSingle(v.Z);
                    WriteSingle(v.W);
                    return true;
                }

                case NifValueType.Quat:
                {
                    var q = value.Get<NifQuat>();
                    WriteSingle(q.W);
                    WriteSingle(q.X);
                    WriteSingle(q.Y);
                    WriteSingle(q.Z);
                    return true;
                }

                case NifValueType.QuatXYZW:
                {
                    var q = value.Get<NifQuat>();
                    WriteSingle(q.X);
                    WriteSingle(q.Y);
                    WriteSingle(q.Z);
                    WriteSingle(q.W);
                    return true;
                }

                case NifValueType.Matrix:
                {
                    var m = value.Get<NifMatrix33>();
                    WriteSingle(m.M11);
                    WriteSingle(m.M12);
                    WriteSingle(m.M13);
                    WriteSingle(m.M21);
                    WriteSingle(m.M22);
                    WriteSingle(m.M23);
                    WriteSingle(m.M31);
                    WriteSingle(m.M32);
                    WriteSingle(m.M33);
                    return true;
                }

                case NifValueType.Matrix4:
                {
                    var m = value.Get<NifMatrix44>();
                    WriteSingle(m.M11);
                    WriteSingle(m.M12);
                    WriteSingle(m.M13);
                    WriteSingle(m.M14);
                    WriteSingle(m.M21);
                    WriteSingle(m.M22);
                    WriteSingle(m.M23);
                    WriteSingle(m.M24);
                    WriteSingle(m.M31);
                    WriteSingle(m.M32);
                    WriteSingle(m.M33);
                    WriteSingle(m.M34);
                    WriteSingle(m.M41);
                    WriteSingle(m.M42);
                    WriteSingle(m.M43);
                    WriteSingle(m.M44);
                    return true;
                }

                case NifValueType.Color3:
                {
                    var c = value.Get<NifColor3>();
                    WriteSingle(c.R);
                    WriteSingle(c.G);
                    WriteSingle(c.B);
                    return true;
                }

                case NifValueType.Color4:
                {
                    var c = value.Get<NifColor4>();
                    WriteSingle(c.R);
                    WriteSingle(c.G);
                    WriteSingle(c.B);
                    WriteSingle(c.A);
                    return true;
                }

                case NifValueType.ByteColor4:
                {
                    var c = value.Get<NifColor4>();
                    _buffer[0] = ToColorByte(c.R);
                    _buffer[1] = ToColorByte(c.G);
                    _buffer[2] = ToColorByte(c.B);
                    _buffer[3] = ToColorByte(c.A);
                    _stream.Write(_buffer, 0, 4);
                    return true;
                }

                case NifValueType.Triangle:
                {
                    var t = value.Get<NifTriangle>();
                    WriteUInt16(t.V1);
                    WriteUInt16(t.V2);
                    WriteUInt16(t.V3);
                    return true;
                }

                case NifValueType.SizedString:
                case NifValueType.Text:
                    WriteSizedString(value.AsString());
                    return true;

                case NifValueType.ShortString:
                {
                    byte[] bytes = StringEncoding.GetBytes(value.AsString());

                    // The stored length counts a NUL terminator, and the field is a
                    // single byte, so the text itself can be at most 254 bytes.
                    if (bytes.Length > 254)
                        bytes = bytes[..254];

                    WriteByte((byte)(bytes.Length + 1));
                    _stream.Write(bytes, 0, bytes.Length);
                    WriteByte(0);
                    return true;
                }

                case NifValueType.String:
                case NifValueType.FilePath:
                    if (_stringAdjust)
                    {
                        uint index = value.ToUInt();

                        // Anything above the plausible range of a string-table index
                        // is treated as garbage and written as 0, as NifSkope does.
                        WriteUInt32(index < 0x00010000 ? index : 0u);
                    }
                    else
                    {
                        WriteSizedString(value.AsString());
                    }

                    return true;

                case NifValueType.HeaderString:
                case NifValueType.LineString:
                {
                    byte[] bytes = StringEncoding.GetBytes(value.AsString());
                    _stream.Write(bytes, 0, bytes.Length);
                    WriteByte((byte)'\n');
                    return true;
                }

                case NifValueType.Char8String:
                {
                    byte[] bytes = StringEncoding.GetBytes(value.AsString());
                    int n = Math.Min(8, bytes.Length);

                    _stream.Write(bytes, 0, n);

                    for (int i = n; i < 8; i++)
                        WriteByte(0);

                    return true;
                }

                case NifValueType.FileVersion:
                {
                    // NeoSteam stores a sentinel where the version belongs.
                    uint version = HeaderString.StartsWith("NS", StringComparison.Ordinal)
                        ? 0x08F35232
                        : value.ToUInt();

                    BinaryPrimitives.WriteUInt32LittleEndian(_buffer, version);
                    _stream.Write(_buffer, 0, 4);
                    return true;
                }

                case NifValueType.ByteArray:
                {
                    byte[] bytes = value.AsByteArray();
                    WriteUInt32((uint)bytes.Length);
                    _stream.Write(bytes, 0, bytes.Length);
                    return true;
                }

                case NifValueType.StringPalette:
                {
                    byte[] bytes = value.AsByteArray();
                    WriteUInt32((uint)bytes.Length);
                    _stream.Write(bytes, 0, bytes.Length);

                    // The length is stored a second time after the data.
                    WriteUInt32((uint)bytes.Length);
                    return true;
                }

                case NifValueType.ByteMatrix:
                {
                    var matrix = value.Get<ByteMatrix>();

                    if (matrix is null)
                        return false;

                    WriteUInt32((uint)matrix.Width);
                    WriteUInt32((uint)matrix.Height);
                    _stream.Write(matrix.Data, 0, matrix.Data.Length);
                    return true;
                }

                case NifValueType.Blob:
                {
                    byte[] blob = value.AsByteArray();
                    _stream.Write(blob, 0, blob.Length);
                    return true;
                }

                case NifValueType.None:
                    return true;

                default:
                    throw new NifFormatException($"no write rule for value type {value.Type}");
            }
        }

        /// <summary>
        /// The number of bytes <see cref="Write"/> would emit for a value, used to
        /// fill in the block sizes the header carries from 20.2.0.0 onward.
        /// </summary>
        public int SizeOf(in NifValue value)
        {
            switch (value.Type)
            {
                case NifValueType.Bool:
                    return _bool32Bit ? 4 : 1;

                case NifValueType.Byte:
                case NifValueType.Normbyte:
                    return 1;

                case NifValueType.Word:
                case NifValueType.Short:
                case NifValueType.Flags:
                case NifValueType.BlockTypeIndex:
                case NifValueType.Hfloat:
                    return 2;

                case NifValueType.ByteVector3:
                    return 3;

                case NifValueType.StringOffset:
                case NifValueType.Int:
                case NifValueType.UInt:
                case NifValueType.StringIndex:
                case NifValueType.ULittle32:
                case NifValueType.Link:
                case NifValueType.UpLink:
                case NifValueType.Float:
                case NifValueType.FileVersion:
                case NifValueType.ByteColor4:
                case NifValueType.HalfVector2:
                    return 4;

                case NifValueType.UshortVector3:
                case NifValueType.HalfVector3:
                case NifValueType.Triangle:
                    return 6;

                case NifValueType.Int64:
                case NifValueType.UInt64:
                case NifValueType.BSVertexDesc:
                case NifValueType.Vector2:
                    return 8;

                case NifValueType.Vector3:
                case NifValueType.Color3:
                    return 12;

                case NifValueType.Vector4:
                case NifValueType.Color4:
                case NifValueType.Quat:
                case NifValueType.QuatXYZW:
                    return 16;

                case NifValueType.Matrix:
                    return 36;

                case NifValueType.Matrix4:
                    return 64;

                case NifValueType.Char8String:
                    return 8;

                case NifValueType.SizedString:
                case NifValueType.Text:
                    return 4 + StringEncoding.GetByteCount(value.AsString());

                case NifValueType.ShortString:
                    return 1 + Math.Min(254, StringEncoding.GetByteCount(value.AsString())) + 1;

                case NifValueType.String:
                case NifValueType.FilePath:
                    return _stringAdjust ? 4 : 4 + StringEncoding.GetByteCount(value.AsString());

                case NifValueType.HeaderString:
                case NifValueType.LineString:
                    return StringEncoding.GetByteCount(value.AsString()) + 1;

                case NifValueType.ByteArray:
                    return 4 + value.AsByteArray().Length;

                case NifValueType.StringPalette:
                    return 8 + value.AsByteArray().Length;

                case NifValueType.ByteMatrix:
                    return 8 + (value.Get<ByteMatrix>()?.Data.Length ?? 0);

                case NifValueType.Blob:
                    return value.AsByteArray().Length;

                case NifValueType.None:
                    return 0;

                default:
                    throw new NifFormatException($"no size rule for value type {value.Type}");
            }
        }

        // --- primitives -------------------------------------------------------

        private static byte ToColorByte(float value) =>
            (byte)Math.Clamp(MathF.Round(value * 255f), 0f, 255f);

        private void WriteSizedString(string s)
        {
            byte[] bytes = StringEncoding.GetBytes(s);
            WriteUInt32((uint)bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }

        private void WriteByte(byte value) => _stream.WriteByte(value);

        private void WriteUInt16(ushort value)
        {
            if (_bigEndian)
                BinaryPrimitives.WriteUInt16BigEndian(_buffer, value);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(_buffer, value);

            _stream.Write(_buffer, 0, 2);
        }

        private void WriteUInt32(uint value)
        {
            if (_bigEndian)
                BinaryPrimitives.WriteUInt32BigEndian(_buffer, value);
            else
                BinaryPrimitives.WriteUInt32LittleEndian(_buffer, value);

            _stream.Write(_buffer, 0, 4);
        }

        private void WriteUInt64(ulong value)
        {
            if (_bigEndian)
                BinaryPrimitives.WriteUInt64BigEndian(_buffer, value);
            else
                BinaryPrimitives.WriteUInt64LittleEndian(_buffer, value);

            _stream.Write(_buffer, 0, 8);
        }

        private void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));
    }
}
