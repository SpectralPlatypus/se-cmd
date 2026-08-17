using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// BSVertexDesc, decoded against values taken from real Skyrim SE meshes.
    /// </summary>
    /// <remarks>
    /// The layout is not documented in nif.xml, which treats the field as an opaque
    /// bitfield, so these constants come from nifly's SE fixtures and are the only
    /// thing pinning the packing down.
    /// </remarks>
    public class VertexDescTests
    {
        // TestNifFile_Static_SE.nif: position, UV, normal and tangent, stride 28.
        private const ulong StaticSe = 0x1B00000650407;

        // TestNifFile_Skinned_SE.nif: the same plus skinning, stride 40.
        private const ulong SkinnedSe = 0x5B0007065040A;

        [Fact]
        public void DecodesAStaticMeshDescriptor()
        {
            var desc = new BSVertexDesc(StaticSe);

            Assert.Equal(28u, desc.VertexSize);
            Assert.Equal(VertexFlags.Vertex | VertexFlags.UV | VertexFlags.Normal | VertexFlags.Tangent, desc.Flags);

            // Position first, then the bitangent's X lane, so UV starts at 16.
            Assert.Equal(0u, desc.VertexOffset);
            Assert.Equal(16u, desc.UVOffset);
            Assert.Equal(20u, desc.NormalOffset);
            Assert.Equal(24u, desc.TangentOffset);
        }

        [Fact]
        public void DecodesASkinnedMeshDescriptor()
        {
            var desc = new BSVertexDesc(SkinnedSe);

            Assert.Equal(40u, desc.VertexSize);
            Assert.True(desc.HasFlag(VertexFlags.Skinned));

            Assert.Equal(16u, desc.UVOffset);
            Assert.Equal(20u, desc.NormalOffset);
            Assert.Equal(24u, desc.TangentOffset);

            // Bone weights and indices follow the tangent: four halves and four
            // bytes, which is the twelve bytes taking the stride from 28 to 40.
            Assert.Equal(28u, desc.SkinningOffset);
        }

        [Fact]
        public void OffsetsRoundTrip()
        {
            var desc = new BSVertexDesc();

            desc.VertexSize = 40;
            desc.Flags = VertexFlags.Vertex | VertexFlags.UV | VertexFlags.Normal;
            desc.SetOffsetOf(VertexAttribute.TexCoord0, 16);
            desc.SetOffsetOf(VertexAttribute.Normal, 20);

            Assert.Equal(40u, desc.VertexSize);
            Assert.Equal(16u, desc.UVOffset);
            Assert.Equal(20u, desc.NormalOffset);
            Assert.Equal(VertexFlags.Vertex | VertexFlags.UV | VertexFlags.Normal, desc.Flags);
        }

        [Fact]
        public void FlagsLiveAboveTheOffsets()
        {
            // Setting flags must not disturb the stride or the offsets below them.
            var desc = new BSVertexDesc(StaticSe);
            uint stride = desc.VertexSize;

            desc.Flags = VertexFlags.Vertex;

            Assert.Equal(stride, desc.VertexSize);
            Assert.Equal(16u, desc.UVOffset);
        }
    }
}
