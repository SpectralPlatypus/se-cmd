using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Exercises the descriptor parser against the real nif.xml shipped in the
    /// assembly, rather than against a cut-down fixture. Parsing it at all is most
    /// of the test: the parser validates every type reference and inheritance edge
    /// as it goes, so a successful load means all ~1200 declarations agree.
    /// </summary>
    public class NifXmlDatabaseTests
    {
        // Parsing 564 KB of XML is not free, so share one instance across the class.
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        [Fact]
        public void LoadsTheEmbeddedDescription()
        {
            Assert.NotEmpty(Db.Blocks);
            Assert.NotEmpty(Db.Compounds);
            Assert.NotEmpty(Db.SupportedVersions);
        }

        [Fact]
        public void KnowsTheCommonSkyrimBlocks()
        {
            Assert.True(Db.IsBlock("NiNode"));
            Assert.True(Db.IsBlock("BSTriShape"));
            Assert.True(Db.IsBlock("NiSkinInstance"));
            Assert.True(Db.IsBlock("bhkRigidBody"));
            Assert.True(Db.IsBlock("NiControllerSequence"));
        }

        [Fact]
        public void ResolvesInheritanceChains()
        {
            Assert.True(Db.Inherits("NiNode", "NiAVObject"));
            Assert.True(Db.Inherits("NiNode", "NiObjectNET"));
            Assert.True(Db.Inherits("NiNode", "NiObject"));
            Assert.True(Db.Inherits("BSFadeNode", "NiNode"));
            Assert.False(Db.Inherits("NiNode", "BSFadeNode"));
        }

        [Fact]
        public void InheritedFieldsComeBeforeOwnFields()
        {
            var fields = Db.GetInheritedFields("NiNode");
            var names = fields.Select(f => f.Name).ToList();

            // Name is declared on NiObjectNET, Children on NiNode itself.
            int name = names.IndexOf("Name");
            int children = names.IndexOf("Children");

            Assert.True(name >= 0, "NiNode should inherit a Name field");
            Assert.True(children >= 0, "NiNode should declare a Children field");
            Assert.True(name < children, "inherited fields must precede the block's own");
        }

        [Fact]
        public void TypesThatTheStreamReadsDirectlyAreNotCompounds()
        {
            // nif.xml declares these as <struct> for documentation, but they are
            // single values as far as the reader is concerned. Treating them as
            // compounds would make every field using them nest.
            foreach (string name in new[] { "Vector3", "Vector4", "Color4", "Matrix33", "Matrix44", "Triangle", "string" })
            {
                Assert.False(Db.IsCompound(name), $"{name} should not be a compound");
                Assert.NotEqual(NifValueType.None, Db.ResolveType(name));
            }
        }

        [Fact]
        public void ResolvesEnumAndBitfieldAliasesToTheirStorageType()
        {
            // Declared as <enum storage="uint">.
            Assert.Equal(NifValueType.UInt, Db.ResolveType("SkyrimShaderPropertyFlags1"));

            // Declared as <bitfield storage="uint64">.
            Assert.Equal(NifValueType.UInt64, Db.ResolveType("BSVertexDesc"));
        }

        [Fact]
        public void ResolvesNamedEnumOptions()
        {
            Assert.True(Db.TryGetEnumOptionValue("EndianType", "ENDIAN_LITTLE", out uint little));
            Assert.Equal(1u, little);
        }

        [Fact]
        public void ExpandsTokensInConditionsAndArguments()
        {
            // Declared as:
            //   <field name="Vertex Data" type="BSVertexDataSSE" length="Num Vertices"
            //          arg="Vertex Desc #RSH# 44" cond="Data Size #GT# 0" vercond="#BS_SSE#" />
            // Every one of those macros has to be gone by the time the reader sees it.
            var field = Db.GetInheritedFields("BSTriShape")
                .First(f => f.Name == "Vertex Data" && f.Type == "BSVertexDataSSE");

            Assert.Equal("Vertex Desc >> 44", field.Arg);
            Assert.Equal("Data Size > 0", field.Cond);

            Assert.NotEmpty(field.VerCond);
            Assert.DoesNotContain('#', field.VerCond);

            // #BSVER# expands to a path into the header, not a bare field name.
            Assert.Contains(@"BS Header\BS Version", field.VerCond);
        }

        [Fact]
        public void ExpandsOperatorTokens()
        {
            // Every cond and vercond in the file should be free of #TOKEN# markers.
            var withTokens = Db.Blocks.Values
                .SelectMany(b => b.Fields)
                .Where(f => f.Cond.Contains('#') || f.VerCond.Contains('#'))
                .Select(f => f.Name)
                .ToList();

            Assert.Empty(withTokens);
        }

        [Fact]
        public void MarksArraysAndTheirLengthExpressions()
        {
            var children = Db.GetInheritedFields("NiNode").First(f => f.Name == "Children");

            Assert.True(children.IsArray);
            Assert.Equal("Num Children", children.Arr1);
        }

        [Fact]
        public void FlattensHavokFilterAsAMixin()
        {
            // bhkWorldObject holds a HavokFilter, which is spliced in rather than
            // nested, so its fields appear directly on the block.
            var field = Db.GetInheritedFields("bhkRigidBody").FirstOrDefault(f => f.Type == "HavokFilter");

            Assert.NotNull(field);
            Assert.True(field!.IsMixin);
            Assert.False(field.IsCompound);
        }

        [Fact]
        public void TreatsVertexDataAsAFixedCompound()
        {
            // Every element of a Vertex Data array shares the layout decided by the
            // first one, so its conditions are evaluated once.
            Assert.True(Db.IsFixedCompound("BSVertexData"));
        }

        [Fact]
        public void CompilesConditionExpressionsLazilyButCorrectly()
        {
            var field = Db.GetInheritedFields("NiNode").First(f => f.Name == "Children");

            // Conditionless fields still produce a usable no-op expression.
            Assert.True(field.CondExpr.IsNop);
        }

        [Fact]
        public void EveryFieldTypeResolvesToAStorageTypeOrACompound()
        {
            var unresolved = Db.Blocks.Values
                .SelectMany(b => b.Fields)
                .Concat(Db.Compounds.Values.SelectMany(c => c.Fields))
                .Where(f => f.ValueType == NifValueType.None
                            && !Db.IsCompound(f.Type)
                            && f.Type != "#T#")
                .Select(f => f.Type)
                .Distinct()
                .ToList();

            Assert.Empty(unresolved);
        }

        [Fact]
        public void SupportsTheSkyrimSpecialEditionVersion() =>
            Assert.Contains(0x14020007u, Db.SupportedVersions);
    }
}
