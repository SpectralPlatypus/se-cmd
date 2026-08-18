using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a particle system through FBX as properties on its node.
    /// </summary>
    /// <remarks>
    /// FBX has no emitter, no modifier stack, and nothing that means what
    /// <c>NiPSysCylinderEmitter</c> means, so there is no conversion to make — only
    /// a choice between losing the system and carrying it across intact. ck-cmd
    /// makes the first choice: neither FBXWrangler nor HKXWrangler mentions
    /// particles, and a particle system exported through them comes back as a bare
    /// node.
    ///
    /// There is also no geometry to export. Skyrim's <c>NiPSysData</c> holds no
    /// vertices on disk — the corpus fixture has <c>Vertices = 0</c> and
    /// <c>BS Max Vertices = 18</c>, a capacity for a buffer the engine fills at
    /// runtime. ck-cmd's own NIF converter empties those arrays on purpose when it
    /// upgrades an older file, which is the same fact from the other side.
    ///
    /// So the node stays an empty, with its name, transform and animation, and the
    /// system rides along beside it.
    /// </remarks>
    public static class FbxParticleWriter
    {
        /// <summary>The property naming the particle system's block type.</summary>
        public const string TypeProperty = "particle_system";

        /// <summary>The property naming its data block's type.</summary>
        public const string DataTypeProperty = "particle_data";

        /// <summary>The property naming a modifier node's block type.</summary>
        /// <remarks>
        /// Also what marks the node as a modifier rather than a bone, so that the
        /// import walk does not turn the stack into eleven empty NiNodes.
        /// </remarks>
        public const string ModifierTypeProperty = "particle_modifier";

        /// <summary>The property carrying a modifier's own NIF name.</summary>
        /// <remarks>
        /// Separate from the node's name, which is sanitised for FBX and may have
        /// been renamed in a DCC tool. This is the name a controller binds to.
        /// </remarks>
        public const string ModifierNameProperty = "particle_modifier_name";

        /// <summary>Prefix on the system block's own fields.</summary>
        public const string SystemPrefix = "nps_";

        /// <summary>Prefix on the data block's fields.</summary>
        public const string DataPrefix = "npsd_";

        /// <summary>
        /// Suffix marking a property that names what a link pointed at.
        /// </summary>
        /// <remarks>
        /// A block index means nothing once exported, but the *name* of what it
        /// pointed at survives anything: an emitter object and a gravity object are
        /// named nodes, and a spawn modifier is a named modifier. Resolving by name is
        /// also what this project already does for skin bones, animation targets and
        /// constraint entities, so a particle system is not a special case.
        /// </remarks>
        public const string LinkSuffix = "_ref";

        /// <summary>
        /// Fields the node already carries, or that mean nothing outside the file.
        /// </summary>
        /// <remarks>
        /// The name and transform are the node's, and a count left behind without the
        /// array it sizes would make the rebuilt block claim references it has not
        /// got. A modifier's own name is carried separately, since the node's has been
        /// through FBX's naming rules.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Name", "Translation", "Rotation", "Scale",
            "Num Extra Data List", "Num Modifiers", "Num Properties"
        };

        /// <summary>
        /// Links the rebuild wires up for itself, so naming them would be redundant.
        /// </summary>
        /// <remarks>
        /// The system's own data and modifier list, and each modifier's pointer back
        /// to the system it belongs to. All three follow from the structure being
        /// rebuilt and cannot disagree with it.
        /// </remarks>
        private static readonly HashSet<string> StructuralLinks = new(StringComparer.Ordinal)
        {
            "Data", "Modifiers", "Target"
        };

        /// <summary>Whether a block is a particle system this carries.</summary>
        public static bool IsParticleSystem(NifModel model, NifItem block) =>
            model.BlockInherits(block, "NiParticleSystem");

        /// <summary>
        /// Writes a particle system onto the node standing for it, with its modifier
        /// stack as child nodes.
        /// </summary>
        /// <remarks>
        /// One empty per modifier, in order, rather than one long list of properties
        /// on the system. The stack is then something a rigger can see and reorder in
        /// an outliner, and each modifier's fields are named as the file names them —
        /// <c>frame_count</c> rather than <c>npsm_7_frame_count</c>.
        ///
        /// Sibling order is the stack order. That is the point of putting them in the
        /// tree: moving one is meant to move it in the file too. The engine's own
        /// ordering still comes from each modifier's <c>Order</c> field, which is
        /// carried like any other, with array position breaking its ties.
        /// </remarks>
        public static void AddParticleSystem(
            FbxScene scene, FbxObject node, NifModel model, NifItem system)
        {
            node.Properties.SetUserString(TypeProperty, system.Name);

            Write(node, model, system, SystemPrefix);

            if (model.GetRef(system, "Data") is { } data)
            {
                node.Properties.SetUserString(DataTypeProperty, data.Name);
                Write(node, model, data, DataPrefix);
            }

            foreach (NifItem modifier in model.GetRefArray(system, "Modifiers"))
                AddModifier(scene, node, model, modifier);
        }

        /// <summary>Whether a node stands for a particle modifier.</summary>
        public static bool IsModifierNode(FbxObject node) =>
            node.Properties.GetString(ModifierTypeProperty).Length > 0;

        private static void AddModifier(
            FbxScene scene, FbxObject parent, NifModel model, NifItem modifier)
        {
            string name = model.GetString(modifier, "Name");

            FbxObject node = FbxMeshWriter.AddModel(
                scene,
                NameEncoding.Sanitize(name.Length > 0 ? name : modifier.Name),
                "Null",
                NifTransform.Identity);

            scene.Connect(node, parent);

            node.Properties.SetUserString(ModifierTypeProperty, modifier.Name);
            node.Properties.SetUserString(ModifierNameProperty, name);

            // No prefix: the node is the modifier, so there is nothing to
            // disambiguate it from.
            Write(node, model, modifier, string.Empty);
        }

        private static void Write(FbxObject node, NifModel model, NifItem block, string prefix)
        {
            NifFieldCodec.Write(
                model, block, prefix,
                (name, value) => node.Properties.SetUserString(name, value),
                child => Skipped.Contains(child.Name),
                (name, item) => WriteLink(node, model, block, name, item));
        }

        /// <summary>Records what a link pointed at, by name.</summary>
        private static void WriteLink(
            FbxObject node, NifModel model, NifItem block, string name, NifItem item)
        {
            if (StructuralLinks.Contains(item.Name))
                return;

            // A null link and a link to something nameless are the same thing here:
            // nothing to say, and a blank property would only look like a loss.
            if (model.GetBlock(item) is not { } target)
                return;

            string targetName = model.GetName(target);

            if (targetName.Length > 0)
                node.Properties.SetUserString($"{name}{LinkSuffix}", targetName);
        }
    }
}
