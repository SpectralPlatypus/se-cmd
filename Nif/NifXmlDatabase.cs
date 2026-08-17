using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace SECmd.Nif
{
    /// <summary>
    /// nif.xml, parsed into the block and field descriptors that drive reading and
    /// writing.
    /// </summary>
    /// <remarks>
    /// This is a port of NifSkope's NifXmlHandler. Everything it decides — which
    /// names are storage types, which are structs, which structs get spliced into
    /// their parent, which blocks inherit from which — is decided here, once, so
    /// that the stream layer stays a plain type switch and the model layer stays a
    /// plain tree walk.
    ///
    /// Unlike NifSkope, the name-to-type map lives on the instance. NifSkope keeps
    /// it in a global that <c>NifValue::initialize()</c> has to reset; making it
    /// per-database means loading a second XML cannot leak aliases into the first.
    /// </remarks>
    public sealed class NifXmlDatabase
    {
        /// <summary>The placeholder nif.xml uses for a template parameter.</summary>
        public const string TemplatePlaceholder = "#T#";

        /// <summary>The placeholder nif.xml uses for an inherited argument.</summary>
        public const string ArgPlaceholder = "#ARG#";

        private readonly Dictionary<string, NifValueType> _typeMap = NifValue.CreateTypeMap();
        private readonly Dictionary<string, NifBlockDef> _compounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NifBlockDef> _fixedCompounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NifBlockDef> _blocks = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, NifBlockDef> _blockHashes = [];
        private readonly Dictionary<string, NifEnumType> _enumTypes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, NifEnumOption>> _enumOptions = new(StringComparer.Ordinal);
        private readonly List<uint> _supportedVersions = [];

        // Token substitutions, keyed by the attribute they apply to, in the order
        // they were declared. Order matters: nif.xml declares the operator tokens
        // last precisely so that expansions of earlier tokens get operator-expanded
        // in the same pass.
        private readonly Dictionary<string, List<(string Token, string Replacement)>> _tokens =
            new(StringComparer.Ordinal);

        // Structs that are spliced into their parent rather than nested, so that a
        // Havok filter's fields sit directly on the block that declares one. Taken
        // verbatim from NifSkope, since the flattening is observable in field paths.
        private static readonly HashSet<string> MixinTypes = new(StringComparer.Ordinal)
        {
            "HavokFilter",
            "HavokMaterial",
            "bhkRagdollConstraintCInfo",
            "bhkLimitedHingeConstraintCInfo",
            "bhkHingeConstraintCInfo",
            "bhkBallAndSocketConstraintCInfo",
            "bhkPrismaticConstraintCInfo",
            "bhkMalleableConstraintCInfo",
            "bhkConstraintData",
            "bhkConstraintCInfo"
        };

        public IReadOnlyDictionary<string, NifBlockDef> Compounds => _compounds;
        public IReadOnlyDictionary<string, NifBlockDef> Blocks => _blocks;
        public IReadOnlyDictionary<uint, NifBlockDef> BlockHashes => _blockHashes;
        public IReadOnlyList<uint> SupportedVersions => _supportedVersions;

        private NifXmlDatabase()
        {
        }

        // --- loading ---------------------------------------------------------

        /// <summary>Parses the nif.xml embedded in this assembly.</summary>
        public static NifXmlDatabase LoadEmbedded()
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("nif.xml")
                ?? throw new NifFormatException("nif.xml is missing from the assembly resources");

            return Load(stream);
        }

        /// <summary>Parses a nif.xml from a stream.</summary>
        public static NifXmlDatabase Load(Stream stream)
        {
            var db = new NifXmlDatabase();
            db.Parse(XDocument.Load(stream, LoadOptions.None));
            return db;
        }

        private void Parse(XDocument doc)
        {
            XElement root = doc.Root
                ?? throw new NifFormatException("nif.xml is empty");

            if (root.Name.LocalName != "niftoolsxml")
                throw new NifFormatException($"this is not a niftoolsxml file (root is <{root.Name.LocalName}>)");

            // A single ordered pass, so that a declaration only sees the tokens and
            // types declared above it -- which is what NifSkope's SAX handler does.
            foreach (XElement e in root.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "version":
                        ParseVersion(e);
                        break;

                    case "token":
                        ParseTokenGroup(e);
                        break;

                    case "basic":
                        ParseBasic(e);
                        break;

                    case "enum":
                    case "bitflags":
                        ParseEnum(e, e.Name.LocalName == "bitflags" ? NifEnumType.Flags : NifEnumType.Default);
                        break;

                    case "bitfield":
                        ParseBitfield(e);
                        break;

                    case "struct":
                        ParseBlock(e, isCompound: true);
                        break;

                    case "niobject":
                        ParseBlock(e, isCompound: false);
                        break;

                    case "module":
                    case "verattr":
                        // Metadata that the reader does not need.
                        break;

                    default:
                        throw new NifFormatException($"unexpected element <{e.Name.LocalName}> in nif.xml");
                }
            }

            Validate();
        }

        private void ParseVersion(XElement e)
        {
            string num = (Attr(e, "num") ?? string.Empty).Trim();
            uint v = NifVersion.FromString(num);

            if (v == 0 || num.Length == 0)
                throw new NifFormatException($"invalid version tag \"{num}\"");

            _supportedVersions.Add(v);
        }

        private void ParseTokenGroup(XElement e)
        {
            string attrs = Attr(e, "attrs") ?? string.Empty;

            foreach (XElement child in e.Elements())
            {
                string token = Attr(child, "token") ?? string.Empty;
                string replacement = Attr(child, "string") ?? string.Empty;

                // nif.xml spells positive infinity as a word; the expression parser
                // only deals in numbers.
                if (replacement == "INFINITY")
                    replacement = "0x7F800000";

                foreach (string attr in attrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!_tokens.TryGetValue(attr, out var list))
                        _tokens[attr] = list = [];

                    list.Add((token, replacement));
                }
            }
        }

        private void ParseBasic(XElement e)
        {
            string name = Attr(e, "name") ?? string.Empty;

            if (ResolveType(name) == NifValueType.None)
                throw new NifFormatException($"basic definition {name} has no corresponding storage type");
        }

        private void ParseEnum(XElement e, NifEnumType enumType)
        {
            string name = Attr(e, "name") ?? string.Empty;
            string storage = Attr(e, "storage") ?? string.Empty;

            if (name.Length == 0 || storage.Length == 0)
                throw new NifFormatException("enum definition must have a name and a storage type");

            RegisterAlias(name, storage);
            _enumTypes[name] = enumType;

            var options = new Dictionary<string, NifEnumOption>(StringComparer.Ordinal);

            foreach (XElement option in e.Elements("option"))
            {
                string optName = Attr(option, "name") ?? string.Empty;

                // A bit index is an alternative spelling of the value.
                string optValue = Attr(option, "bit") ?? Attr(option, "value") ?? string.Empty;

                if (optName.Length == 0 || optValue.Length == 0)
                    throw new NifFormatException($"option in enum {name} must have a name and a value");

                if (!TryParseUInt(optValue, out uint value))
                    throw new NifFormatException($"option {name}.{optName} has a non-integer value \"{optValue}\"");

                options[optName] = new NifEnumOption(optName, value, TextOf(option));
            }

            _enumOptions[name] = options;
        }

        private void ParseBitfield(XElement e)
        {
            string name = Attr(e, "name") ?? string.Empty;
            string storage = Attr(e, "storage") ?? string.Empty;

            if (name.Length == 0 || storage.Length == 0)
                throw new NifFormatException("bitfield definition must have a name and a storage type");

            // Members describe how to present the packed value; the reader only
            // needs to know how many bytes the storage type occupies.
            RegisterAlias(name, storage);
        }

        private void ParseBlock(XElement e, bool isCompound)
        {
            string name = Attr(e, "name") ?? string.Empty;

            // A handful of "structs" -- Vector3, Color4, Matrix33, string and
            // friends -- name a type the stream already reads as a single value.
            // Those are documentation only: they must not become compounds, or
            // every field using them would nest instead of being read directly.
            if (ResolveType(name) != NifValueType.None)
                return;

            if (name.Length == 0)
                throw new NifFormatException("struct and niobject declarations must have a name");

            if (_compounds.ContainsKey(name) || _blocks.ContainsKey(name))
                throw new NifFormatException($"multiple declarations of {name}");

            var block = new NifBlockDef
            {
                Id = name,
                Abstract = IsTrue(Attr(e, "abstract")),
                Text = TextOf(e)
            };

            if (!isCompound)
            {
                block.Ancestor = Attr(e, "inherit") ?? string.Empty;

                if (block.Ancestor.Length > 0 && !_blocks.ContainsKey(block.Ancestor))
                    throw new NifFormatException($"forward declaration of block {block.Ancestor}");
            }

            foreach (XElement field in e.Elements("field"))
                block.Fields.Add(ParseField(field, block.Id));

            if (isCompound)
            {
                _compounds[block.Id] = block;

                // Structures whose field conditions refer to a sibling structure
                // rather than to their own fields. The vertex formats are the
                // reason this exists: every element of a Vertex Data array shares
                // the layout decided by the first one.
                if (IsTrue(Attr(e, "externalcond")) || block.Id.StartsWith("BSVertexData", StringComparison.Ordinal))
                    _fixedCompounds[block.Id] = block;
            }
            else
            {
                _blocks[block.Id] = block;
                _blockHashes[Djb1Hash(block.Id)] = block;
            }
        }

        private NifFieldDef ParseField(XElement e, string owner)
        {
            string name = Attr(e, "name") ?? string.Empty;
            string type = Attr(e, "type") ?? string.Empty;
            string template = Attr(e, "template") ?? string.Empty;

            string arg = TokenReplace(e, "arg");
            string arr1 = TokenReplace(e, "length");
            string arr2 = TokenReplace(e, "width");
            string cond = TokenReplace(e, "cond");
            string verCond = TokenReplace(e, "vercond");
            string defaultValue = TokenReplace(e, "default");

            uint ver1 = NifVersion.FromString(Attr(e, "since"));
            uint ver2 = NifVersion.FromString(Attr(e, "until"));

            var flags = NifFieldFlags.None;

            // onlyT/excludeT are a shorthand for "this field exists only when the
            // block's template argument is (not) this type".
            string onlyT = Attr(e, "onlyT") ?? string.Empty;
            string excludeT = Attr(e, "excludeT") ?? string.Empty;

            if (onlyT.Length > 0 || excludeT.Length > 0)
            {
                if (cond.Length > 0)
                    throw new NifFormatException($"{owner}.{name} combines cond with onlyT/excludeT");

                if (onlyT.Length > 0 && excludeT.Length > 0)
                    throw new NifFormatException($"{owner}.{name} has both onlyT and excludeT");

                flags |= NifFieldFlags.TypeCondition;
                cond = onlyT.Length > 0 ? onlyT : $"!{excludeT}";
            }

            bool isTemplated = type == TemplatePlaceholder || template == TemplatePlaceholder;
            bool isCompound = _compounds.ContainsKey(type);
            bool isArray = arr1.Length > 0;
            bool isMultiArray = arr2.Length > 0;

            if (isMultiArray && !isArray)
                throw new NifFormatException($"{owner}.{name} has a width without a length");

            // A mixin is a struct flattened into its parent. Only a bare reference
            // qualifies: anything conditional or repeated has to keep its own node
            // so that the condition or the index has something to attach to.
            bool isMixin = isCompound
                && MixinTypes.Contains(type)
                && !isTemplated
                && !isArray
                && cond.Length == 0
                && verCond.Length == 0
                && ver1 == 0
                && ver2 == 0;

            if (isMixin)
                isCompound = false;

            // BSVertexDesc is read as one packed value here rather than as the
            // bitfield nif.xml describes, so args pointing into it need retargeting.
            if (arg == @"Vertex Desc\Vertex Attributes")
                arg = "Vertex Desc";

            if (isTemplated)
                flags |= NifFieldFlags.Templated;
            if (isCompound)
                flags |= NifFieldFlags.Compound;
            if (isArray)
                flags |= NifFieldFlags.Array;
            if (isMultiArray)
                flags |= NifFieldFlags.MultiArray;
            if (isMixin)
                flags |= NifFieldFlags.Mixin;
            if (IsTrue(Attr(e, "abstract")))
                flags |= NifFieldFlags.Abstract;
            if (IsTrue(Attr(e, "binary")))
                flags |= NifFieldFlags.Binary;
            if (cond.Length == 0 && verCond.Length == 0 && ver1 == 0 && ver2 == 0)
                flags |= NifFieldFlags.Conditionless;

            if ((flags & NifFieldFlags.Binary) != 0 && isMultiArray)
                throw new NifFormatException($"{owner}.{name} is a binary multi-array, which is not supported");

            if (name.Length == 0 || type.Length == 0)
                throw new NifFormatException($"a field of {owner} is missing its name or type");

            return new NifFieldDef
            {
                Name = name,
                Type = type,
                Template = template,
                Arg = arg,
                Arr1 = arr1,
                Arr2 = arr2,
                Cond = cond,
                VerCond = verCond,
                Ver1 = ver1,
                Ver2 = ver2,
                Flags = flags,
                ValueType = ResolveType(type),
                Default = defaultValue.Length > 0 ? defaultValue : null,
                Text = TextOf(e)
            };
        }

        private void Validate()
        {
            foreach ((string key, NifBlockDef compound) in _compounds)
            {
                foreach (NifFieldDef field in compound.Fields)
                {
                    CheckFieldTypes(key, field);

                    if (field.Type == key)
                        throw new NifFormatException($"struct {key} contains itself");
                }
            }

            foreach ((string key, NifBlockDef block) in _blocks)
            {
                if (block.Ancestor.Length > 0 && !_blocks.ContainsKey(block.Ancestor))
                    throw new NifFormatException($"niobject {key} inherits unknown ancestor {block.Ancestor}");

                if (block.Ancestor == key)
                    throw new NifFormatException($"niobject {key} inherits itself");

                foreach (NifFieldDef field in block.Fields)
                    CheckFieldTypes(key, field);
            }
        }

        private void CheckFieldTypes(string owner, NifFieldDef field)
        {
            bool typeKnown = _compounds.ContainsKey(field.Type)
                || ResolveType(field.Type) != NifValueType.None
                || field.Type == TemplatePlaceholder;

            if (!typeKnown)
                throw new NifFormatException($"{owner} refers to unknown type {field.Type}");

            bool templateKnown = field.Template.Length == 0
                || ResolveType(field.Template) != NifValueType.None
                || field.Template == TemplatePlaceholder
                || _blocks.ContainsKey(field.Template)
                || _compounds.ContainsKey(field.Template);

            if (!templateKnown)
                throw new NifFormatException($"{owner} refers to unknown template type {field.Template}");
        }

        // --- lookups ---------------------------------------------------------

        /// <summary>
        /// Resolves a nif.xml type name to its storage type, following any aliases
        /// that enum, bitflags and bitfield declarations introduced.
        /// </summary>
        public NifValueType ResolveType(string name) =>
            _typeMap.TryGetValue(name, out var type) ? type : NifValueType.None;

        public bool IsCompound(string name) => _compounds.ContainsKey(name);

        public bool IsBlock(string name) => _blocks.ContainsKey(name);

        /// <summary>
        /// True for structs whose field conditions are evaluated once, against the
        /// first element of the enclosing array, rather than per element.
        /// </summary>
        public bool IsFixedCompound(string name) => _fixedCompounds.ContainsKey(name);

        public NifBlockDef? GetBlock(string name) => _blocks.GetValueOrDefault(name);

        public NifBlockDef? GetCompound(string name) => _compounds.GetValueOrDefault(name);

        /// <summary>True when <paramref name="blockName"/> is, or descends from, <paramref name="ancestor"/>.</summary>
        public bool Inherits(string blockName, string ancestor)
        {
            while (true)
            {
                if (blockName == ancestor)
                    return true;

                if (!_blocks.TryGetValue(blockName, out var block) || block.Ancestor.Length == 0)
                    return false;

                blockName = block.Ancestor;
            }
        }

        /// <summary>The enum presentation for a type name, if it is an enum at all.</summary>
        public NifEnumType GetEnumType(string typeName) =>
            _enumTypes.GetValueOrDefault(typeName, NifEnumType.None);

        /// <summary>
        /// Looks up the numeric value of a named enum option, used to resolve
        /// symbolic <c>default</c> attributes.
        /// </summary>
        public bool TryGetEnumOptionValue(string typeName, string optionName, out uint value)
        {
            value = 0;

            if (!_enumOptions.TryGetValue(typeName, out var options) || !options.TryGetValue(optionName, out var option))
                return false;

            value = option.Value;
            return true;
        }

        /// <summary>
        /// The name of an enum option given its value, for writing a symbolic name
        /// into FBX instead of a bare number.
        /// </summary>
        public bool TryGetEnumOptionName(string typeName, uint value, out string name)
        {
            name = string.Empty;

            if (!_enumOptions.TryGetValue(typeName, out var options))
                return false;

            foreach (NifEnumOption option in options.Values)
            {
                if (option.Value != value)
                    continue;

                name = option.Name;
                return true;
            }

            return false;
        }

        public IReadOnlyCollection<NifEnumOption> GetEnumOptions(string typeName) =>
            _enumOptions.TryGetValue(typeName, out var options) ? options.Values : [];

        /// <summary>
        /// The full field list for a block, ancestors first, as the reader consumes
        /// them. Mixins are still represented as single fields here; splicing them
        /// happens when the item tree is built.
        /// </summary>
        public IReadOnlyList<NifFieldDef> GetInheritedFields(string blockName)
        {
            var fields = new List<NifFieldDef>();
            AppendFields(blockName, fields);
            return fields;
        }

        private void AppendFields(string blockName, List<NifFieldDef> into)
        {
            if (!_blocks.TryGetValue(blockName, out var block))
                return;

            if (block.Ancestor.Length > 0)
                AppendFields(block.Ancestor, into);

            into.AddRange(block.Fields);
        }

        // --- helpers ---------------------------------------------------------

        private void RegisterAlias(string alias, string storage)
        {
            if (!_typeMap.TryGetValue(storage, out var type))
                throw new NifFormatException($"cannot alias {alias} onto unknown storage type {storage}");

            _typeMap[alias] = type;
        }

        /// <summary>
        /// Reads an attribute, expanding any <c>#TOKEN#</c> macros declared for that
        /// attribute name, in declaration order.
        /// </summary>
        private string TokenReplace(XElement e, string attribute)
        {
            string value = Attr(e, attribute) ?? string.Empty;

            if (value.Length == 0 || !_tokens.TryGetValue(attribute, out var replacements))
                return value;

            foreach ((string token, string replacement) in replacements)
                value = value.Replace(token, replacement, StringComparison.Ordinal);

            return value;
        }

        private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

        private static bool IsTrue(string? value) => value is "1" or "true";

        /// <summary>The element's own text, ignoring the text of nested elements.</summary>
        private static string TextOf(XElement e) =>
            string.Concat(e.Nodes().OfType<XText>().Select(t => t.Value.Trim())).Trim();

        private static bool TryParseUInt(string s, out uint value)
        {
            s = s.Trim();

            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return uint.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

            if (uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return true;

            // Some options are written as negative numbers of the storage type.
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int signed))
            {
                value = unchecked((uint)signed);
                return true;
            }

            return false;
        }

        /// <summary>
        /// The block-name hash used by version 20.3.1.2, which stores hashes in the
        /// header instead of type strings.
        /// </summary>
        internal static uint Djb1Hash(string key)
        {
            uint hash = 0;

            foreach (char c in key)
            {
                hash *= 33;
                hash += c;
            }

            // NifSkope's default table size is UINT_MAX, which is the identity for
            // every value except UINT_MAX itself.
            return hash == uint.MaxValue ? 0 : hash;
        }
    }
}
