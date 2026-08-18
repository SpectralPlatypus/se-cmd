using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries which kind of NIF node an FBX node stands for.
    /// </summary>
    /// <remarks>
    /// FBX has one kind of node, and NIF has a dozen that differ in what the engine
    /// does with them rather than in where they sit: a <c>BSOrderedNode</c> draws its
    /// children in a fixed order, a <c>BSMultiBoundNode</c> carries its own culling
    /// volume, a <c>BSLeafAnimNode</c> is a tree. Rebuilding all of them as
    /// <c>NiNode</c> loses that with nothing to show for it — the scene still has the
    /// right shape, and the engine treats it differently.
    ///
    /// The root matters most. <c>BSXFlags</c> asks twice whether the root is exactly
    /// <c>NiNode</c>, once for the external-skeleton test behind bit 0 and once for the
    /// root-collision test behind bit 3, so a body part whose root is rebuilt as
    /// <c>BSFadeNode</c> comes back claiming animation it does not have.
    /// </remarks>
    public static class FbxNodeType
    {
        /// <summary>The property the block type travels in.</summary>
        public const string Property = "nif_block_type";

        /// <summary>Records which block an exported node came from.</summary>
        public static void Write(FbxObject node, NifItem block) =>
            node.Properties.SetUserString(Property, block.Name);

        /// <summary>
        /// The block type to rebuild a node as.
        /// </summary>
        /// <remarks>
        /// A name only wins when the schema knows it and it really is a node, so a
        /// scene from elsewhere — or one whose property has been edited into something
        /// else — cannot turn a node into a shape or a controller.
        /// </remarks>
        public static string Read(FbxObject node, NifModel model, string fallback)
        {
            string name = node.Properties.GetString(Property);

            if (name.Length == 0 || !model.KnowsBlock(name) || !model.Database.Inherits(name, "NiNode"))
                return fallback;

            return name;
        }
    }
}
