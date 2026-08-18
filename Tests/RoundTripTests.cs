using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// NIF to FBX and back, compared field by field.
    /// </summary>
    /// <remarks>
    /// The comparison walks both graphs from the root and follows references, so block
    /// order does not enter into it: what is being asked is whether the rebuilt file
    /// says the same things, not whether it says them in the same places.
    ///
    /// Byte identity is the goal and is not reached yet. What holds today, and what
    /// this pins, is that the ck-cmd example files come back with the same graph — the
    /// same blocks, of the same kinds, linked the same way — and differ only in the
    /// fields listed in <see cref="KnownGaps"/>. Each of those is either derived on
    /// import by design or a gap with a reason recorded against it.
    /// </remarks>
    public class RoundTripTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel RoundTrip(NifModel source)
        {
            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            var converter = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    // The root's name is a property of the file, and the option exists
                    // for FBX that never was a NIF. Carrying it keeps the comparison
                    // about the conversion rather than about the option.
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                });

            return converter.Convert(Db);
        }

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        /// <summary>
        /// Fields the round trip is not expected to reproduce, and why.
        /// </summary>
        /// <remarks>
        /// A field is listed by the name it ends in, since the same gap shows up under
        /// every shape in a file. Shrinking this list is the work; it exists so that
        /// anything *not* on it fails rather than being absorbed into a count.
        /// </remarks>
        public static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
        {
            // Derived on import by design. The size of a collision shape comes back
            // from its tessellated geometry, which is the half of the shape a DCC tool
            // can edit; carrying the original would ignore whatever was done to it.
            ["Radius"] = "refitted from the tessellated collision geometry",
            ["Dimensions"] = "refitted from the tessellated collision geometry",


            // Deliberately dropped, not lost. These bodies carry a mass on a layer
            // their own filter calls SKYL_STATIC, and a static with a mass is treated
            // as movable -- which is how scenery ends up falling through the world. So
            // the static profile zeroes both, as ck-cmd's does, and the source file
            // disagrees with itself rather than with the importer.
            ["Mass"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m11"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m22"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m33"] = "zeroed by the static motion profile, as ck-cmd does",

            // 0xCD in every byte is the debug heap's fill pattern: these are fields the
            // exporter that wrote the fixture never initialised. There is nothing to
            // reproduce.
            ["Auto Remove Level"] = "uninitialised in the source file (0xCD)",
            ["Response Modifier Flags"] = "uninitialised in the source file (0xCD)",
            ["Num Shape Keys in Contact Point"] = "uninitialised in the source file (0xCD)",
            ["Force Collided Onto PPU"] = "uninitialised in the source file (0xCD)",

            // Real gaps, each its own piece of work.
            ["BS Data Flags"] = "tangents are not rebuilt, so the flag that announces them is not set",
            ["Tangents"] = "tangent space is not rebuilt on import",
            ["Bitangents"] = "tangent space is not rebuilt on import",
            ["Consistency Flags"] = "not carried",
            ["Shader Flags 2"] = "one flag differs; the shader flag words are not carried verbatim",
            ["Bounding Sphere"] = "recomputed rather than carried",
            ["Center"] = "recomputed rather than carried",
            // The hull is refitted, so its vertices and planes come back in the order
            // the fit produced rather than the order Havok emitted. The values agree:
            // the plane convention is checked directly, against a shipped hull, in
            // ConvexHullPlaneTests.
            ["Vertices"] = "convex hull refitted from the tessellation, so the order differs",
            ["Normals"] = "convex hull refitted from the tessellation, so the order differs",
        };

        public static TheoryData<string> CkCmdExamples() =>
            new("generate_rb.nif", "generate_rb_box.nif", "generate_rb_sphere.nif", "multi_material_cube.nif");

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void TheGraphSurvivesTheRoundTrip(string name)
        {
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            // Same blocks, of the same kinds. The comparison below follows references
            // and would miss a block that nothing points at.
            Assert.Equal(
                source.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal),
                rebuilt.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal));
        }

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void OnlyTheKnownGapsDiffer(string name)
        {
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            var unexplained = NifComparer.Compare(source, rebuilt)
                .Where(d => !KnownGaps.ContainsKey(d.Field))
                .ToList();

            Assert.True(
                unexplained.Count == 0,
                $"{name} differs in {unexplained.Count} fields that are not known gaps:\n  "
                + string.Join("\n  ", unexplained.Take(20)));
        }

        [Fact]
        public void TheRootKeepsItsKind()
        {
            // Half of BSXFlags is a question about the root, twice asking whether it is
            // exactly NiNode, so rebuilding one kind as another changes what the file
            // claims about itself.
            NifModel source = Load("nifly/TestNifFile_Static_SE.nif");

            Assert.Equal("NiNode", source.Blocks[0].Name);

            NifModel rebuilt = RoundTrip(source);
            NifItem root = rebuilt.GetBlock(rebuilt.FindItem(rebuilt.Footer, "Roots")!.Children[0])!;

            Assert.Equal("NiNode", root.Name);
        }

        [Theory]
        [InlineData("nifly/TestNifFile_OrderedNode_SE.nif", "BSOrderedNode")]
        [InlineData("nifly/TestNifFile_MultiBound_SE.nif", "BSMultiBoundNode")]
        public void ANodeKeepsItsKind(string name, string kind)
        {
            NifModel source = Load(name);

            Assert.Contains(source.Blocks, b => b.Name == kind);

            NifModel rebuilt = RoundTrip(source);

            Assert.Contains(rebuilt.Blocks, b => b.Name == kind);
        }

        [Fact]
        public void TheCollisionObjectKeepsItsFlags()
        {
            // bhkCOFlags says how the body and its node keep in step: SET_LOCAL reads
            // the body transform as local, SYNC_ON_UPDATE follows the node when it is
            // animated. Rebuilding it as a bare ACTIVE leaves the collision the right
            // size and in roughly the right place, no longer tracking what it belongs
            // to.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceCollision = source.Blocks.First(b => b.Name == "bhkCollisionObject");

            uint expected = source.GetUInt(sourceCollision, "Flags");

            Assert.Equal(9u, expected);

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltCollision = rebuilt.Blocks.First(b => b.Name == "bhkCollisionObject");

            Assert.Equal(expected, rebuilt.GetUInt(rebuiltCollision, "Flags"));
        }

        [Fact]
        public void TheCollisionMaterialSurvives()
        {
            // Nothing in the tessellated triangles says wood rather than stone, and the
            // engine reads it for footstep sound and impact response.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceShape = source.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(source, sourceShape));

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltShape = rebuilt.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(rebuilt, rebuiltShape));
        }

        [Fact]
        public void TheShaderKeepsAnIdentityUvTransform()
        {
            // A zero UV scale does not fail loudly: it multiplies every texture
            // coordinate in the mesh to nothing.
            NifModel rebuilt = RoundTrip(Load("multi_material_cube.nif"));

            foreach (NifItem shader in rebuilt.Blocks.Where(b => b.Name == "BSLightingShaderProperty"))
                Assert.Equal(new NifVector2(1f, 1f), rebuilt.FindItem(shader, "UV Scale")!.Value.Get<NifVector2>());
        }

        [Fact]
        public void TheImporterCalculatesBsxFlags()
        {
            NifModel rebuilt = RoundTrip(Load("generate_rb_box.nif"));

            NifItem bsx = Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");

            Assert.Equal(rebuilt.Calculate(), rebuilt.GetUInt(bsx, "Integer Data"));
        }
    }
}
