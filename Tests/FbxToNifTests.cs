using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class FbxToNifTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        /// <summary>Converts an FBX fixture to a NIF, then saves and reloads it.</summary>
        private static NifModel FromFbx(string name, out List<string> warnings)
        {
            var scene = new FbxScene(FbxDocument.Load(PathTo(name)));
            var converter = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(name)
            });

            NifModel model = converter.Convert(Db);
            warnings = converter.Warnings;

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        /// <summary>NIF to FBX and back, the full round trip.</summary>
        private static NifModel RoundTrip(string nif)
        {
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();

            var converter = new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = Path.GetFileNameWithoutExtension(nif) });

            NifModel rebuilt = converter.Convert(Db);

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        public static TheoryData<string> FbxFiles() =>
        [
            "generate_rb_box_with_mesh.fbx",
            "generate_rb_box_with_transform_mesh.fbx",
            "multi_material_cube.fbx"
        ];

        [Theory]
        [MemberData(nameof(FbxFiles))]
        public void ProducesALoadableNif(string name)
        {
            NifModel model = FromFbx(name, out _);

            Assert.NotEmpty(model.Blocks);
            Assert.Equal("BSFadeNode", model.Blocks[0].Name);
            Assert.Empty(model.Warnings);
        }

        [Fact]
        public void NamesTheRootAfterTheFile()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            Assert.Equal("multi_material_cube", model.GetName(model.Blocks[0]));
        }

        [Fact]
        public void BuildsGeometry()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            var shapes = model.Blocks.Where(b => b.Name == "NiTriShape").ToList();
            Assert.NotEmpty(shapes);

            foreach (NifItem shape in shapes)
            {
                NifItem? data = model.GetRef(shape, "Data");
                Assert.NotNull(data);

                var vertices = model.GetVertices(data!);
                var triangles = model.GetGeometryTriangles(data!);

                Assert.NotEmpty(vertices);
                Assert.NotEmpty(triangles);

                // A NIF with an out-of-range index crashes the game, so this is the
                // assertion that actually matters.
                Assert.All(triangles, t =>
                {
                    Assert.InRange(t.V1, 0, vertices.Count - 1);
                    Assert.InRange(t.V2, 0, vertices.Count - 1);
                    Assert.InRange(t.V3, 0, vertices.Count - 1);
                });
            }
        }

        [Fact]
        public void WritesNormalsAndABoundingSphere()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem data = model.GetRef(shape, "Data")!;

            var vertices = model.GetVertices(data);
            var normals = model.GetNormals(data);

            Assert.Equal(vertices.Count, normals.Count);

            float radius = model.FindItem(data, @"Bounding Sphere\Radius")!.Value.ToFloat();
            Assert.True(radius > 0, "a non-empty mesh must have a positive bounding radius");
        }

        [Fact]
        public void BuildsCollisionFromRigidBodyNodes()
        {
            // The _rb suffix marks a rigid body, which is rebuilt rather than
            // becoming an ordinary node.
            NifModel model = FromFbx("generate_rb_box_with_mesh.fbx", out _);

            Assert.Contains(model.Blocks, b => b.Name == "bhkCollisionObject");
            Assert.DoesNotContain(model.Blocks, b => model.GetName(b).EndsWith("_rb", StringComparison.Ordinal));
        }

        // --- full round trip --------------------------------------------------

        [Fact]
        public void RoundTripPreservesShapeCount()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            int before = source.Blocks.Count(b => b.Name == "NiTriShape");
            int after = rebuilt.Blocks.Count(b => b.Name == "NiTriShape");

            Assert.Equal(before, after);
        }

        [Fact]
        public void RoundTripPreservesGeometry()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem sourceShape = source.Blocks.First(b =>
                b.Name == "NiTriShape" && source.GetName(b) == "Cube_Material0");
            NifItem rebuiltShape = rebuilt.Blocks.First(b =>
                b.Name == "NiTriShape" && rebuilt.GetName(b) == "Cube_Material0");

            var sourceData = source.GetRef(sourceShape, "Data")!;
            var rebuiltData = rebuilt.GetRef(rebuiltShape, "Data")!;

            var sourceVertices = source.GetVertices(sourceData);
            var rebuiltVertices = rebuilt.GetVertices(rebuiltData);

            Assert.Equal(sourceVertices.Count, rebuiltVertices.Count);
            Assert.Equal(
                source.GetGeometryTriangles(sourceData).Count,
                rebuilt.GetGeometryTriangles(rebuiltData).Count);

            // The shape transform was baked into the vertices on the way out, so
            // positions come back in the parent's space rather than the shape's.
            NifTransform transform = source.GetTransform(sourceShape);

            for (int i = 0; i < sourceVertices.Count; i++)
            {
                NifVector3 expected = transform.Apply(sourceVertices[i]);

                Assert.Equal(expected.X, rebuiltVertices[i].X, 3);
                Assert.Equal(expected.Y, rebuiltVertices[i].Y, 3);
                Assert.Equal(expected.Z, rebuiltVertices[i].Z, 3);
            }
        }

        [Fact]
        public void RoundTripPreservesUvs()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);

            NifItem sourceShape = source.Blocks.First(b =>
                b.Name == "NiTriShape" && source.GetName(b) == "Cube_Material0");
            var sourceUvs = source.GetUvSet(source.GetRef(sourceShape, "Data")!);

            if (sourceUvs.Count == 0)
                return;

            NifModel rebuilt = RoundTrip("multi_material_cube.nif");
            NifItem rebuiltShape = rebuilt.Blocks.First(b =>
                b.Name == "NiTriShape" && rebuilt.GetName(b) == "Cube_Material0");
            var rebuiltUvs = rebuilt.GetUvSet(rebuilt.GetRef(rebuiltShape, "Data")!);

            Assert.Equal(sourceUvs.Count, rebuiltUvs.Count);

            // Flipped out and back, so V lands where it started.
            for (int i = 0; i < sourceUvs.Count; i++)
            {
                Assert.Equal(sourceUvs[i].X, rebuiltUvs[i].X, 3);
                Assert.Equal(sourceUvs[i].Y, rebuiltUvs[i].Y, 3);
            }
        }

        [Fact]
        public void RoundTripPreservesTheMaterial()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem sourceShader = source.GetRef(
                source.Blocks.First(b => b.Name == "NiTriShape"), "Shader Property")!;
            NifItem rebuiltShader = rebuilt.GetRef(
                rebuilt.Blocks.First(b => b.Name == "NiTriShape"), "Shader Property")!;

            Assert.Equal(
                source.FindItem(sourceShader, "Glossiness")!.Value.ToFloat(),
                rebuilt.FindItem(rebuiltShader, "Glossiness")!.Value.ToFloat(), 2);

            // Scaled to 0..1 for FBX and back to 0..999 for NIF.
            Assert.Equal(
                source.FindItem(sourceShader, "Specular Strength")!.Value.ToFloat(),
                rebuilt.FindItem(rebuiltShader, "Specular Strength")!.Value.ToFloat(), 1);
        }

        [Fact]
        public void RoundTripKeepsTheHierarchy()
        {
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem root = rebuilt.Blocks[0];
            Assert.Equal("BSFadeNode", root.Name);

            var names = rebuilt.GetChildren(root).Select(rebuilt.GetName).ToList();

            Assert.Contains("Cube", names);
            Assert.Contains("Light", names);
            Assert.Contains("Camera", names);
        }
    }
}
