namespace SECmd.Nif
{
    /// <summary>Properties of a field that the reader needs to branch on.</summary>
    [Flags]
    public enum NifFieldFlags
    {
        None = 0x0,
        /// <summary>Declared but never read; the reader skips it.</summary>
        Abstract = 0x1,
        /// <summary>Read as an opaque blob rather than as its declared structure.</summary>
        Binary = 0x2,
        /// <summary>The type or template is the placeholder <c>#T#</c>.</summary>
        Templated = 0x4,
        /// <summary>The type is a struct declared in the XML, so the field nests.</summary>
        Compound = 0x8,
        Array = 0x10,
        MultiArray = 0x20,
        /// <summary>No cond, vercond, since or until: always present.</summary>
        Conditionless = 0x40,
        /// <summary>A struct spliced into its parent instead of nesting.</summary>
        Mixin = 0x80,
        /// <summary>The condition is an <c>onlyT</c>/<c>excludeT</c> type test.</summary>
        TypeCondition = 0x100
    }

    /// <summary>
    /// One <c>&lt;field&gt;</c> from nif.xml: what to read, and the conditions under
    /// which it is present at all.
    /// </summary>
    public sealed class NifFieldDef
    {
        public required string Name { get; init; }

        /// <summary>The declared type name, which may be a built-in, a struct, or <c>#T#</c>.</summary>
        public required string Type { get; init; }

        /// <summary>The template argument for a templated type, if any.</summary>
        public string Template { get; init; } = string.Empty;

        /// <summary>The <c>arg</c> attribute, passed down to nested structs as <c>#ARG#</c>.</summary>
        public string Arg { get; init; } = string.Empty;

        /// <summary>The <c>length</c> attribute: the array length expression.</summary>
        public string Arr1 { get; init; } = string.Empty;

        /// <summary>The <c>width</c> attribute: the inner length of a two-dimensional array.</summary>
        public string Arr2 { get; init; } = string.Empty;

        /// <summary>The <c>cond</c> attribute, or the expansion of <c>onlyT</c>/<c>excludeT</c>.</summary>
        public string Cond { get; init; } = string.Empty;

        /// <summary>The <c>vercond</c> attribute.</summary>
        public string VerCond { get; init; } = string.Empty;

        /// <summary>The <c>since</c> attribute, packed. 0 means unbounded.</summary>
        public uint Ver1 { get; init; }

        /// <summary>The <c>until</c> attribute, packed. 0 means unbounded.</summary>
        public uint Ver2 { get; init; }

        public NifFieldFlags Flags { get; init; }

        /// <summary>The storage type, or None when <see cref="Type"/> names a struct.</summary>
        public NifValueType ValueType { get; init; }

        /// <summary>The raw <c>default</c> attribute, applied when the field is created.</summary>
        public string? Default { get; init; }

        /// <summary>Documentation text from the element body.</summary>
        public string Text { get; set; } = string.Empty;

        // Expressions are compiled once here rather than per item, since a single
        // field definition is shared by every instance of its block in the file.
        private NifExpr? _condExpr;
        private NifExpr? _verExpr;
        private NifExpr? _arr1Expr;
        private NifExpr? _argExpr;

        public NifExpr CondExpr => _condExpr ??= new NifExpr(Cond);
        public NifExpr VerExpr => _verExpr ??= new NifExpr(VerCond);
        public NifExpr Arr1Expr => _arr1Expr ??= new NifExpr(Arr1);
        public NifExpr ArgExpr => _argExpr ??= new NifExpr(Arg);

        public bool IsAbstract => (Flags & NifFieldFlags.Abstract) != 0;
        public bool IsBinary => (Flags & NifFieldFlags.Binary) != 0;
        public bool IsTemplated => (Flags & NifFieldFlags.Templated) != 0;
        public bool IsCompound => (Flags & NifFieldFlags.Compound) != 0;
        public bool IsArray => (Flags & NifFieldFlags.Array) != 0;
        public bool IsMultiArray => (Flags & NifFieldFlags.MultiArray) != 0;
        public bool IsConditionless => (Flags & NifFieldFlags.Conditionless) != 0;
        public bool IsMixin => (Flags & NifFieldFlags.Mixin) != 0;
        public bool HasTypeCondition => (Flags & NifFieldFlags.TypeCondition) != 0;

        public override string ToString() => $"{Name} : {Type}{(IsArray ? "[]" : string.Empty)}";
    }

    /// <summary>
    /// One <c>&lt;niobject&gt;</c> or <c>&lt;struct&gt;</c> from nif.xml.
    /// </summary>
    public sealed class NifBlockDef
    {
        public required string Id { get; init; }

        /// <summary>The <c>inherit</c> attribute; empty for a root block or a struct.</summary>
        public string Ancestor { get; set; } = string.Empty;

        /// <summary>Declared abstract, so it never appears as a block in a file.</summary>
        public bool Abstract { get; set; }

        public string Text { get; set; } = string.Empty;

        /// <summary>The block's own fields, not including inherited ones.</summary>
        public List<NifFieldDef> Fields { get; } = [];

        public override string ToString() =>
            Ancestor.Length > 0 ? $"{Id} : {Ancestor}" : Id;
    }

    /// <summary>A single named value of an <c>&lt;enum&gt;</c> or <c>&lt;bitflags&gt;</c>.</summary>
    public sealed record NifEnumOption(string Name, uint Value, string Text);
}
