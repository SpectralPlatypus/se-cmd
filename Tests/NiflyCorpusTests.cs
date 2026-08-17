using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The reader and writer against nifly's test corpus.
    /// </summary>
    /// <remarks>
    /// Our own fixtures are all Skyrim LE files produced by one exporter, which is a
    /// narrow slice of what a NIF can be. These come from
    /// <see href="https://github.com/ousnius/nifly">nifly</see>, the library behind
    /// BodySlide and Outfit Studio, and cover Skyrim SE as well as LE, skinned
    /// meshes, deep block graphs, loose blocks, multi-bounds, ordered nodes and
    /// furniture collision.
    ///
    /// They are the only skinned Skyrim SE files here, and the only real evidence
    /// that the format support is broad rather than merely sufficient for four
    /// hand-made cubes.
    /// </remarks>
    public class NiflyCorpusTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name);

        /// <summary>Every corpus file that is expected to load.</summary>
        public static TheoryData<string> Corpus() =>
        [
            "TestNifFile_Animated_LE.nif",
            "TestNifFile_DeepGraph_SE.nif",
            "TestNifFile_FixBSXFlags_AddExtEmit.nif",
            "TestNifFile_FixBSXFlags_RemoveExtEmit.nif",
            "TestNifFile_FixShaderFlags_AddEnvMap.nif",
            "TestNifFile_FixShaderFlags_RemoveEnvMap.nif",
            "TestNifFile_Furniture_Col_SE.nif",
            "TestNifFile_LooseBlocks_SE.nif",
            "TestNifFile_MultiBound_SE.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif",
            "TestNifFile_Optimize_Dynamic_SE_to_LE.nif",
            "TestNifFile_Optimize_LE_to_SE.nif",
            "TestNifFile_Optimize_SE_to_LE.nif",
            "TestNifFile_OrderedNode_SE.nif",
            "TestNifFile_RootNonZero.nif",
            "TestNifFile_Skinned_Dynamic_SE.nif",
            "TestNifFile_Skinned_NoNiSkinDataWeights.nif",
            "TestNifFile_Skinned_SE.nif",
            "TestNifFile_Static_SE.nif"
        ];

        /// <summary>The skinned files, which are the ones with skin blocks.</summary>
        public static TheoryData<string> Skinned() =>
        [
            "TestNifFile_Skinned_SE.nif",
            "TestNifFile_Skinned_Dynamic_SE.nif",
            "TestNifFile_Skinned_NoNiSkinDataWeights.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif",
            "TestNifFile_Optimize_Dynamic_SE_to_LE.nif",
            "TestNifFile_LooseBlocks_SE.nif"
        ];

        [Theory]
        [MemberData(nameof(Corpus))]
        public void LoadsWithoutWarnings(string name)
        {
            NifModel model = NifModel.Load(PathTo(name), Db);

            Assert.NotEmpty(model.Blocks);
            Assert.Empty(model.Warnings);
        }

        /// <summary>
        /// The load/save round trip, which is the strongest single check on the
        /// whole reader and writer: descriptors, conditions, array lengths and both
        /// stream directions all have to agree for the bytes to match.
        /// </summary>
        [Theory]
        [MemberData(nameof(Corpus))]
        public void SavingReproducesTheFileByteForByte(string name)
        {
            byte[] original = File.ReadAllBytes(PathTo(name));

            NifModel model = NifModel.Load(PathTo(name), Db);

            using var saved = new MemoryStream();
            model.Save(saved);

            byte[] actual = saved.ToArray();

            Assert.Equal(original.Length, actual.Length);

            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != actual[i])
                {
                    Assert.Fail($"{name} differs at offset 0x{i:X} " +
                                $"(expected 0x{original[i]:X2}, got 0x{actual[i]:X2})");
                }
            }
        }

        [Fact]
        public void CoversBothSkyrimStreamVersions()
        {
            var versions = Directory
                .GetFiles(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly"), "*.nif")
                .Where(f => !f.Contains("Corrupted", StringComparison.Ordinal))
                .Select(f => NifModel.Load(f, Db).BSVersion)
                .Distinct()
                .ToList();

            // 83 is Skyrim LE, 100 Skyrim SE. Our own fixtures are all 83.
            Assert.Contains(83u, versions);
            Assert.Contains(100u, versions);
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ReadsSkinningBlocks(string name)
        {
            NifModel model = NifModel.Load(PathTo(name), Db);

            // One skin instance per shape, and Bethesda's files use the dismember
            // subclass rather than a plain NiSkinInstance.
            var skins = model.Blocks
                .Where(b => model.BlockInherits(b, "NiSkinInstance"))
                .ToList();

            Assert.NotEmpty(skins);

            foreach (NifItem skin in skins)
            {
                // A skin is meaningless without its bones and its per-bone weights.
                NifItem? data = model.GetRef(skin, "Data");
                Assert.NotNull(data);

                uint bones = model.GetUInt(skin, "Num Bones");
                Assert.True(bones > 0, "a skin must reference at least one bone");

                Assert.Equal(bones, model.GetUInt(data!, "Num Bones"));

                // Every bone link must resolve to a real node.
                foreach (NifItem bone in model.GetRefArray(skin, "Bones"))
                    Assert.True(model.BlockInherits(bone, "NiNode"), $"{bone.Name} is not a node");
            }
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void SkinWeightsAreWellFormed(string name)
        {
            NifModel model = NifModel.Load(PathTo(name), Db);

            NifItem skin = model.Blocks.First(b => model.BlockInherits(b, "NiSkinInstance"));
            NifItem data = model.GetRef(skin, "Data")!;

            NifItem? boneList = model.FindItem(data, "Bone List");

            if (boneList is null)
                return;

            foreach (NifItem bone in boneList.Children)
            {
                NifItem? weights = model.FindItem(bone, "Vertex Weights");

                if (weights is null)
                    continue;

                foreach (NifItem entry in weights.Children)
                {
                    float weight = model.FindItem(entry, "Weight")!.Value.ToFloat();

                    // A weight outside 0..1 means the layout was misread rather than
                    // the file being odd.
                    Assert.InRange(weight, 0f, 1.0001f);
                }
            }
        }

        [Fact]
        public void NiTriShapeGeometryConvertsToFbx()
        {
            // The LE file stores its geometry as NiTriShape, which the converter
            // handles. Skinning is not carried across yet, but the mesh must still
            // arrive rather than the shape being skipped.
            NifModel model = NifModel.Load(PathTo("TestNifFile_Optimize_Dynamic_LE_to_SE.nif"), Db);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            Assert.Equal(2, scene.OfClass("Geometry").Count());
        }

        [Fact]
        public void BsTriShapeGeometryConvertsToFbx()
        {
            // Skyrim SE stores geometry in BSTriShape, which packs its vertex data
            // inline and inherits NiAVObject directly rather than NiTriBasedGeom.
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            Assert.Contains(model.Blocks, b => b.Name == "BSTriShape");

            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            Assert.Empty(converter.Warnings);
            Assert.Equal(2, scene.OfClass("Geometry").Count());
        }

        [Fact]
        public void BsTriShapeVertexDataIsDecoded()
        {
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            // A skinned SE shape stores nothing in itself: both the vertex data and
            // the triangles live in the skin partition, and the shape's own counts
            // are zero. So the expected numbers come from the partition.
            NifItem shape = model.Blocks.First(b => b.Name == "BSTriShape");
            Assert.Equal(0u, model.GetUInt(shape, "Num Vertices"));

            NifItem partition = model.Blocks.First(b => b.Name == "NiSkinPartition");
            NifItem entry = model.FindItem(partition, "Partitions")!.Children[0];

            uint declaredVertices = model.GetUInt(entry, "Num Vertices");
            uint declaredTriangles = model.GetUInt(entry, "Num Triangles");

            var scene = new FbxScene(new NifToFbx(model).Convert());
            FbxObject geometry = scene.OfClass("Geometry").First();

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            Assert.Equal((int)declaredVertices, vertices.Length / 3);
            Assert.Equal((int)declaredTriangles, indices.Length / 3);

            // Positions come from a packed vertex struct, so a decoding slip shows
            // up as everything collapsing to the origin.
            bool anyNonZero = vertices.Any(v => Math.Abs(v) > 1e-6);
            Assert.True(anyNonZero, "decoded vertices are all at the origin");
        }

        [Fact]
        public void BsTriShapeNormalsAndUvsSurvive()
        {
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            var scene = new FbxScene(new NifToFbx(model).Convert());
            FbxObject geometry = scene.OfClass("Geometry").First();

            var normals = geometry.Child("LayerElementNormal");
            var uvs = geometry.Child("LayerElementUV");

            Assert.NotNull(normals);
            Assert.NotNull(uvs);

            // Normals are stored as signed bytes, so a unit length is the check that
            // the -1..1 expansion happened.
            var data = (double[])normals!.Nodes.First(n => n.Name == "Normals").Properties[0]!;

            double length = Math.Sqrt(data[0] * data[0] + data[1] * data[1] + data[2] * data[2]);
            Assert.InRange(length, 0.9, 1.1);
        }

        [Fact]
        public void RejectsADeliberatelyCorruptFile()
        {
            // nifly ships this to check that a reader fails rather than producing
            // nonsense. Loading it successfully would be the bug.
            Assert.ThrowsAny<Exception>(() =>
                NifModel.Load(PathTo("TestNifFile_Corrupted.nif"), Db));
        }

        [Fact]
        public void ReadsALargeBlockGraph()
        {
            // 185 blocks, which exercises the block-type table and index resolution
            // far harder than a seventeen-block fixture does.
            NifModel model = NifModel.Load(PathTo("TestNifFile_DeepGraph_SE.nif"), Db);

            Assert.True(model.Blocks.Count > 100, $"expected a deep graph, got {model.Blocks.Count} blocks");
            Assert.Equal((uint)model.Blocks.Count, model.GetUInt(model.Header, "Num Blocks"));
        }
    }
}
