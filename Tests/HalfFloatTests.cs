using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The half-precision encoding the Bethesda vertex formats use.
    /// </summary>
    /// <remarks>
    /// A NIF is a file, not a computation: whatever bit pattern is in it has to come
    /// back out unchanged, whether or not it means a number. Vanilla Skyrim ships
    /// meshes with NaNs in their vertex data, and a NaN is where a numeric conversion
    /// quietly loses information.
    /// </remarks>
    public class HalfFloatTests
    {
        [Fact]
        public void EveryBitPatternSurvivesTheRoundTrip()
        {
            var lost = new List<string>();

            for (int i = 0; i <= ushort.MaxValue; i++)
            {
                ushort half = (ushort)i;
                ushort back = NifPack.FloatToHalf(NifPack.HalfToFloat(half));

                if (back != half)
                    lost.Add($"0x{half:X4} -> 0x{back:X4}");
            }

            // All 65536 of them. Anything less means some file somewhere does not
            // save back as it loaded, and the file that finds out is the user's.
            Assert.Empty(lost);
        }

        [Fact]
        public void NaNPayloadsAreKept()
        {
            // 0x7F7D is what a Skyrim architecture mesh actually contains. Converting
            // through float and back returned 0x7F7F -- still a NaN, so nothing
            // compared unequal, and the file simply saved one byte different.
            const ushort Sample = 0x7F7D;

            float f = NifPack.HalfToFloat(Sample);

            Assert.True(float.IsNaN(f));
            Assert.Equal(Sample, NifPack.FloatToHalf(f));
        }

        [Fact]
        public void OrdinaryValuesAreStillOrdinary()
        {
            // The NaN handling must not disturb the arithmetic path.
            foreach (float value in new[] { 0f, 1f, -1f, 0.5f, -0.25f, 65504f, -65504f })
                Assert.Equal(value, NifPack.HalfToFloat(NifPack.FloatToHalf(value)));

            Assert.True(float.IsPositiveInfinity(NifPack.HalfToFloat(0x7C00)));
            Assert.True(float.IsNegativeInfinity(NifPack.HalfToFloat(0xFC00)));

            Assert.Equal((ushort)0x7C00, NifPack.FloatToHalf(float.PositiveInfinity));
            Assert.Equal((ushort)0xFC00, NifPack.FloatToHalf(float.NegativeInfinity));
        }

        [Fact]
        public void AFloatNaNFromElsewhereStaysANaN()
        {
            // A NaN that did not come from a half has nothing in the ten bits this
            // encoding reads, and must not collapse into an infinity.
            ushort half = NifPack.FloatToHalf(BitConverter.UInt32BitsToSingle(0x7F800001));

            Assert.Equal(0x7C00, half & 0x7C00);
            Assert.NotEqual(0, half & 0x03FF);
            Assert.True(float.IsNaN(NifPack.HalfToFloat(half)));
        }
    }
}
