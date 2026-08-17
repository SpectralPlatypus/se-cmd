using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Builds the blocks that describe a skin.
    /// </summary>
    /// <remarks>
    /// A skinned shape needs three blocks: a <c>BSDismemberSkinInstance</c> naming
    /// the bones, a <c>NiSkinData</c> holding the bind pose and the per-bone
    /// weights, and a <c>NiSkinPartition</c> holding the same weights arranged per
    /// vertex for the renderer.
    ///
    /// The partition is not optional. Skyrim renders skinned geometry from it, so a
    /// shape with weights only in the skin data draws unskinned — which looks like
    /// the mesh ignoring its skeleton entirely.
    /// </remarks>
    public static class NifSkinWriter
    {
        /// <summary>Skyrim reads at most four bone influences per vertex.</summary>
        public const int MaxInfluences = 4;

        /// <summary>
        /// Writes a skin for a shape, given the bone nodes it refers to.
        /// </summary>
        /// <param name="boneNodes">The skeleton nodes, by name.</param>
        /// <returns>Names of bones that had no node, whose influence was dropped.</returns>
        public static List<string> WriteSkin(
            this NifModel model,
            NifItem shape,
            SkinData skin,
            IReadOnlyDictionary<string, NifItem> boneNodes,
            NifItem skeletonRoot,
            int vertexCount)
        {
            var missing = new List<string>();

            // Drop influences Skyrim cannot read, renormalising what is left.
            skin.LimitInfluences(MaxInfluences);

            var bones = new List<SkinBone>();
            var nodes = new List<NifItem>();

            foreach (SkinBone bone in skin.Bones)
            {
                if (!boneNodes.TryGetValue(bone.Name, out NifItem? node))
                {
                    if (bone.Weights.Count > 0)
                        missing.Add(bone.Name);

                    continue;
                }

                bones.Add(bone);
                nodes.Add(node);
            }

            if (bones.Count == 0)
                return missing;

            NifItem instance = model.InsertBlock("BSDismemberSkinInstance");
            NifItem data = model.InsertBlock("NiSkinData");
            NifItem partition = model.InsertBlock("NiSkinPartition");

            model.SetRef(instance, "Data", data);
            model.SetRef(instance, "Skin Partition", partition);
            model.SetRef(instance, "Skeleton Root", skeletonRoot);

            if (model.SetArraySize(instance, "Num Bones", "Bones", nodes.Count) is { } boneRefs)
            {
                for (int i = 0; i < nodes.Count && i < boneRefs.Children.Count; i++)
                    boneRefs.Children[i].Value.SetLink(model.IndexOf(nodes[i]));
            }

            // One body-part entry per partition, which is what makes this a
            // dismember instance rather than a plain skin.
            if (model.SetArraySize(instance, "Num Partitions", "Partitions", 1) is { Children.Count: > 0 } parts)
                model.FindItem(parts.Children[0], "Body Part")?.Value.SetCount(0);

            WriteSkinData(model, data, skin, bones);
            WriteSkinPartition(model, partition, skin, bones, vertexCount);

            // BSTriShape names the field Skin, NiGeometry names it Skin Instance.
            if (model.FindItem(shape, "Skin Instance") is not null)
                model.SetRef(shape, "Skin Instance", instance);
            else
                model.SetRef(shape, "Skin", instance);

            return missing;
        }

        /// <summary>Writes the bind pose and the per-bone weights.</summary>
        private static void WriteSkinData(NifModel model, NifItem data, SkinData skin, List<SkinBone> bones)
        {
            WriteTransform(model, data, "Skin Transform", skin.SkinTransform);

            model.FindItem(data, "Has Vertex Weights")?.Value.SetCount(1);

            if (model.SetArraySize(data, "Num Bones", "Bone List", bones.Count) is not { } boneList)
                return;

            for (int i = 0; i < bones.Count && i < boneList.Children.Count; i++)
            {
                NifItem entry = boneList.Children[i];
                SkinBone bone = bones[i];

                WriteTransform(model, entry, "Skin Transform", bone.SkinTransform);

                if (model.SetArraySize(entry, "Num Vertices", "Vertex Weights", bone.Weights.Count)
                    is not { } weights)
                {
                    continue;
                }

                for (int w = 0; w < bone.Weights.Count && w < weights.Children.Count; w++)
                {
                    NifItem slot = weights.Children[w];
                    model.FindItem(slot, "Index")?.Value.SetCount(bone.Weights[w].Vertex);
                    model.FindItem(slot, "Weight")?.Value.SetFloat(bone.Weights[w].Weight);
                }
            }
        }

        /// <summary>
        /// Writes the partition the renderer uses: the same weights, arranged per
        /// vertex with a fixed four slots each.
        /// </summary>
        /// <remarks>
        /// Everything is emitted as a single partition. Splitting exists to keep a
        /// partition's bone count within what the shader can address, so it only
        /// matters past sixty-odd bones; a single partition is correct below that
        /// and simpler to get right.
        /// </remarks>
        private static void WriteSkinPartition(
            NifModel model, NifItem partition, SkinData skin, List<SkinBone> bones, int vertexCount)
        {
            if (model.SetArraySize(partition, "Num Partitions", "Partitions", 1)
                is not { Children.Count: > 0 } partitions)
            {
                return;
            }

            NifItem entry = partitions.Children[0];
            var byVertex = skin.ByVertex();

            model.FindItem(entry, "Num Vertices")?.Value.SetCount((uint)vertexCount);
            model.FindItem(entry, "Num Bones")?.Value.SetCount((uint)bones.Count);
            model.FindItem(entry, "Num Weights Per Vertex")?.Value.SetCount(MaxInfluences);
            model.FindItem(entry, "Has Vertex Map")?.Value.SetCount(1);
            model.FindItem(entry, "Has Vertex Weights")?.Value.SetCount(1);
            model.FindItem(entry, "Has Bone Indices")?.Value.SetCount(1);
            model.FindItem(entry, "Has Faces")?.Value.SetCount(1);

            // The partition addresses the skin's bones through its own list, so with
            // one partition that list is just the identity.
            if (model.SetArraySize(entry, "Num Bones", "Bones", bones.Count) is { } boneList)
            {
                for (int i = 0; i < bones.Count && i < boneList.Children.Count; i++)
                    boneList.Children[i].Value.SetCount((uint)i);
            }

            // With one partition every vertex is present, so the map is the identity
            // too. It still has to be written: the reader uses it to translate the
            // triangle indices.
            if (model.SetArraySize(entry, "Num Vertices", "Vertex Map", vertexCount) is { } map)
            {
                for (int i = 0; i < vertexCount && i < map.Children.Count; i++)
                    map.Children[i].Value.SetCount((uint)i);
            }

            // Both of these are two-dimensional: one row per vertex, each holding
            // Num Weights Per Vertex slots. Sizing the outer array creates the rows
            // but leaves them empty, because writing only ever walks children that
            // already exist, so each row has to be sized too.
            NifItem? weights = SizeGrid(model, entry, "Vertex Weights");
            NifItem? indices = SizeGrid(model, entry, "Bone Indices");

            for (int v = 0; v < vertexCount; v++)
            {
                byVertex.TryGetValue((ushort)v, out List<(int Bone, float Weight)>? influences);

                for (int slot = 0; slot < MaxInfluences; slot++)
                {
                    bool present = influences is not null && slot < influences.Count;

                    float weight = present ? influences![slot].Weight : 0f;
                    uint bone = present ? (uint)influences![slot].Bone : 0u;

                    if (weights is not null && v < weights.Children.Count
                        && slot < weights.Children[v].Children.Count)
                    {
                        weights.Children[v].Children[slot].Value.SetFloat(weight);
                    }

                    if (indices is not null && v < indices.Children.Count
                        && slot < indices.Children[v].Children.Count)
                    {
                        indices.Children[v].Children[slot].Value.SetCount(bone);
                    }
                }
            }
        }

        /// <summary>
        /// Sizes a two-dimensional array and every row inside it.
        /// </summary>
        private static NifItem? SizeGrid(NifModel model, NifItem parent, string field)
        {
            if (model.FindItem(parent, field) is not { } array)
                return null;

            array.InvalidateConditionsRecursive();
            model.UpdateArraySize(array);

            foreach (NifItem row in array.Children)
                model.UpdateArraySize(row);

            return array;
        }

        /// <summary>Writes an <c>NiTransform</c>, whose parts are stored separately.</summary>
        private static void WriteTransform(NifModel model, NifItem parent, string field, NifTransform transform)
        {
            model.FindItem(parent, $@"{field}\Translation")?.Value.Set(transform.Translation);
            model.FindItem(parent, $@"{field}\Rotation")?.Value.Set(transform.Rotation);
            model.FindItem(parent, $@"{field}\Scale")?.Value.SetFloat(transform.Scale);
        }
    }
}
