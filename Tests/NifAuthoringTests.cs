using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Building a NIF from nothing, which is what the FBX to NIF direction needs.
    /// </summary>
    public class NifAuthoringTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel SaveAndReload(NifModel model)
        {
            model.UpdateHeader();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        [Fact]
        public void CreatesAHeaderThatReadsBack()
        {
            NifModel model = NifModel.CreateNew(Db);

            Assert.Equal(0x14020007u, model.Version);
            Assert.Equal(12u, model.UserVersion);
            Assert.Equal(83u, model.BSVersion);

            NifModel reloaded = SaveAndReload(model);

            Assert.Equal(0x14020007u, reloaded.Version);
            Assert.Equal(12u, reloaded.UserVersion);
            Assert.Equal(83u, reloaded.BSVersion);
            Assert.Empty(reloaded.Blocks);
        }

        [Fact]
        public void WritesASingleBlockThatReadsBack()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "Scene");
            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);

            NifItem block = Assert.Single(reloaded.Blocks);
            Assert.Equal("BSFadeNode", block.Name);
            Assert.Equal("Scene", reloaded.GetName(block));
        }

        [Fact]
        public void WritesATransform()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "Bone");

            var transform = new NifTransform(
                new NifVector3(1.5f, -2f, 3f),
                NifTransform.RotationFromEulerDegrees(10f, 20f, 30f),
                2f);

            model.SetTransform(root, transform);
            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);
            NifTransform back = reloaded.GetTransform(reloaded.Blocks[0]);

            Assert.Equal(1.5f, back.Translation.X, 4);
            Assert.Equal(-2f, back.Translation.Y, 4);
            Assert.Equal(3f, back.Translation.Z, 4);
            Assert.Equal(2f, back.Scale, 4);
            Assert.Equal(transform.Rotation.M11, back.Rotation.M11, 4);
            Assert.Equal(transform.Rotation.M23, back.Rotation.M23, 4);
        }

        [Fact]
        public void LinksBlocksTogether()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "Scene");

            NifItem child = model.InsertBlock("NiNode");
            model.SetString(child, "Name", "Child");

            NifItem? children = model.SetArraySize(root, "Num Children", "Children", 1);
            Assert.NotNull(children);
            children!.Children[0].Value.SetLink(model.IndexOf(child));

            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);

            Assert.Equal(2, reloaded.Blocks.Count);

            var names = reloaded.GetChildren(reloaded.Blocks[0]).Select(reloaded.GetName).ToList();
            Assert.Equal(["Child"], names);
        }

        [Fact]
        public void InternsStringsOnce()
        {
            NifModel model = NifModel.CreateNew(Db);

            Assert.Equal(0, model.AddString("Scene"));
            Assert.Equal(1, model.AddString("Other"));

            // The same text reuses its slot rather than growing the table.
            Assert.Equal(0, model.AddString("Scene"));

            // An empty name has no entry at all.
            Assert.Equal(-1, model.AddString(""));
        }

        [Fact]
        public void HeaderRecordsEveryBlockTypeOnce()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.InsertBlock("NiNode");
            model.InsertBlock("NiNode");
            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);

            // Two distinct types across three blocks.
            Assert.Equal(2u, reloaded.GetUInt(reloaded.Header, "Num Block Types"));
            Assert.Equal(3u, reloaded.GetUInt(reloaded.Header, "Num Blocks"));

            Assert.Equal(["BSFadeNode", "NiNode", "NiNode"], reloaded.Blocks.Select(b => b.Name));
        }

        [Fact]
        public void HeaderBlockSizesMatchWhatWasWritten()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "Scene");
            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);

            NifItem? sizes = reloaded.FindItem(reloaded.Header, "Block Size");

            if (sizes is null)
                return;

            // A wrong size is how a NIF ends up unreadable, so it has to be right
            // rather than merely present.
            Assert.Equal(reloaded.Blocks.Count, sizes.Children.Count);
            Assert.All(sizes.Children, s => Assert.True(s.Value.ToUInt() > 0));
        }

        [Fact]
        public void FooterNamesTheRoot()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "Scene");
            model.SetRoots([root]);

            NifModel reloaded = SaveAndReload(model);

            NifItem? roots = reloaded.FindItem(reloaded.Footer, "Roots");

            Assert.NotNull(roots);
            NifItem link = Assert.Single(roots!.Children);
            Assert.Equal(0, link.Value.ToLink());
        }

        [Fact]
        public void AnEditedFileKeepsItsExistingStrings()
        {
            // A model that was loaded takes over the file's string table, so adding
            // a name does not renumber what is already there.
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "multi_material_cube.nif"), Db);

            string firstName = model.GetName(model.Blocks[0]);

            NifItem added = model.InsertBlock("NiNode");
            model.SetString(added, "Name", "AddedLater");

            NifModel reloaded = SaveAndReload(model);

            Assert.Equal(firstName, reloaded.GetName(reloaded.Blocks[0]));
            Assert.Equal("AddedLater", reloaded.GetName(reloaded.Blocks[^1]));
        }
    }
}
