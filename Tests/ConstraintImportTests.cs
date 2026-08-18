using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Rebuilding Havok constraints from a scene's attachment points.
    /// </summary>
    /// <remarks>
    /// ck-cmd has no equivalent: HKXWrangler reads the same nodes but builds Havok
    /// constraint instances for a ragdoll, never NIF blocks (see
    /// `docs/hkx-constraint-spec.md` §3–4). So the round trip is the only thing that
    /// can say whether a constraint came back, and most of what follows is one.
    /// </remarks>
    public class ConstraintImportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        private static readonly Dictionary<string, FbxDocument> Exported = new(StringComparer.Ordinal);

        /// <summary>The exported scene, cached: the chain fixture is not cheap.</summary>
        private static FbxDocument Export(string name)
        {
            lock (Exported)
            {
                if (!Exported.TryGetValue(name, out FbxDocument? document))
                    Exported[name] = document = new NifToFbx(Load(name)).Convert();

                return document;
            }
        }

        private static (NifModel Model, List<string> Warnings) RoundTrip(string name)
        {
            var converter = new FbxToNif(
                new FbxScene(Export(name)),
                new FbxToNifOptions { RootName = "test" });

            return (converter.Convert(Db), converter.Warnings);
        }

        // --- reading the scene -------------------------------------------------

        [Fact]
        public void AttachmentPointsAreFoundByTheirSeparator()
        {
            var constraints = new FbxScene(Export("TestNifFile_DeepGraph_SE.nif")).ReadConstraints();

            ConstraintImport constraint = Assert.Single(constraints);

            // The second half of the name is the body that owned the constraint; the
            // first repeats the parent's own name (spec 3.1).
            Assert.Equal("PegRight01_rb", constraint.OwnerName);
            Assert.Equal("RopeL01_rb", constraint.OtherName);
            Assert.Equal("BallSocketConstraintChain", constraint.Type);
        }

        [Fact]
        public void TheWholeDescriptorIsRecovered()
        {
            var constraints = new FbxScene(Export("TestNifFile_Furniture_Col_SE.nif")).ReadConstraints();

            ConstraintImport constraint = Assert.Single(constraints);

            Assert.True(constraint.HasFields);
            Assert.Equal("0.062033348", constraint.Fields["length"]);

            // The wrapper is a block of its own and the type property names what is
            // inside it, so without this the breakable constraint is lost.
            Assert.Equal("StiffSpring", constraint.Type);
            Assert.Equal("bhkBreakableConstraint", constraint.Wrapper);
        }

        // --- rebuilding --------------------------------------------------------

        [Theory]
        [InlineData("TestNifFile_Furniture_Col_SE.nif", "bhkBreakableConstraint")]
        [InlineData("TestNifFile_DeepGraph_SE.nif", "bhkBallSocketConstraintChain")]
        public void ConstraintsComeBackAsTheirOwnBlockType(string file, string block)
        {
            (NifModel model, List<string> warnings) = RoundTrip(file);

            // HKXWrangler turns everything but a ragdoll into a limited hinge, which
            // would make the corpus's stiff spring a hinge nobody authored.
            Assert.Contains(model.Blocks, b => b.Name == block);
            Assert.Empty(warnings);
        }

        [Fact]
        public void ConstraintsAreListedByTheBodyThatOwnsThem()
        {
            (NifModel model, _) = RoundTrip("TestNifFile_Furniture_Col_SE.nif");

            NifItem constraint = model.Blocks.First(b => b.Name == "bhkBreakableConstraint");

            var owners = model.Blocks
                .Where(b => model.GetRefArray(b, "Constraints").Contains(constraint))
                .ToList();

            // A constraint nothing lists is a block in the file and a joint in
            // nothing: the engine reaches it through its body's array.
            NifItem owner = Assert.Single(owners);

            Assert.Equal(owner, model.GetBlock(model.FindItem(constraint, "Entity A")!));
        }

        [Fact]
        public void DescriptorValuesSurviveTheRoundTrip()
        {
            NifModel before = Load("TestNifFile_Furniture_Col_SE.nif");

            NifItem source = before.ConstraintDescriptor(
                before.Blocks.First(b => b.Name == "bhkBreakableConstraint"))!;

            (NifModel after, _) = RoundTrip("TestNifFile_Furniture_Col_SE.nif");

            NifItem rebuilt = after.ConstraintDescriptor(
                after.Blocks.First(b => b.Name == "bhkBreakableConstraint"))!;

            AssertVector(before, source, after, rebuilt, "Pivot A");
            AssertVector(before, source, after, rebuilt, "Pivot B");

            Assert.Equal(
                before.FindItem(source, "Length")!.Value.ToFloat(),
                after.FindItem(rebuilt, "Length")!.Value.ToFloat(), 6);
        }

        private static void AssertVector(
            NifModel before, NifItem source, NifModel after, NifItem rebuilt, string field)
        {
            NifVector4 a = before.FindItem(source, field)!.Value.Get<NifVector4>();
            NifVector4 b = after.FindItem(rebuilt, field)!.Value.Get<NifVector4>();

            Assert.Equal(a.X, b.X, 6);
            Assert.Equal(a.Y, b.Y, 6);
            Assert.Equal(a.Z, b.Z, 6);
        }

        [Fact]
        public void TheWrappersOwnSettingsComeBackToo()
        {
            (NifModel model, _) = RoundTrip("TestNifFile_Furniture_Col_SE.nif");

            NifItem constraint = model.Blocks.First(b => b.Name == "bhkBreakableConstraint");

            // How much force breaks it is not part of the descriptor and would be
            // lost with the wrapper it belongs to.
            Assert.Equal(20f, model.FindItem(constraint, "Threshold")!.Value.ToFloat(), 4);

            const uint StiffSpring = 8;
            Assert.Equal(StiffSpring, model.GetUInt(constraint, @"Constraint Data\Type"));
        }

        [Fact]
        public void ChainPivotsComeBack()
        {
            NifModel before = Load("TestNifFile_DeepGraph_SE.nif");

            NifItem source = before.Blocks.First(b => b.Name == "bhkBallSocketConstraintChain");
            var expected = before.FindItem(source, "Pivots")!.Children;

            (NifModel after, _) = RoundTrip("TestNifFile_DeepGraph_SE.nif");

            NifItem rebuilt = after.Blocks.First(b => b.Name == "bhkBallSocketConstraintChain");
            var actual = after.FindItem(rebuilt, "Pivots")!.Children;

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
                AssertVector(before, expected[i], after, actual[i], "Pivot A");

            // The array is sized by a count that has to be written first, so this is
            // also what says the two happened in the right order.
            Assert.Equal(
                before.GetUInt(source, "Num Pivots"),
                after.GetUInt(rebuilt, "Num Pivots"));
        }

        [Fact]
        public void AttachmentPointsDoNotBecomeNodes()
        {
            (NifModel model, _) = RoundTrip("TestNifFile_Furniture_Col_SE.nif");

            // The node is a marker for where a joint is, not a bone: left as a NiNode
            // it would show up in the tree and, being empty, move nothing.
            Assert.DoesNotContain(model.Blocks, b =>
                model.GetName(b).Contains(FbxConstraintWriter.NameSeparator, StringComparison.Ordinal));
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            (NifModel model, _) = RoundTrip("TestNifFile_Furniture_Col_SE.nif");

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Contains(reloaded.Blocks, b => b.Name == "bhkBreakableConstraint");
        }

        [Fact]
        public void ImportCanBeTurnedOff()
        {
            NifModel model = new FbxToNif(
                new FbxScene(Export("TestNifFile_Furniture_Col_SE.nif")),
                new FbxToNifOptions { RootName = "test", ImportConstraints = false }).Convert(Db);

            Assert.DoesNotContain(model.Blocks, b => b.Name.Contains("Constraint", StringComparison.Ordinal));
        }

        // --- scenes that came from ck-cmd --------------------------------------

        /// <summary>
        /// An attachment point as FBXWrangler writes one: a type, six limits, and a
        /// placement, with none of se-cmd's own descriptor properties.
        /// </summary>
        private static FbxScene LegacyScene(string type, params (string Name, string Value)[] limits)
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "root", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject body = FbxMeshWriter.AddModel(scene, "Bone01_rb", "Null", NifTransform.Identity);
            scene.Connect(body, root);

            FbxObject shape = FbxMeshWriter.AddModel(scene, "Bone01_sphere", "Null", NifTransform.Identity);
            scene.Connect(shape, body);
            scene.Connect(FbxMeshWriter.AddGeometry(scene, "Bone01_sphere", Sphere()), shape);

            FbxObject other = FbxMeshWriter.AddModel(scene, "Bone02_rb", "Null", NifTransform.Identity);
            scene.Connect(other, root);

            FbxObject otherShape = FbxMeshWriter.AddModel(scene, "Bone02_sphere", "Null", NifTransform.Identity);
            scene.Connect(otherShape, other);
            scene.Connect(FbxMeshWriter.AddGeometry(scene, "Bone02_sphere", Sphere()), otherShape);

            FbxObject point = FbxMeshWriter.AddModel(
                scene,
                $"Bone02_rb{FbxConstraintWriter.NameSeparator}Bone01_rb{FbxConstraintWriter.NameSuffix}",
                "Null",
                new NifTransform(new NifVector3(1f, 2f, 3f), NifMatrix33.Identity, 1f));

            scene.Connect(point, other);
            point.Properties.SetUserString(FbxConstraintWriter.TypeProperty, type);

            foreach ((string name, string value) in limits)
                point.Properties.SetUserString(name, value);

            scene.Flush();
            return new FbxScene(document);
        }

        private static MeshGeometry Sphere() => ShapeTessellator.Sphere(1f);

        [Fact]
        public void ScenesFromCkCmdImportFromTheirLimitsAlone()
        {
            FbxScene scene = LegacyScene("Ragdoll",
                ("coneMaxAngle", "0.75"),
                ("twistMinAngle", "-0.5"),
                ("twistMaxAngle", "0.5"),
                ("maxFriction", "10"));

            var converter = new FbxToNif(scene, new FbxToNifOptions { RootName = "test" });
            NifModel model = converter.Convert(Db);

            NifItem constraint = Assert.Single(model.Blocks, b => b.Name == "bhkRagdollConstraint");
            NifItem descriptor = model.ConstraintDescriptor(constraint)!;

            // All ck-cmd's own importer ever had, so it has to be enough here too.
            Assert.Equal(0.75f, model.FindItem(descriptor, "Cone Max Angle")!.Value.ToFloat(), 4);
            Assert.Equal(-0.5f, model.FindItem(descriptor, "Twist Min Angle")!.Value.ToFloat(), 4);
            Assert.Equal(10f, model.FindItem(descriptor, "Max Friction")!.Value.ToFloat(), 4);
        }

        [Fact]
        public void LegacyPlacementBecomesThePivot()
        {
            FbxScene scene = LegacyScene("Ragdoll");

            NifModel model = new FbxToNif(scene, new FbxToNifOptions { RootName = "test" }).Convert(Db);

            NifItem descriptor = model.ConstraintDescriptor(
                model.Blocks.First(b => b.Name == "bhkRagdollConstraint"))!;

            NifVector4 pivot = model.FindItem(descriptor, "Pivot B")!.Value.Get<NifVector4>();

            // The node's placement is in Skyrim units and the pivot is in Havok's
            // metres, so the scale goes back the other way here.
            Assert.Equal(1f / ShapeTessellator.BhkScaleFactor, pivot.X, 2);
            Assert.Equal(2f / ShapeTessellator.BhkScaleFactor, pivot.Y, 2);
            Assert.Equal(3f / ShapeTessellator.BhkScaleFactor, pivot.Z, 2);
        }

        [Fact]
        public void LegacyHingesKeepTheirLimits()
        {
            FbxScene scene = LegacyScene("LimitedHinge",
                ("minAngle", "-1.25"),
                ("maxAngle", "1.25"),
                ("maxFriction", "3"));

            NifModel model = new FbxToNif(scene, new FbxToNifOptions { RootName = "test" }).Convert(Db);

            NifItem descriptor = model.ConstraintDescriptor(
                model.Blocks.First(b => b.Name == "bhkLimitedHingeConstraint"))!;

            Assert.Equal(-1.25f, model.FindItem(descriptor, "Min Angle")!.Value.ToFloat(), 4);
            Assert.Equal(1.25f, model.FindItem(descriptor, "Max Angle")!.Value.ToFloat(), 4);
        }

        [Fact]
        public void UnknownTypesAreReportedRatherThanGuessed()
        {
            FbxScene scene = LegacyScene("SomethingElse");

            var converter = new FbxToNif(scene, new FbxToNifOptions { RootName = "test" });
            NifModel model = converter.Convert(Db);

            // Guessing a limited hinge here, as HKXWrangler does, would put a joint
            // in the file that nobody authored and nothing would question.
            Assert.DoesNotContain(model.Blocks, b => b.Name.Contains("Constraint", StringComparison.Ordinal));
            Assert.Contains(converter.Warnings, w => w.Contains("SomethingElse", StringComparison.Ordinal));
        }

        [Fact]
        public void ConstraintsNamingAMissingBodyAreReported()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "root", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject point = FbxMeshWriter.AddModel(
                scene,
                $"A_rb{FbxConstraintWriter.NameSeparator}Nowhere_rb{FbxConstraintWriter.NameSuffix}",
                "Null",
                NifTransform.Identity);

            scene.Connect(point, root);
            point.Properties.SetUserString(FbxConstraintWriter.TypeProperty, "Ragdoll");
            scene.Flush();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions { RootName = "test" });
            converter.Convert(Db);

            Assert.Contains(converter.Warnings, w => w.Contains("Nowhere_rb", StringComparison.Ordinal));
        }
    }
}
