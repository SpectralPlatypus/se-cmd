using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Carrying a particle system through FBX.
    /// </summary>
    /// <remarks>
    /// There is no conversion to make here. FBX has no emitter and nothing that
    /// means what <c>NiPSysCylinderEmitter</c> means, so the choice is between
    /// losing the system and carrying it across intact. ck-cmd loses it: neither
    /// FBXWrangler nor HKXWrangler mentions particles at all.
    ///
    /// There is also no geometry: the fixture's <c>NiPSysData</c> holds
    /// <c>Vertices = 0</c> and <c>BS Max Vertices = 18</c>, a capacity for a buffer
    /// the engine fills at runtime.
    /// </remarks>
    public class ParticleTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const string Fixture = "TestNifFile_Animated_LE.nif";

        private static NifModel Load() =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", Fixture), Db);

        private static FbxDocument? _exported;

        private static FbxDocument Export() => _exported ??= new NifToFbx(Load()).Convert();

        private static (NifModel Model, List<string> Warnings) RoundTrip()
        {
            var converter = new FbxToNif(
                new FbxScene(Export()),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true });

            return (converter.Convert(Db), converter.Warnings);
        }

        private static FbxObject Node() =>
            new FbxScene(Export()).OfClass("Model").First(o => o.Name == "PCloud06");

        // --- exporting ---------------------------------------------------------

        [Fact]
        public void ParticleSystemsStayEmptyNodes()
        {
            var scene = new FbxScene(Export());
            FbxObject node = scene.OfClass("Model").First(o => o.Name == "PCloud06");

            // Nothing to make a mesh out of, so making one would mean inventing
            // eighteen vertices the file never had.
            Assert.DoesNotContain(scene.ChildrenOf(node.Id), o => o.Class == "Geometry");
            Assert.DoesNotContain(scene.OfClass("Geometry"), o => o.Name.Contains("PCloud", StringComparison.Ordinal));
        }

        [Fact]
        public void TheSystemIsTaggedAndCarried()
        {
            FbxObject node = Node();

            Assert.Equal("NiParticleSystem", node.Properties.GetString(FbxParticleWriter.TypeProperty));
            Assert.Equal("NiPSysData", node.Properties.GetString(FbxParticleWriter.DataTypeProperty));
            Assert.Equal("11", node.Properties.GetString(FbxParticleWriter.ModifierCountProperty));
        }

        [Fact]
        public void NodeFieldsAreNotDuplicatedOntoTheProperties()
        {
            FbxObject node = Node();

            // The name and transform are the node's own. Carrying them twice would
            // let the two disagree after an edit, with nothing to say which won.
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}name"));
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}translation"));
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}rotation"));
        }

        [Fact]
        public void LinksAreNotCarriedAsValues()
        {
            FbxObject node = Node();

            string[] prefixes =
            [
                FbxParticleWriter.SystemPrefix,
                FbxParticleWriter.DataPrefix,
                FbxParticleWriter.ModifierPrefix
            ];

            var fields = node.Properties.All
                .Select(p => p.Name)
                .Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .ToList();

            Assert.NotEmpty(fields);

            // A block index means nothing once exported, so a modifier's target or a
            // system's data and shader property are never carried as values. What
            // they pointed at is said by the structure or not at all.
            Assert.DoesNotContain(fields, n =>
                n.EndsWith("_data", StringComparison.Ordinal)
                || n.EndsWith("_target", StringComparison.Ordinal)
                || n.EndsWith("_shader_property", StringComparison.Ordinal)
                || n.EndsWith("_gravity_object", StringComparison.Ordinal));
        }

        // --- rebuilding --------------------------------------------------------

        [Fact]
        public void TheSystemComesBack()
        {
            (NifModel model, List<string> warnings) = RoundTrip();

            NifItem system = Assert.Single(model.Blocks, b => b.Name == "NiParticleSystem");

            Assert.Equal("PCloud06", model.GetName(system));
            Assert.Equal("NiPSysData", model.GetRef(system, "Data")?.Name);
            Assert.Empty(warnings);
        }

        [Fact]
        public void ModifiersComeBackInOrder()
        {
            NifModel before = Load();

            var expected = before.GetRefArray(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Modifiers")
                .Select(m => (m.Name, before.GetName(m)))
                .ToList();

            (NifModel after, _) = RoundTrip();

            var actual = after.GetRefArray(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Modifiers")
                .Select(m => (m.Name, after.GetName(m)))
                .ToList();

            // The order is the order they run in, so it is data rather than a way of
            // telling them apart: gravity before position before bound update.
            Assert.Equal(11, expected.Count);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ModifiersPointBackAtTheirSystem()
        {
            (NifModel model, _) = RoundTrip();

            NifItem system = model.Blocks.First(b => b.Name == "NiParticleSystem");

            // A modifier in the array but attached to nothing is one the engine
            // holds and never runs.
            Assert.All(
                model.GetRefArray(system, "Modifiers"),
                m => Assert.Equal(system, model.GetRef(m, "Target")));
        }

        [Fact]
        public void TheDataBlockKeepsItsValues()
        {
            NifModel before = Load();
            NifItem source = before.GetRef(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;

            (NifModel after, _) = RoundTrip();
            NifItem rebuilt = after.GetRef(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;

            // The vertex buffer's capacity is the whole of what the file says about
            // the particles themselves.
            Assert.Equal(before.GetUInt(source, "BS Max Vertices"), after.GetUInt(rebuilt, "BS Max Vertices"));
            Assert.Equal(18u, after.GetUInt(rebuilt, "BS Max Vertices"));

            Assert.Equal(
                before.FindItem(source, "Aspect Ratio")!.Value.ToFloat(),
                after.FindItem(rebuilt, "Aspect Ratio")!.Value.ToFloat(), 5);

            Assert.Equal(
                before.FindItem(source, "Speed to Aspect Speed 2")!.Value.ToFloat(),
                after.FindItem(rebuilt, "Speed to Aspect Speed 2")!.Value.ToFloat(), 3);
        }

        [Fact]
        public void ArraysInsideTheDataBlockComeBack()
        {
            NifModel before = Load();
            NifItem source = before.GetRef(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;
            var expected = before.FindItem(source, "Subtexture Offsets")!.Children;

            (NifModel after, _) = RoundTrip();
            NifItem rebuilt = after.GetRef(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;
            var actual = after.FindItem(rebuilt, "Subtexture Offsets")!.Children;

            // Sized by a count that has to be written first, so this says the two
            // happened in the right order as well as that the values survived.
            Assert.Equal(16, expected.Count);
            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                NifVector4 a = expected[i].Value.Get<NifVector4>();
                NifVector4 b = actual[i].Value.Get<NifVector4>();

                Assert.Equal(a.X, b.X, 5);
                Assert.Equal(a.Y, b.Y, 5);
                Assert.Equal(a.Z, b.Z, 5);
                Assert.Equal(a.W, b.W, 5);
            }
        }

        [Fact]
        public void ModifierSettingsComeBack()
        {
            NifModel before = Load();

            NifItem emitter = before.Blocks.First(b => b.Name == "NiPSysCylinderEmitter");

            (NifModel after, _) = RoundTrip();

            NifItem rebuilt = after.Blocks.First(b => b.Name == "NiPSysCylinderEmitter");

            foreach (string field in new[] { "Radius", "Height", "Speed", "Declination", "Life Span" })
            {
                Assert.Equal(
                    before.FindItem(emitter, field)!.Value.ToFloat(),
                    after.FindItem(rebuilt, field)!.Value.ToFloat(), 4);
            }
        }

        [Fact]
        public void TheEmittersAnimationStillBinds()
        {
            (NifModel model, _) = RoundTrip();

            AnimTrack track = model.ReadAnimations()
                .First(s => s.Name == "mBegin")
                .Tracks.First(t => t.NodeName == "PCloud06");

            // The system is a node like any other as far as animation goes, and
            // rebuilding it as one had better not have broken that.
            Assert.Equal(
                ["BirthRate", "EmitterActive"],
                track.Properties.Select(p => p.InterpolatorId));
        }

        [Fact]
        public void ParticleSystemsAreNotAlsoNodes()
        {
            (NifModel model, _) = RoundTrip();

            // Emitting both would leave the system parented under a copy of itself,
            // and the name would resolve to whichever came first.
            Assert.DoesNotContain(
                model.Blocks,
                b => b.Name == "NiNode" && model.GetName(b) == "PCloud06");
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            (NifModel model, _) = RoundTrip();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            NifItem system = Assert.Single(reloaded.Blocks, b => b.Name == "NiParticleSystem");
            Assert.Equal(11, reloaded.GetRefArray(system, "Modifiers").Count());
        }

        [Fact]
        public void UnknownBlockTypesAreReportedRatherThanTrusted()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "root", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject node = FbxMeshWriter.AddModel(scene, "Cloud", "Null", NifTransform.Identity);
            scene.Connect(node, root);

            // The type arrives as text from outside the file, so it is not something
            // to take on trust: inserting an unknown block throws.
            node.Properties.SetUserString(FbxParticleWriter.TypeProperty, "NiNotAThing");
            scene.Flush();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions { RootName = "test" });
            NifModel model = converter.Convert(Db);

            Assert.Contains(converter.Warnings, w => w.Contains("NiNotAThing", StringComparison.Ordinal));
            Assert.Contains(model.Blocks, b => b.Name == "NiNode" && model.GetName(b) == "Cloud");
        }
    }
}
