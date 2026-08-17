using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Skinning, against nifly's LE and SE fixtures.
    /// </summary>
    /// <remarks>
    /// The two editions store weights in different places. LE keeps them in
    /// NiSkinData's bone list; SE keeps them per vertex in the skin partition,
    /// which also owns the geometry. Both fixtures describe the same two-bone
    /// cylinder, so the same assertions should hold either way.
    /// </remarks>
    public class SkinningTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        /// <summary>The skinned fixtures, and which edition each is.</summary>
        public static TheoryData<string> Skinned() =>
        [
            "TestNifFile_Skinned_SE.nif",
            "TestNifFile_Skinned_Dynamic_SE.nif",
            "TestNifFile_Skinned_NoNiSkinDataWeights.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif",
            "TestNifFile_Optimize_Dynamic_SE_to_LE.nif"
        ];

        private static NifItem FirstSkinnedShape(NifModel model) =>
            model.Blocks.First(b => model.GetSkinInstance(b) is not null);

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ReadsSkinFromEitherEdition(string name)
        {
            NifModel model = Load(name);
            SkinData? skin = model.ReadSkin(FirstSkinnedShape(model));

            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.Bones);

            // Every bone must be named, or the FBX side cannot link a cluster to it.
            Assert.All(skin.Bones, b => Assert.False(string.IsNullOrEmpty(b.Name)));

            // And at least one must actually move something.
            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void WeightsPerVertexSumToOne(string name)
        {
            NifModel model = Load(name);
            SkinData skin = model.ReadSkin(FirstSkinnedShape(model))!;

            foreach ((ushort vertex, List<(int Bone, float Weight)> influences) in skin.ByVertex())
            {
                float total = influences.Sum(i => i.Weight);

                // A vertex whose weights do not sum to one drifts toward the origin
                // when the mesh deforms.
                Assert.True(Math.Abs(total - 1f) < 0.01f,
                    $"vertex {vertex} has weights summing to {total:G6}");
            }
        }

        [Fact]
        public void BothEditionsDescribeTheSameSkin()
        {
            // The LE and SE fixtures are the same cylinder saved twice, so the skins
            // should agree despite being stored completely differently.
            SkinData le = Load("TestNifFile_Optimize_Dynamic_LE_to_SE.nif") is var lm
                ? lm.ReadSkin(FirstSkinnedShape(lm))!
                : throw new InvalidOperationException();

            NifModel sm = Load("TestNifFile_Skinned_SE.nif");
            SkinData se = sm.ReadSkin(FirstSkinnedShape(sm))!;

            Assert.Equal(le.Bones.Count, se.Bones.Count);
            Assert.Equal(
                le.Bones.Select(b => b.Name).OrderBy(n => n),
                se.Bones.Select(b => b.Name).OrderBy(n => n));
        }

        [Fact]
        public void ReadsWeightsWhenOnlyThePartitionHasThem()
        {
            // This fixture exists precisely because NiSkinData carries no weights,
            // so they can only come from the partition.
            NifModel model = Load("TestNifFile_Skinned_NoNiSkinDataWeights.nif");
            SkinData skin = model.ReadSkin(FirstSkinnedShape(model))!;

            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        [Fact]
        public void UnskinnedShapesReportNoSkin()
        {
            NifModel model = Load("TestNifFile_Static_SE.nif");

            Assert.All(model.Blocks, b => Assert.Null(model.ReadSkin(b)));
        }

        // --- limiting influences ---------------------------------------------

        [Fact]
        public void LimitingInfluencesKeepsTheHeaviestAndRenormalises()
        {
            var skin = new SkinData();

            for (int i = 0; i < 6; i++)
                skin.Bones.Add(new SkinBone { Name = $"Bone{i}" });

            // One vertex pulled by six bones, which Skyrim cannot represent.
            float[] weights = [0.30f, 0.25f, 0.20f, 0.15f, 0.07f, 0.03f];

            for (int i = 0; i < weights.Length; i++)
                skin.Bones[i].Weights.Add((0, weights[i]));

            skin.LimitInfluences(4);

            var influences = skin.ByVertex()[0];

            Assert.Equal(4, influences.Count);

            // Renormalised, or the vertex would be under-weighted by the 10% that
            // the dropped influences carried.
            Assert.Equal(1f, influences.Sum(i => i.Weight), 4);

            // The four kept are the heaviest four.
            Assert.Equal([0, 1, 2, 3], influences.Select(i => i.Bone).OrderBy(b => b));
        }

        // --- conversion to FBX -------------------------------------------------

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ConvertsSkinToFbxDeformers(string name)
        {
            NifModel model = Load(name);
            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            Assert.Empty(converter.Warnings);

            // One Skin deformer per skinned mesh, and a Cluster per bone under it.
            var skins = scene.OfClass("Deformer", "Skin").ToList();
            Assert.NotEmpty(skins);

            foreach (FbxObject skin in skins)
            {
                var clusters = scene.ChildrenOf(skin.Id)
                    .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster")
                    .ToList();

                Assert.NotEmpty(clusters);

                foreach (FbxObject cluster in clusters)
                {
                    // A cluster with no bone linked to it deforms nothing.
                    Assert.NotEmpty(scene.ChildrenOf(cluster.Id).Where(o => o.Class == "Model"));

                    var indices = (int[])cluster.Child("Indexes")!.Properties[0]!;
                    var weights = (double[])cluster.Child("Weights")!.Properties[0]!;

                    Assert.Equal(indices.Length, weights.Length);
                    Assert.NotEmpty(indices);
                }
            }
        }

        [Fact]
        public void SkinIsAttachedToTheGeometry()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject geometry = scene.OfClass("Geometry").First();

            // FBX hangs the skin off the geometry, not off the model.
            Assert.Single(scene.ChildrenOf(geometry.Id).Where(o => o.SubClass == "Skin"));
        }

        [Fact]
        public void ClusterIndicesAreInRangeOfTheMesh()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject geometry = scene.OfClass("Geometry").First();
            int vertexCount = ((double[])geometry.Child("Vertices")!.Properties[0]!).Length / 3;

            FbxObject skin = scene.ChildrenOf(geometry.Id).First(o => o.SubClass == "Skin");

            foreach (FbxObject cluster in scene.ChildrenOf(skin.Id).Where(o => o.SubClass == "Cluster"))
            {
                var indices = (int[])cluster.Child("Indexes")!.Properties[0]!;

                // An index past the mesh is how a skin silently deforms nothing.
                Assert.All(indices, i => Assert.InRange(i, 0, vertexCount - 1));
            }
        }

        [Fact]
        public void SkinSurvivesAWriteAndReadCycle()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            FbxDocument document = new NifToFbx(model).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            FbxObject geometry = reloaded.OfClass("Geometry").First();
            SkinData? skin = FbxSkinIO.ReadSkin(reloaded, geometry);

            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.Bones);
            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }
    }
}
