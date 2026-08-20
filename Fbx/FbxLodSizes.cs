using System.Globalization;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a <c>BSLODTriShape</c>'s level-of-detail triangle counts.
    /// </summary>
    /// <remarks>
    /// A `BSLODTriShape` does not hold three meshes. It holds one triangle list,
    /// partitioned: the first `LOD0 Size` triangles are the nearest level, the next
    /// `LOD1 Size` the one after, and so on, and the engine draws a prefix of the list
    /// according to distance.
    ///
    /// So the counts are the whole of the mechanism, and FBX has nowhere to put them.
    /// Rebuilding the class without them gives a shape whose every level is zero
    /// triangles long — present, correct in every other respect, and invisible.
    ///
    /// Vanilla uses them for plants: all 34 of them, where a distant shrub drops to a
    /// handful of triangles.
    /// </remarks>
    public static class FbxLodSizes
    {
        /// <summary>The fields carried, in the order the shape stores them.</summary>
        private static readonly string[] Fields = ["LOD0 Size", "LOD1 Size", "LOD2 Size"];

        /// <summary>Prefix on the property each count travels in.</summary>
        public const string Prefix = "lod_size_";

        /// <summary>Records the counts, if this shape has any.</summary>
        public static void Write(FbxObject geometry, NifModel model, NifItem shape)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (model.FindItem(shape, Fields[i]) is { } size)
                {
                    geometry.Properties.SetUserString(
                        $"{Prefix}{i}", size.Value.ToUInt().ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>Puts them back on a rebuilt shape.</summary>
        /// <remarks>
        /// Silent when nothing travelled: a shape authored in a DCC tool has no LOD
        /// groups to describe, and zero counts are what it should have.
        /// </remarks>
        public static void Read(FbxObject geometry, NifModel model, NifItem shape)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (model.FindItem(shape, Fields[i]) is { } size
                    && uint.TryParse(
                        geometry.Properties.GetString($"{Prefix}{i}"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out uint value))
                {
                    size.Value.SetCount(value);
                }
            }
        }
    }
}
