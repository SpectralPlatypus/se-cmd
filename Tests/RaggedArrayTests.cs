using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Two-dimensional arrays whose rows are not all the same width.
    /// </summary>
    /// <remarks>
    /// nif.xml describes these as <c>length="Num Strips" width="Strip Lengths"</c>:
    /// there are <c>Num Strips</c> rows, and row <c>n</c> holds
    /// <c>Strip Lengths[n]</c> entries. The width therefore names a *sibling array*,
    /// indexed by the row being sized, which is unlike every other length expression
    /// in the format.
    ///
    /// Getting it wrong is silent. The rows come out empty, the block reads short,
    /// and the failure surfaces as an implausible array size in whatever block
    /// happens to follow.
    /// </remarks>
    public class RaggedArrayTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>A strips block with two strips of the given lengths.</summary>
        private static (NifModel Model, NifItem Data) BuildStrips(uint first, uint second)
        {
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem data = model.InsertBlock("NiTriStripsData");
            model.FindItem(data, "Has Points")!.Value.SetCount(1);

            NifItem lengths = model.SetArraySize(data, "Num Strips", "Strip Lengths", 2)!;
            lengths.Children[0].Value.SetCount(first);
            lengths.Children[1].Value.SetCount(second);

            data.InvalidateConditionsRecursive();

            return (model, data);
        }

        [Fact]
        public void EachRowTakesItsWidthFromItsOwnEntry()
        {
            (NifModel model, NifItem data) = BuildStrips(3, 5);

            NifItem points = model.FindItem(data, "Points")!;
            model.UpdateArraySize(points);

            foreach (NifItem row in points.Children)
                model.UpdateArraySize(row);

            // Not three and three, and not zero and zero: the two strips are
            // different lengths and that is the whole point of the construct.
            Assert.Equal([3, 5], points.Children.Select(r => r.Children.Count));
        }

        [Fact]
        public void TheRowsAreActuallyWritten()
        {
            (NifModel model, NifItem data) = BuildStrips(3, 5);

            model.SetRoots([model.Blocks[0]]);
            model.UpdateHeader();

            uint size = model.FindItem(model.Header, "Block Size")!.Children[1].Value.ToUInt();

            (NifModel empty, _) = BuildStrips(0, 0);
            empty.SetRoots([empty.Blocks[0]]);
            empty.UpdateHeader();

            uint baseline = empty.FindItem(empty.Header, "Block Size")!.Children[1].Value.ToUInt();

            // Eight ushorts of points. Sizing the rows but never emitting them was
            // the other half of the same bug.
            Assert.Equal(baseline + 16, size);
        }

        [Fact]
        public void StripsSurviveASaveAndReload()
        {
            (NifModel model, NifItem data) = BuildStrips(3, 5);

            NifItem points = model.FindItem(data, "Points")!;
            model.UpdateArraySize(points);

            ushort next = 1;

            foreach (NifItem row in points.Children)
            {
                model.UpdateArraySize(row);

                foreach (NifItem point in row.Children)
                    point.Value.SetCount(next++);
            }

            model.SetRoots([model.Blocks[0]]);
            model.UpdateHeader();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);
            NifItem rebuilt = reloaded.FindItem(reloaded.Blocks[1], "Points")!;

            Assert.Equal([3, 5], rebuilt.Children.Select(r => r.Children.Count));

            Assert.Equal(
                [1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u],
                rebuilt.Children.SelectMany(r => r.Children).Select(p => p.Value.ToUInt()));
        }

        [Fact]
        public void ASiblingArrayIsIndexedRatherThanReadAsAValue()
        {
            // An array item carries no value of its own, and an unset value reads as
            // a count of zero -- which is what every ragged row used to get.
            (NifModel model, NifItem data) = BuildStrips(3, 5);

            NifItem lengths = model.FindItem(data, "Strip Lengths")!;

            Assert.True(lengths.IsArray);
            Assert.Equal(0u, lengths.Value.ToUInt());
            Assert.Equal(3u, lengths.Children[0].Value.ToUInt());
        }
    }
}
