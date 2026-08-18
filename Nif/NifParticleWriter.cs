using System.Globalization;
using SECmd.Fbx;

namespace SECmd.Nif
{
    /// <summary>
    /// Rebuilds a particle system from the properties on its node.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="FbxParticleWriter"/>. Nothing in ck-cmd does this,
    /// in either direction — a particle system exported through FBXWrangler comes
    /// back as a bare node with its emitter, its data and all of its modifiers gone.
    ///
    /// What cannot be carried is the links *between* blocks: a gravity modifier
    /// points at the node whose position it pulls towards, and a block index means
    /// nothing once exported. Those are left null and reported rather than guessed
    /// at from names, which would attach the wrong node as readily as the right one.
    /// </remarks>
    public static class NifParticleWriter
    {
        /// <summary>Reads what a node says about the particle system it stands for.</summary>
        public static bool HasParticleSystem(FbxObject node) =>
            node.Properties.GetString(FbxParticleWriter.TypeProperty).Length > 0;

        /// <summary>
        /// Builds the system, its data block and its modifiers.
        /// </summary>
        /// <returns>The system block, or null when the node carries none.</returns>
        public static NifItem? WriteParticleSystem(
            this NifModel model, FbxObject node, string name, List<string> warnings)
        {
            string type = node.Properties.GetString(FbxParticleWriter.TypeProperty);

            if (type.Length == 0)
                return null;

            if (!model.KnowsBlock(type))
            {
                warnings.Add($"{name}: unknown particle system type \"{type}\", it is dropped");
                return null;
            }

            var fields = Fields(node);

            NifItem system = model.InsertBlock(type);
            model.SetString(system, "Name", name);

            Read(model, system, fields, FbxParticleWriter.SystemPrefix);

            string dataType = node.Properties.GetString(FbxParticleWriter.DataTypeProperty);

            if (dataType.Length > 0 && model.KnowsBlock(dataType))
            {
                NifItem data = model.InsertBlock(dataType);
                Read(model, data, fields, FbxParticleWriter.DataPrefix);
                model.SetRef(system, "Data", data);
            }

            WriteModifiers(model, node, system, fields, name, warnings);

            return system;
        }

        /// <summary>
        /// Builds the modifier stack, in the order it runs in.
        /// </summary>
        /// <remarks>
        /// Each modifier also points back at the system it belongs to, which is the
        /// one link here worth restoring: without it a modifier is in the array and
        /// attached to nothing.
        /// </remarks>
        private static void WriteModifiers(
            NifModel model, FbxObject node, NifItem system,
            IReadOnlyDictionary<string, string> fields, string name, List<string> warnings)
        {
            int count = Count(node, FbxParticleWriter.ModifierCountProperty);

            if (count <= 0)
                return;

            var built = new List<NifItem>();

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{FbxParticleWriter.ModifierPrefix}{i}_";
                string type = node.Properties.GetString($"{prefix}type");

                if (type.Length == 0 || !model.KnowsBlock(type))
                {
                    warnings.Add($"{name}: unknown particle modifier \"{type}\", it is dropped");
                    continue;
                }

                NifItem modifier = model.InsertBlock(type);

                model.SetString(modifier, "Name", node.Properties.GetString($"{prefix}name"));
                Read(model, modifier, fields, prefix);

                model.SetRef(modifier, "Target", system);
                built.Add(modifier);
            }

            if (model.SetArraySize(system, "Num Modifiers", "Modifiers", built.Count) is not { } array)
                return;

            for (int i = 0; i < built.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetLink(model.IndexOf(built[i]));
        }

        private static void Read(
            NifModel model, NifItem block, IReadOnlyDictionary<string, string> fields, string prefix)
        {
            NifFieldCodec.Read(
                model, block, prefix,
                name => fields.GetValueOrDefault(name),

                // The name is the node's, and the counts are rewritten from what was
                // actually rebuilt rather than from what the source had.
                child => child.Name is "Name" or "Num Extra Data List" or "Num Modifiers" or "Num Properties");
        }

        /// <summary>Every user property on the node, by name.</summary>
        private static Dictionary<string, string> Fields(FbxObject node)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (FbxProperty70 property in node.Properties.All)
            {
                if (property.IsUserDefined && property.Values.Count > 0)
                    fields[property.Name] = property.Values[0]?.ToString() ?? string.Empty;
            }

            return fields;
        }

        private static int Count(FbxObject node, string property) =>
            int.TryParse(
                node.Properties.GetString(property),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}
