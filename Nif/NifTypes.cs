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
        private const ulong MaskVert = 0xFFFFFFFFFFFFFFF0;
        private const ulong MaskUVs = 0xFFFFFFFFFFFFFF0F;
        private const ulong MaskNBT = 0xFFFFFFFFFFFFF0FF;
        private const ulong MaskSkCol = 0xFFFFFFFFFFFF0FFF;
        private const ulong MaskData = 0xFFFFFFFFFFF0FFFF;
        private const ulong MaskOffset = 0xFFFFFF0000000000;

        public ulong Value = value;

        /// <summary>The attribute flags, which live in the nibbles above bit 44.</summary>
        public VertexFlags Flags
        {
            readonly get => (VertexFlags)((Value >> 44) & 0xFFFF);
            set => Value = (Value & MaskOffset & ~(0xFFFFUL << 44)) | ((ulong)value << 44);
        }

        public readonly bool HasFlag(VertexFlags flag) => (Flags & flag) != 0;

        /// <summary>Vertex stride in bytes; stored divided by four in the low nibble.</summary>
        public uint VertexSize
        {
            readonly get => (uint)((Value & 0xF) * 4);
            set => Value = (Value & MaskVert) | ((ulong)value >> 2);
        }

        /// <summary>Total size of the dynamic vertex data, in bytes.</summary>
        public uint DataSize
        {
            readonly get => (uint)(((Value >> 16) & 0xF) * 4);
            set => Value = (Value & MaskData) | (((ulong)value >> 2) << 16);
        }

        public uint UVOffset
        {
            readonly get => (uint)(((Value >> 4) & 0xF) * 4);
            set => Value = (Value & MaskUVs) | (((ulong)value >> 2) << 4);
        }

        public uint NormalOffset
        {
            readonly get => (uint)(((Value >> 8) & 0xF) * 4);
            set => Value = (Value & MaskNBT) | (((ulong)value >> 2) << 8);
        }

        public uint ColorOffset
        {
            readonly get => (uint)(((Value >> 12) & 0xF) * 4);
            set => Value = (Value & MaskSkCol) | (((ulong)value >> 2) << 12);
        }

        public override readonly string ToString() => $"0x{Value:X16} ({Flags})";
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
        /// <summary>Decodes a 16-bit half into a float.</summary>
        public static float HalfToFloat(ushort half) => (float)BitConverter.UInt16BitsToHalf(half);

        /// <summary>Encodes a float as a 16-bit half.</summary>
        public static ushort FloatToHalf(float value) => BitConverter.HalfToUInt16Bits((Half)value);

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
