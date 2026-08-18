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

        /// <summary>The property counting the modifiers that follow.</summary>
        public const string ModifierCountProperty = "particle_modifiers";

        /// <summary>Prefix on the system block's own fields.</summary>
        public const string SystemPrefix = "nps_";

        /// <summary>Prefix on the data block's fields.</summary>
        public const string DataPrefix = "npsd_";

        /// <summary>Prefix on a modifier's fields, before its index.</summary>
        public const string ModifierPrefix = "npsm_";

        /// <summary>
        /// Fields the node already carries, or that mean nothing outside the file.
        /// </summary>
        /// <remarks>
        /// The name and transform are the node's; the rest are links, which the
        /// codec drops anyway, and the extra data and controller counts that go with
        /// them. A count left behind without the array it sizes would make the
        /// rebuilt block claim references it has not got.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Name", "Translation", "Rotation", "Scale",
            "Num Extra Data List", "Num Modifiers", "Num Properties"
        };

        /// <summary>Whether a block is a particle system this carries.</summary>
        public static bool IsParticleSystem(NifModel model, NifItem block) =>
            model.BlockInherits(block, "NiParticleSystem");

        /// <summary>Writes a particle system onto the node standing for it.</summary>
        public static void AddParticleSystem(FbxObject node, NifModel model, NifItem system)
        {
            node.Properties.SetUserString(TypeProperty, system.Name);

            Write(node, model, system, SystemPrefix);

            if (model.GetRef(system, "Data") is { } data)
            {
                node.Properties.SetUserString(DataTypeProperty, data.Name);
                Write(node, model, data, DataPrefix);
            }

            var modifiers = model.GetRefArray(system, "Modifiers").ToList();

            // The order is the order they run in, so the index is part of the data
            // rather than a way of telling them apart.
            node.Properties.SetUserString(
                ModifierCountProperty, modifiers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < modifiers.Count; i++)
            {
                node.Properties.SetUserString($"{ModifierPrefix}{i}_type", modifiers[i].Name);
                node.Properties.SetUserString(
                    $"{ModifierPrefix}{i}_name", model.GetString(modifiers[i], "Name"));

                Write(node, model, modifiers[i], $"{ModifierPrefix}{i}_");
            }
        }

        private static void Write(FbxObject node, NifModel model, NifItem block, string prefix)
        {
            NifFieldCodec.Write(
                model, block, prefix,
                (name, value) => node.Properties.SetUserString(name, value),
                child => Skipped.Contains(child.Name));
        }
    }
}
