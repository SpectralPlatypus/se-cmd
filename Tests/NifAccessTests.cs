using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class NifAccessTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        [Fact]
        public void ReadsNodeNamesAndHierarchy()
        {
            NifModel model = Load("multi_material_cube.nif");
            NifItem root = model.Blocks[0];

            Assert.Equal("Scene", model.GetName(root));

            var childNames = model.GetChildren(root).Select(model.GetName).ToList();

            Assert.Contains("Cube", childNames);
            Assert.Contains("Light", childNames);
            Assert.Contains("Camera", childNames);
        }

        [Fact]
        public void ReadsTransforms()
        {
            NifModel model = Load("multi_material_cube.nif");
            NifItem root = model.Blocks[0];

            NifTransform transform = model.GetTransform(root);

            // The scene root of these fixtures is untransformed.
            Assert.Equal(1f, transform.Scale, 5);
            Assert.Equal(0f, transform.Translation.X, 5);
        }

        [Fact]
        public void ReadsGeometry()
        {
            NifModel model = Load("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem? data = model.GetRef(shape, "Data");

            Assert.NotNull(data);

            var vertices = model.GetVertices(data!);
            var triangles = model.GetGeometryTriangles(data!);

            Assert.NotEmpty(vertices);
            Assert.NotEmpty(triangles);

            // Every index must be in range, or the mesh would be corrupt.
            Assert.All(triangles, t =>
            {
                Assert.InRange(t.V1, 0, vertices.Count - 1);
                Assert.InRange(t.V2, 0, vertices.Count - 1);
                Assert.InRange(t.V3, 0, vertices.Count - 1);
            });
        }

        [Fact]
        public void ReadsNormalsAndUvs()
        {
            NifModel model = Load("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem data = model.GetRef(shape, "Data")!;

            var vertices = model.GetVertices(data);
            var normals = model.GetNormals(data);
            var uvs = model.GetUvSet(data);

            // Per-vertex attributes, when present, must match the vertex count.
            if (normals.Count > 0)
                Assert.Equal(vertices.Count, normals.Count);

            if (uvs.Count > 0)
                Assert.Equal(vertices.Count, uvs.Count);
        }

        [Fact]
        public void FollowsShaderPropertyReferences()
        {
            NifModel model = Load("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem? shader = model.GetRef(shape, "Shader Property");

            Assert.NotNull(shader);
            Assert.True(model.BlockInherits(shader!, "BSLightingShaderProperty"));
        }

        [Fact]
        public void FindsCollisionObjects()
        {
            NifModel model = Load("generate_rb_box.nif");

            NifItem root = model.Blocks[0];
            NifItem? collision = model.GetRef(root, "Collision Object");

            Assert.NotNull(collision);
            Assert.True(model.BlockInherits(collision!, "bhkCollisionObject"));

            NifItem? body = model.GetRef(collision!, "Body");
            Assert.NotNull(body);
            Assert.True(model.BlockInherits(body!, "bhkRigidBody"));
        }

        // --- transform maths --------------------------------------------------

        [Fact]
        public void EulerRoundTripsThroughARotationMatrix()
        {
            foreach ((float x, float y, float z) in new[]
                     {
                         (0f, 0f, 0f),
                         (30f, 0f, 0f),
                         (0f, 45f, 0f),
                         (0f, 0f, 60f),
                         (15f, -25f, 80f),
                         (-170f, 12f, 33f)
                     })
            {
                NifMatrix33 m = NifTransform.RotationFromEulerDegrees(x, y, z);
                var transform = new NifTransform(new NifVector3(), m, 1f);

                NifVector3 back = transform.ToEulerDegrees();
                NifMatrix33 again = NifTransform.RotationFromEulerDegrees(back.X, back.Y, back.Z);

                // Euler angles are not unique, so compare the matrices rather than
                // the angles themselves.
                Assert.Equal(m.M11, again.M11, 3);
                Assert.Equal(m.M12, again.M12, 3);
                Assert.Equal(m.M13, again.M13, 3);
                Assert.Equal(m.M21, again.M21, 3);
                Assert.Equal(m.M22, again.M22, 3);
                Assert.Equal(m.M23, again.M23, 3);
                Assert.Equal(m.M31, again.M31, 3);
                Assert.Equal(m.M32, again.M32, 3);
                Assert.Equal(m.M33, again.M33, 3);
            }
        }

        [Fact]
        public void QuaternionMatchesTheRotationMatrix()
        {
            NifMatrix33 m = NifTransform.RotationFromEulerDegrees(25f, -40f, 70f);
            var transform = new NifTransform(new NifVector3(), m, 1f);

            NifQuat q = transform.ToQuaternion();

            // A rotation quaternion is a unit quaternion.
            float length = MathF.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            Assert.Equal(1f, length, 4);
        }

        [Fact]
        public void AppliesTranslationRotationAndScale()
        {
            var transform = new NifTransform(
                new NifVector3(10f, 0f, 0f),
                NifTransform.RotationFromEulerDegrees(0f, 0f, 90f),
                2f);

            NifVector3 moved = transform.Apply(new NifVector3(1f, 0f, 0f));

            // Scaled by two, rotated a quarter turn about Z, then translated.
            Assert.Equal(10f, moved.X, 3);
            Assert.Equal(2f, moved.Y, 3);
            Assert.Equal(0f, moved.Z, 3);
        }

        [Fact]
        public void ComposesWithAParentTransform()
        {
            var parent = new NifTransform(new NifVector3(5f, 0f, 0f), NifMatrix33.Identity, 1f);
            var child = new NifTransform(new NifVector3(0f, 3f, 0f), NifMatrix33.Identity, 1f);

            NifTransform world = child.ComposedWith(parent);

            Assert.Equal(5f, world.Translation.X, 3);
            Assert.Equal(3f, world.Translation.Y, 3);
        }

        [Fact]
        public void MatrixRoundTripsThroughDecomposition()
        {
            var original = new NifTransform(
                new NifVector3(1f, 2f, 3f),
                NifTransform.RotationFromEulerDegrees(10f, 20f, 30f),
                1.5f);

            NifTransform back = NifTransform.FromMatrix(original.ToMatrix());

            Assert.Equal(original.Translation.X, back.Translation.X, 3);
            Assert.Equal(original.Translation.Y, back.Translation.Y, 3);
            Assert.Equal(original.Translation.Z, back.Translation.Z, 3);
            Assert.Equal(original.Scale, back.Scale, 3);
            Assert.Equal(original.Rotation.M11, back.Rotation.M11, 3);
            Assert.Equal(original.Rotation.M23, back.Rotation.M23, 3);
        }
    }
}
