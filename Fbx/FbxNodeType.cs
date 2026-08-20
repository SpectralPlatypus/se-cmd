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
    /// The same applies to geometry, where the class decides what the engine does with
    /// the mesh rather than where it sits: a <c>BSDynamicTriShape</c> keeps a second
    /// vertex buffer the engine writes into every frame, which is how a cloak moves.
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

        /// <summary>Prefix on the fields a specialised class adds to its base.</summary>
        public const string FieldPrefix = "nif_own_";

        /// <summary>Records which block an exported node came from.</summary>
        public static void Write(FbxObject node, NifItem block) =>
            node.Properties.SetUserString(Property, block.Name);

        /// <summary>
        /// Records the class *and* the fields it adds to its base.
        /// </summary>
        /// <remarks>
        /// Carrying a class without the thing the class is for is worse than not
        /// carrying it: a <c>BSLODTriShape</c> rebuilt without its triangle counts
        /// draws nothing at any distance, and a <c>BSOrderedNode</c> without its
        /// bound sorts against an empty one.
        ///
        /// Which fields those are is asked of the schema rather than listed here —
        /// everything the class declares that its base does not — so a class nobody
        /// has thought about yet is carried as completely as the ones that have been.
        /// Fields with their own carrier are left out, since two carriers writing the
        /// same field is how one of them ends up losing.
        /// </remarks>
        public static void WriteWithFields(
            FbxObject node, NifModel model, NifItem block, string baseClass, ISet<string>? except = null)
        {
            Write(node, block);

            foreach (NifFieldDef field in OwnFields(model, block.Name, baseClass))
            {
                if (except is not null && except.Contains(field.Name))
                    continue;

                if (model.FindItem(block, field.Name) is { Children.Count: 0 } item)
                    node.Properties.SetUserString(FieldPrefix + field.Name, NifFieldCodec.Format(model, item));
            }
        }

        /// <summary>Puts those fields back on a rebuilt block.</summary>
        public static void ReadFields(
            FbxObject node, NifModel model, NifItem block, string baseClass, ISet<string>? except = null)
        {
            foreach (NifFieldDef field in OwnFields(model, block.Name, baseClass))
            {
                if (except is not null && except.Contains(field.Name))
                    continue;

                if (node.Properties.GetString(FieldPrefix + field.Name) is { Length: > 0 } text
                    && model.FindItem(block, field.Name) is { Children.Count: 0 } item)
                {
                    NifFieldCodec.Assign(model, item, text);
                }
            }
        }

        /// <summary>What a class declares that its base does not.</summary>
        private static IEnumerable<NifFieldDef> OwnFields(NifModel model, string blockName, string baseClass)
        {
            if (!model.KnowsBlock(blockName) || blockName == baseClass)
                return [];

            var inherited = model.Database.GetInheritedFields(baseClass)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            return model.Database.GetInheritedFields(blockName)
                .Where(f => !inherited.Contains(f.Name));
        }

        /// <summary>
        /// The block type to rebuild a node as.
        /// </summary>
        /// <remarks>
        /// A name only wins when the schema knows it and it really is a node, so a
        /// scene from elsewhere — or one whose property has been edited into something
        /// else — cannot turn a node into a shape or a controller.
        /// </remarks>
        public static string Read(FbxObject node, NifModel model, string fallback, string ancestor = "NiNode")
        {
            string name = node.Properties.GetString(Property);

            if (name.Length == 0 || !model.KnowsBlock(name) || !model.Database.Inherits(name, ancestor))
                return fallback;

            return name;
        }
    }
}
