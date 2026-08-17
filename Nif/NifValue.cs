namespace SECmd.Nif
{
    /// <summary>
    /// Every storage type the NIF reader knows how to turn into bytes.
    /// </summary>
    /// <remarks>
    /// Mirrors NifSkope's NifValue::Type. Two orderings are load-bearing and must be
    /// preserved: all count-like types sit between <see cref="Bool"/> and
    /// <see cref="UInt"/>, and all string-like types sit between
    /// <see cref="SizedString"/> and <see cref="Char8String"/>. <see cref="IsCount"/>
    /// and <see cref="IsString"/> are range checks over those spans.
    /// </remarks>
    public enum NifValueType : byte
    {
        // --- count types: keep contiguous, Bool first ---
        Bool = 0,
        Byte,
        Word,
        Flags,
        StringOffset,
        StringIndex,
        BlockTypeIndex,
        Int,
        Short,
        ULittle32,
        Int64,
        UInt64,
        UInt,
        // --- end count types ---

        Link,
        UpLink,
        Float,

        // --- string types: keep contiguous, SizedString first ---
        SizedString,
        Text,
        ShortString,
        HeaderString,
        LineString,
        Char8String,
        // --- end string types ---

        Color3,
        Color4,
        Vector3,
        Quat,
        QuatXYZW,
        Matrix,
        Matrix4,
        Vector2,
        Vector4,
        Triangle,
        FileVersion,
        ByteArray,
        StringPalette,

        /// <summary>Not a plain string: an index into the header string table on 20.1.0.3+.</summary>
        String,

        /// <summary>Not a plain string: needs slash/backslash normalisation.</summary>
        FilePath,

        ByteMatrix,
        Blob,
        Hfloat,
        HalfVector3,
        UshortVector3,
        ByteVector3,
        HalfVector2,
        ByteColor4,
        BSVertexDesc,
        Normbyte,

        None = 0xFF
    }

    /// <summary>How nif.xml wants an integer field presented.</summary>
    public enum NifEnumType : byte
    {
        /// <summary>Not an enum.</summary>
        None = 0,
        /// <summary>A plain enumeration.</summary>
        Default,
        /// <summary>A bitflag enumeration.</summary>
        Flags
    }

    /// <summary>
    /// A single typed value read from or written to a NIF.
    /// </summary>
    /// <remarks>
    /// Scalars live in <see cref="_num"/> without allocating; compound types
    /// (vectors, matrices, strings, blobs) are boxed into <see cref="_obj"/>. That
    /// split matches NifSkope's union-plus-pointer layout, and keeps the common case
    /// — the counts, links and floats that dominate a NIF — allocation free.
    /// </remarks>
    public struct NifValue
    {
        private ulong _num;
        private object? _obj;

        public NifValueType Type { get; private set; }

        public NifValue()
        {
            Type = NifValueType.None;
        }

        public NifValue(NifValueType type)
        {
            Type = NifValueType.None;
            ChangeType(type);
        }

        /// <summary>True for the integer-like types that can be read as a count.</summary>
        public readonly bool IsCount => Type >= NifValueType.Bool && Type <= NifValueType.UInt;

        /// <summary>True for the types backed by a <see cref="string"/>.</summary>
        public readonly bool IsString =>
            (Type >= NifValueType.SizedString && Type <= NifValueType.Char8String)
            || Type == NifValueType.String
            || Type == NifValueType.FilePath;

        /// <summary>True for the two block-reference types.</summary>
        public readonly bool IsLink => Type is NifValueType.Link or NifValueType.UpLink;

        public readonly bool IsFloat => Type is NifValueType.Float or NifValueType.Hfloat or NifValueType.Normbyte;

        /// <summary>
        /// Switches the value to a different type, resetting it to that type's
        /// default. A no-op when the type already matches, matching NifSkope.
        /// </summary>
        public void ChangeType(NifValueType type)
        {
            if (Type == type)
                return;

            Type = type;
            _num = 0;
            _obj = type switch
            {
                NifValueType.Vector2 or NifValueType.HalfVector2 => new NifVector2(),
                NifValueType.Vector3 or NifValueType.HalfVector3
                    or NifValueType.UshortVector3 or NifValueType.ByteVector3 => new NifVector3(),
                NifValueType.Vector4 => new NifVector4(),
                NifValueType.Quat or NifValueType.QuatXYZW => NifQuat.Identity,
                NifValueType.Matrix => NifMatrix33.Identity,
                NifValueType.Matrix4 => NifMatrix44.Identity,
                NifValueType.Color3 => new NifColor3(),
                NifValueType.Color4 or NifValueType.ByteColor4 => new NifColor4(),
                NifValueType.Triangle => new NifTriangle(),
                NifValueType.BSVertexDesc => new BSVertexDesc(),
                NifValueType.ByteArray or NifValueType.StringPalette or NifValueType.Blob => Array.Empty<byte>(),
                _ when IsString => string.Empty,
                _ => null
            };
        }

        public void Clear()
        {
            Type = NifValueType.None;
            _num = 0;
            _obj = null;
        }

        // --- scalar access -------------------------------------------------
        // Reads and writes go through the raw bits so that, for example, a value
        // read as tShort can still be inspected as an int without a second copy.

        internal ulong RawBits
        {
            readonly get => _num;
            set => _num = value;
        }

        internal object? RawObject
        {
            readonly get => _obj;
            set => _obj = value;
        }

        public readonly bool ToBool() => _num != 0;

        public readonly int ToInt() => Type switch
        {
            NifValueType.Float => (int)BitConverter.UInt32BitsToSingle((uint)_num),
            NifValueType.Short => (short)(ushort)_num,
            NifValueType.Byte => (byte)_num,
            NifValueType.Word or NifValueType.Flags or NifValueType.BlockTypeIndex => (ushort)_num,
            _ => unchecked((int)(uint)_num)
        };

        public readonly uint ToUInt() => Type switch
        {
            NifValueType.Float => (uint)BitConverter.UInt32BitsToSingle((uint)_num),
            _ => (uint)_num
        };

        public readonly long ToInt64() => unchecked((long)_num);

        public readonly ulong ToUInt64() => _num;

        public readonly float ToFloat() => Type switch
        {
            NifValueType.Float or NifValueType.Hfloat or NifValueType.Normbyte
                => BitConverter.UInt32BitsToSingle((uint)_num),
            _ => ToInt()
        };

        /// <summary>The referenced block index, or -1 when the link is null.</summary>
        public readonly int ToLink() => IsLink ? unchecked((int)(uint)_num) : -1;

        public void SetCount(ulong value)
        {
            _num = value;
        }

        public void SetFloat(float value)
        {
            _num = BitConverter.SingleToUInt32Bits(value);
        }

        public void SetLink(int value)
        {
            _num = unchecked((uint)value);
        }

        // --- compound access -----------------------------------------------

        /// <summary>
        /// Reads the boxed payload as <typeparamref name="T"/>, or the type's default
        /// when this value holds something else.
        /// </summary>
        public readonly T? Get<T>() => _obj is T typed ? typed : default;

        public void Set<T>(T value) where T : notnull
        {
            _obj = value;
        }

        public readonly string AsString() => _obj as string ?? string.Empty;

        public readonly byte[] AsByteArray() => _obj as byte[] ?? [];

        public override readonly string ToString() => Type switch
        {
            NifValueType.None => "<none>",
            _ when IsString => AsString(),
            _ when IsLink => ToLink() < 0 ? "None" : $"{ToLink()}",
            NifValueType.Float or NifValueType.Hfloat or NifValueType.Normbyte => ToFloat().ToString("G6"),
            NifValueType.Int64 => ToInt64().ToString(),
            NifValueType.UInt64 => ToUInt64().ToString(),
            _ when IsCount => ToUInt().ToString(),
            _ => _obj?.ToString() ?? "<null>"
        };

        // --- nif.xml type-name mapping --------------------------------------

        private static readonly Dictionary<string, NifValueType> TypeMap = new(StringComparer.Ordinal)
        {
            ["bool"] = NifValueType.Bool,
            ["byte"] = NifValueType.Byte,
            ["sbyte"] = NifValueType.Byte,
            ["normbyte"] = NifValueType.Normbyte,
            ["char"] = NifValueType.Byte,
            ["word"] = NifValueType.Word,
            ["short"] = NifValueType.Short,
            ["int"] = NifValueType.Int,
            ["Flags"] = NifValueType.Flags,
            ["ushort"] = NifValueType.Word,
            ["uint"] = NifValueType.UInt,
            ["ulittle32"] = NifValueType.ULittle32,
            ["int64"] = NifValueType.Int64,
            ["uint64"] = NifValueType.UInt64,
            ["Ref"] = NifValueType.Link,
            ["Ptr"] = NifValueType.UpLink,
            ["float"] = NifValueType.Float,
            ["SizedString"] = NifValueType.SizedString,
            ["Text"] = NifValueType.Text,
            ["ExportString"] = NifValueType.ShortString,
            ["Color3"] = NifValueType.Color3,
            ["Color4"] = NifValueType.Color4,
            ["Vector4"] = NifValueType.Vector4,
            ["Vector3"] = NifValueType.Vector3,
            ["TBC"] = NifValueType.Vector3,
            ["Quaternion"] = NifValueType.Quat,
            ["QuaternionWXYZ"] = NifValueType.Quat,
            ["QuaternionXYZW"] = NifValueType.QuatXYZW,
            ["hkQuaternion"] = NifValueType.QuatXYZW,
            ["Matrix33"] = NifValueType.Matrix,
            ["Matrix44"] = NifValueType.Matrix4,
            ["Vector2"] = NifValueType.Vector2,
            ["TexCoord"] = NifValueType.Vector2,
            ["Triangle"] = NifValueType.Triangle,
            ["ByteArray"] = NifValueType.ByteArray,
            ["ByteMatrix"] = NifValueType.ByteMatrix,
            ["FileVersion"] = NifValueType.FileVersion,
            ["HeaderString"] = NifValueType.HeaderString,
            ["LineString"] = NifValueType.LineString,
            ["StringPalette"] = NifValueType.StringPalette,
            ["StringOffset"] = NifValueType.StringOffset,
            ["NiFixedString"] = NifValueType.StringIndex,
            ["BlockTypeIndex"] = NifValueType.BlockTypeIndex,
            ["char8string"] = NifValueType.Char8String,
            ["string"] = NifValueType.String,
            ["FilePath"] = NifValueType.FilePath,
            ["blob"] = NifValueType.Blob,
            ["hfloat"] = NifValueType.Hfloat,
            ["HalfVector3"] = NifValueType.HalfVector3,
            ["UshortVector3"] = NifValueType.UshortVector3,
            ["ByteVector3"] = NifValueType.ByteVector3,
            ["HalfVector2"] = NifValueType.HalfVector2,
            ["HalfTexCoord"] = NifValueType.HalfVector2,
            ["ByteColor4"] = NifValueType.ByteColor4
        };

        /// <summary>
        /// Resolves a nif.xml type name to a storage type, or
        /// <see cref="NifValueType.None"/> when the name is a compound declared in
        /// the XML rather than something the stream reads directly.
        /// </summary>
        public static NifValueType TypeFromName(string name) =>
            TypeMap.TryGetValue(name, out var type) ? type : NifValueType.None;

        /// <summary>Registers an additional name for an existing storage type.</summary>
        public static bool RegisterAlias(string alias, string original)
        {
            if (!TypeMap.TryGetValue(original, out var type))
                return false;

            TypeMap[alias] = type;
            return true;
        }

        /// <summary>True when the name maps to a type the stream reads directly.</summary>
        public static bool IsInternalType(string name) => TypeMap.ContainsKey(name);
    }
}
