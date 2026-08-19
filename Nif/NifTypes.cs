using System.Numerics;
using System.Runtime.InteropServices;

namespace SECmd.Nif
{
    /// <summary>
    /// The compound value types that nif.xml refers to by name and that
    /// <see cref="NifIStream"/> reads as a fixed number of bytes.
    /// </summary>
    /// <remarks>
    /// Layouts mirror NifSkope's niftypes.h exactly, including field order, because
    /// the whole point of these types is to be a faithful picture of the bytes on
    /// disk. Where NIF disagrees with <see cref="System.Numerics"/> — most notably
    /// <see cref="NifQuat"/>, which is stored w-first — the NIF order wins here and
    /// conversion helpers bridge the gap.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct NifVector2(float x, float y)
    {
        public float X = x;
        public float Y = y;

        public readonly Vector2 ToNumerics() => new(X, Y);
        public override readonly string ToString() => $"({X:G6}, {Y:G6})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NifVector3(float x, float y, float z)
    {
        public float X = x;
        public float Y = y;
        public float Z = z;

        public readonly Vector3 ToNumerics() => new(X, Y, Z);
        public static NifVector3 FromNumerics(Vector3 v) => new(v.X, v.Y, v.Z);
        public override readonly string ToString() => $"({X:G6}, {Y:G6}, {Z:G6})";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NifVector4(float x, float y, float z, float w)
    {
        public float X = x;
        public float Y = y;
        public float Z = z;
        public float W = w;

        public readonly Vector4 ToNumerics() => new(X, Y, Z, W);
        public override readonly string ToString() => $"({X:G6}, {Y:G6}, {Z:G6}, {W:G6})";
    }

    /// <summary>A quaternion stored in NIF's native w, x, y, z order.</summary>
    /// <remarks>
    /// Note the ordering: nif.xml's <c>Quaternion</c> is w-first, while its
    /// <c>hkQuaternion</c> (tQuatXYZW) is w-last. Both land in this struct; only the
    /// read/write order in <see cref="NifIStream"/> differs.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct NifQuat(float w, float x, float y, float z)
    {
        public float W = w;
        public float X = x;
        public float Y = y;
        public float Z = z;

        public static NifQuat Identity => new(1f, 0f, 0f, 0f);

        public readonly Quaternion ToNumerics() => new(X, Y, Z, W);
        public static NifQuat FromNumerics(Quaternion q) => new(q.W, q.X, q.Y, q.Z);
        public override readonly string ToString() => $"({W:G6}, {X:G6}, {Y:G6}, {Z:G6})";
    }

    /// <summary>A 3x3 rotation matrix, stored row-major as nine floats.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NifMatrix33
    {
        public float M11, M12, M13;
        public float M21, M22, M23;
        public float M31, M32, M33;

        public static NifMatrix33 Identity => new()
        {
            M11 = 1f,
            M22 = 1f,
            M33 = 1f
        };

        public override readonly string ToString() =>
            $"[{M11:G6} {M12:G6} {M13:G6}; {M21:G6} {M22:G6} {M23:G6}; {M31:G6} {M32:G6} {M33:G6}]";
    }

    /// <summary>A 4x4 matrix, stored row-major as sixteen floats.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NifMatrix44
    {
        public float M11, M12, M13, M14;
        public float M21, M22, M23, M24;
        public float M31, M32, M33, M34;
        public float M41, M42, M43, M44;

        public static NifMatrix44 Identity => new()
        {
            M11 = 1f,
            M22 = 1f,
            M33 = 1f,
            M44 = 1f
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NifColor3(float r, float g, float b)
    {
        public float R = r;
        public float G = g;
        public float B = b;

        public override readonly string ToString() => $"#{R:G6} {G:G6} {B:G6}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NifColor4(float r, float g, float b, float a)
    {
        public float R = r;
        public float G = g;
        public float B = b;
        public float A = a;

        public override readonly string ToString() => $"#{R:G6} {G:G6} {B:G6} {A:G6}";
    }

    /// <summary>A triangle as three vertex indices.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NifTriangle(ushort v1, ushort v2, ushort v3)
    {
        public ushort V1 = v1;
        public ushort V2 = v2;
        public ushort V3 = v3;

        public override readonly string ToString() => $"[{V1} {V2} {V3}]";
    }

    /// <summary>Which attributes a Bethesda vertex carries, as a bit per attribute.</summary>
    [Flags]
    public enum VertexFlags : ushort
    {
        None = 0,
        Vertex = 1 << VertexAttribute.Position,
        UV = 1 << VertexAttribute.TexCoord0,
        UV2 = 1 << VertexAttribute.TexCoord1,
        Normal = 1 << VertexAttribute.Normal,
        Tangent = 1 << VertexAttribute.Binormal,
        Colors = 1 << VertexAttribute.Color,
        Skinned = 1 << VertexAttribute.Skinning,
        LandData = 1 << VertexAttribute.LandData,
        EyeData = 1 << VertexAttribute.EyeData,
        FullPrecision = 0x400
    }

    /// <summary>Attribute slots within a Bethesda vertex description.</summary>
    public static class VertexAttribute
    {
        public const int Position = 0;
        public const int TexCoord0 = 1;
        public const int TexCoord1 = 2;
        public const int Normal = 3;
        public const int Binormal = 4;
        public const int Color = 5;
        public const int Skinning = 6;
        public const int LandData = 7;
        public const int EyeData = 8;
    }

    /// <summary>
    /// Bethesda's packed vertex layout descriptor (BSVertexDesc), a single uint64
    /// holding per-stream nibble offsets in the low bits and attribute flags at bit 44.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BSVertexDesc(ulong value)
    {
        /// <summary>
        /// Bit positions of each member, exactly as nif.xml's
        /// <c>&lt;bitfield name="BSVertexDesc"&gt;</c> declares them.
        /// </summary>
        /// <remarks>
        /// Every member is four bits holding a value divided by four, except the
        /// attribute flags, which are twelve bits at the top. Note there is no
        /// offset for the position: it is always first in a vertex.
        /// </remarks>
        public static class Member
        {
            public const int VertexDataSize = 0;
            public const int DynamicVertexSize = 4;
            public const int UV1Offset = 8;
            public const int UV2Offset = 12;
            public const int NormalOffset = 16;
            public const int TangentOffset = 20;
            public const int ColorOffset = 24;
            public const int SkinningDataOffset = 28;
            public const int LandscapeDataOffset = 32;
            public const int EyeDataOffset = 36;
            public const int Unused = 40;
            public const int VertexAttributes = 44;
        }

        public ulong Value = value;

        /// <summary>The attribute flags: twelve bits at position 44.</summary>
        public VertexFlags Flags
        {
            readonly get => (VertexFlags)((Value >> Member.VertexAttributes) & 0xFFF);
            set => Value = (Value & ~(0xFFFUL << Member.VertexAttributes))
                           | (((ulong)value & 0xFFF) << Member.VertexAttributes);
        }

        public readonly bool HasFlag(VertexFlags flag) => (Flags & flag) != 0;

        /// <summary>Reads a four-bit member, undoing the division by four.</summary>
        public readonly uint Get(int position) => (uint)((Value >> position) & 0xF) * 4;

        /// <summary>Writes a four-bit member, dividing by four.</summary>
        public void Set(int position, uint bytes) =>
            Value = (Value & ~(0xFUL << position)) | ((((ulong)bytes / 4) & 0xF) << position);

        /// <summary>Vertex stride in bytes.</summary>
        public uint VertexSize
        {
            readonly get => Get(Member.VertexDataSize);
            set => Set(Member.VertexDataSize, value);
        }

        /// <summary>
        /// Size of the separately stored dynamic vertex data, used by
        /// <c>BSDynamicTriShape</c>.
        /// </summary>
        public uint DynamicVertexSize
        {
            readonly get => Get(Member.DynamicVertexSize);
            set => Set(Member.DynamicVertexSize, value);
        }

        public readonly uint UVOffset => Get(Member.UV1Offset);

        public readonly uint UV2Offset => Get(Member.UV2Offset);

        public readonly uint NormalOffset => Get(Member.NormalOffset);

        public readonly uint TangentOffset => Get(Member.TangentOffset);

        public readonly uint ColorOffset => Get(Member.ColorOffset);

        public readonly uint SkinningOffset => Get(Member.SkinningDataOffset);

        public readonly uint LandscapeDataOffset => Get(Member.LandscapeDataOffset);

        public readonly uint EyeDataOffset => Get(Member.EyeDataOffset);

        public override readonly string ToString() => $"0x{Value:X16} ({Flags}, stride {VertexSize})";
    }

    /// <summary>
    /// A two-dimensional byte array, stored on disk as two int32 dimensions followed
    /// by width * height bytes.
    /// </summary>
    public sealed class ByteMatrix(int width, int height)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Data { get; } = new byte[(long)width * height <= int.MaxValue ? width * height : 0];

        public override string ToString() => $"{Width} x {Height} bytes";
    }

    /// <summary>
    /// Conversions for the half-precision and normalised-byte encodings that the
    /// Bethesda vertex formats use.
    /// </summary>
    internal static class NifPack
    {
        /// <summary>
        /// Decodes a 16-bit half into a float.
        /// </summary>
        /// <remarks>
        /// A NaN's payload is carried across by hand, because the numeric conversion
        /// does not keep it: <c>(float)(Half)</c> and back turns a half of
        /// <c>0x7F7D</c> into <c>0x7F7F</c>. A NIF is a file and may hold any bit
        /// pattern, including a NaN some exporter left in a vertex, so a value that
        /// was not touched has to be written back exactly as it was found.
        /// </remarks>
        public static float HalfToFloat(ushort half)
        {
            const ushort ExponentMask = 0x7C00;
            const ushort MantissaMask = 0x03FF;

            if ((half & ExponentMask) != ExponentMask || (half & MantissaMask) == 0)
                return (float)BitConverter.UInt16BitsToHalf(half);

            // Sign, all-ones exponent, and the ten payload bits where a float keeps
            // its own top ten -- which is where FloatToHalf looks for them.
            uint bits = (uint)(half & 0x8000) << 16
                        | 0x7F800000u
                        | (uint)(half & MantissaMask) << 13;

            return BitConverter.UInt32BitsToSingle(bits);
        }

        /// <summary>Encodes a float as a 16-bit half.</summary>
        /// <remarks>The reverse of <see cref="HalfToFloat"/>, payload and all.</remarks>
        public static ushort FloatToHalf(float value)
        {
            const uint ExponentMask = 0x7F800000;
            const uint MantissaMask = 0x007FFFFF;

            uint bits = BitConverter.SingleToUInt32Bits(value);

            if ((bits & ExponentMask) != ExponentMask || (bits & MantissaMask) == 0)
                return BitConverter.HalfToUInt16Bits((Half)value);

            ushort payload = (ushort)(bits >> 13 & 0x03FF);

            // A float NaN whose top ten mantissa bits are all zero would come out as
            // an infinity, which is a different number rather than a lossy one.
            if (payload == 0)
                payload = 0x0200;

            return (ushort)((bits >> 16 & 0x8000) | 0x7C00 | payload);
        }

        /// <summary>Expands a byte to the -1..1 range, as NifSkope's tNormbyte does.</summary>
        public static float ByteToSNorm(byte value) => (float)(value / 255.0 * 2.0 - 1.0);

        /// <summary>Packs a -1..1 float back into a byte.</summary>
        public static byte SNormToByte(float value)
        {
            double scaled = Math.Round((value + 1.0) / 2.0 * 255.0);
            return (byte)Math.Clamp(scaled, 0.0, 255.0);
        }
    }
}
