using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// NIF to FBX and back again, for animation.
    /// </summary>
    /// <remarks>
    /// The two formats disagree about how a transform is keyed — NIF keys a whole
    /// vector at a time and FBX keys each axis independently — so the round trip is
    /// not an identity and cannot be asserted as one. What has to survive is the
    /// motion: the same nodes moving through the same values at the same times.
    /// </remarks>
    public class AnimationRoundTripTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        /// <summary>Converts out to FBX and back, through a real save and load.</summary>
        private static (NifModel Model, List<string> Warnings) RoundTrip(
            string name = "TestNifFile_Animated_LE.nif")
        {
            FbxDocument document = new NifToFbx(Load(name)).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var converter = new FbxToNif(
                new FbxScene(FbxDocument.Load(stream)),
                new FbxToNifOptions { RootName = "animated", LegendaryEdition = true });

            return (converter.Convert(Db), converter.Warnings);
        }

        [Fact]
        public void EverySequenceComesBack()
        {
            (NifModel model, List<string> warnings) = RoundTrip();

            Assert.Equal(
                ["mBegin", "mLoop", "mEnd"],
                model.ReadAnimations().Select(s => s.Name));

            Assert.Empty(warnings);
        }

        [Fact]
        public void SequencesReachTheEngineThroughAManager()
        {
            (NifModel model, _) = RoundTrip();

            NifItem manager = Assert.Single(model.Blocks, b => b.Name == "NiControllerManager");

            // A manager nothing points at is a loose block: the engine finds it by
            // walking the root's controller chain and no other way.
            Assert.Equal(manager, model.GetRef(model.Blocks[0], "Controller"));

            Assert.Equal(3, model.GetRefArray(manager, "Controller Sequences").Count());
            Assert.NotNull(model.GetRef(manager, "Object Palette"));
        }

        [Fact]
        public void EveryAnimatedNodeIsAControllerTarget()
        {
            (NifModel model, _) = RoundTrip();

            NifItem controller = Assert.Single(
                model.Blocks, b => b.Name == "NiMultiTargetTransformController");

            var targets = model.GetRefArray(controller, "Extra Targets")
                .Select(model.GetName)
                .ToHashSet(StringComparer.Ordinal);

            // A node the controller does not list stays still however many keys name
            // it, since this is what binds a sequence's tracks to real blocks.
            foreach (AnimSequence sequence in model.ReadAnimations())
            {
                foreach (AnimTrack track in sequence.Tracks)
                    Assert.Contains(track.NodeName, targets);
            }
        }

        [Fact]
        public void PaletteNamesResolveToRealBlocks()
        {
            (NifModel model, _) = RoundTrip();

            NifItem palette = model.GetRef(
                model.Blocks.First(b => b.Name == "NiControllerManager"), "Object Palette")!;

            NifItem objects = model.FindItem(palette, "Objs")!;

            Assert.NotEmpty(objects.Children);

            foreach (NifItem entry in objects.Children)
            {
                NifItem block = model.GetBlock(model.FindItem(entry, "AV Object")!)!;

                // The palette is a name-to-block table, so an entry whose name does
                // not match the block it points at resolves to the wrong node.
                Assert.Equal(model.GetName(block), model.GetString(entry, "Name"));
            }
        }

        [Fact]
        public void EverySequenceHasItsStartAndEndMarkers()
        {
            (NifModel model, _) = RoundTrip();

            foreach (NifItem sequence in model.Blocks.Where(b => b.Name == "NiControllerSequence"))
            {
                NifItem keys = model.GetRef(sequence, "Text Keys")!;
                NifItem list = model.FindItem(keys, "Text Keys")!;

                // Skyrim finds a sequence's bounds by these names; without them the
                // sequence loads and never plays.
                Assert.Equal(
                    ["start", "end"],
                    list.Children.Select(k => model.GetString(k, "Value")));

                Assert.Equal(0f, model.FindItem(list.Children[0], "Time")!.Value.ToFloat());

                Assert.Equal(
                    model.FindItem(sequence, "Stop Time")!.Value.ToFloat(),
                    model.FindItem(list.Children[1], "Time")!.Value.ToFloat(), 4);
            }
        }

        [Fact]
        public void SequencesPlayFromZero()
        {
            (NifModel model, _) = RoundTrip();

            var before = Load("TestNifFile_Animated_LE.nif").ReadAnimations()
                .ToDictionary(s => s.Name, s => s.Stop - s.Start, StringComparer.Ordinal);

            foreach (AnimSequence after in model.ReadAnimations())
            {
                Assert.Equal(0f, after.Start);

                // Where a sequence sat on the source timeline is not something the
                // engine has any use for, but how long it runs is.
                Assert.Equal(before[after.Name], after.Stop, 3);
            }
        }

        [Fact]
        public void KeysKeepTheirTimesAndValues()
        {
            AnimSequence before = Load("TestNifFile_Animated_LE.nif")
                .ReadAnimations().First(s => s.Name == "mBegin");

            (NifModel model, _) = RoundTrip();

            AnimSequence after = model.ReadAnimations().First(s => s.Name == "mBegin");

            AnimTrack from = before.Tracks[0];
            AnimTrack to = Assert.Single(after.Tracks);

            Assert.Equal(from.NodeName, to.NodeName);

            for (int axis = 0; axis < 3; axis++)
            {
                AssertSameCurve(from.Rotation[axis], to.Rotation[axis], "rotation");
                AssertSameCurve(from.Translation[axis], to.Translation[axis], "translation");
            }
        }

        private static void AssertSameCurve(AnimCurve before, AnimCurve after, string what)
        {
            Assert.Equal(before.Keys.Count, after.Keys.Count);

            for (int i = 0; i < before.Keys.Count; i++)
            {
                Assert.Equal(before.Keys[i].Time, after.Keys[i].Time, 3);

                // Rotation makes two conversions on the way out and two back, so the
                // tolerance is on the value, not on the shape of the curve.
                Assert.Equal(before.Keys[i].Value, after.Keys[i].Value, 2);
            }
        }

        [Fact]
        public void RotationSurvivesAsSeparateAxisGroups()
        {
            (NifModel model, _) = RoundTrip();

            const uint XyzRotation = 4;

            var counts = new List<List<int>>();

            foreach (NifItem data in model.Blocks.Where(b => b.Name == "NiTransformData"))
            {
                if (model.FindItem(data, "XYZ Rotations") is not { Children.Count: 3 } groups)
                    continue;

                // FBX keys each Euler axis independently and at its own times.
                // Packing those into quaternions would force one shared timeline and
                // lose any winding past a half turn -- this file spins five.
                Assert.Equal(XyzRotation, model.GetUInt(data, "Rotation Type"));

                // The count field says one for this form; the real counts are in the
                // groups, and a reader that trusts the field finds a single key.
                Assert.Equal(1u, model.GetUInt(data, "Num Rotation Keys"));

                counts.Add([.. groups.Children.Select(g => (int)model.GetUInt(g, "Num Keys"))]);
            }

            // mBegin keys one axis three times and the others once each, which is
            // exactly what a shared timeline would have flattened.
            Assert.Equal([[1, 3, 1], [2, 2, 2], [2, 2, 2]], counts);
        }

        [Fact]
        public void TheResultIsAReadableFile()
        {
            (NifModel model, _) = RoundTrip();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Equal(
                ["mBegin", "mLoop", "mEnd"],
                reloaded.ReadAnimations().Select(s => s.Name));
        }

        [Fact]
        public void AnimationCanBeTurnedOff()
        {
            FbxDocument document = new NifToFbx(Load("TestNifFile_Animated_LE.nif")).Convert();

            NifModel model = new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = "animated", ImportAnimation = false }).Convert(Db);

            Assert.DoesNotContain(model.Blocks, b => b.Name == "NiControllerManager");
        }

        [Fact]
        public void UnanimatedScenesGetNoManager()
        {
            (NifModel model, List<string> warnings) = RoundTrip("TestNifFile_Static_SE.nif");

            // A manager with no sequences would be a block claiming the file is
            // animated when nothing in it moves.
            Assert.DoesNotContain(model.Blocks, b => b.Name == "NiControllerManager");
            Assert.Empty(warnings);
        }
    }
}
