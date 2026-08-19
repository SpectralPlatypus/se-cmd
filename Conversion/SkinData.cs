using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// One bone's influence over a mesh.
    /// </summary>
    public sealed class SkinBone
    {
        public required string Name { get; init; }

        /// <summary>
        /// The bone's transform in the skin's space at bind time.
        /// </summary>
        /// <remarks>
        /// NIF stores this per bone on the <c>NiSkinData</c>, as the transform
        /// taking a vertex from skin space into the bone's space. FBX stores the
        /// inverse idea on the cluster: <c>TransformLink</c> is the bone's world
        /// transform at bind time and <c>Transform</c> the mesh's. Getting the two
        /// confused is the usual cause of a mesh that explodes on the first frame.
        /// </remarks>
        public NifTransform SkinTransform { get; set; } = NifTransform.Identity;

        /// <summary>Vertex index and weight pairs. Zero weights are not stored.</summary>
        public List<(ushort Vertex, float Weight)> Weights { get; } = [];
    }

    /// <summary>
    /// A mesh's skinning: the bones that move it and how strongly.
    /// </summary>
    public sealed class SkinData
    {
        /// <summary>The node the bone transforms are relative to.</summary>
        public string SkeletonRoot { get; set; } = string.Empty;

        /// <summary>
        /// Which skin instance class the shape used.
        /// </summary>
        /// <remarks>
        /// A `BSDismemberSkinInstance` carries body-part partitions on top of a plain
        /// `NiSkinInstance`, and the two are not interchangeable: the dismember form is
        /// what lets armour hide the body under it. Rebuilding every skin as the
        /// dismember form gives that machinery to shapes that never had it, which is
        /// the most common single difference across the game's meshes.
        ///
        /// Empty when the scene did not say, in which case the import picks the form
        /// that suits the edition.
        /// </remarks>
        public string InstanceType { get; set; } = string.Empty;

        /// <summary>The whole skin's transform, usually identity.</summary>
        public NifTransform SkinTransform { get; set; } = NifTransform.Identity;

        public List<SkinBone> Bones { get; } = [];

        public bool IsEmpty => Bones.Count == 0;

        /// <summary>
        /// Weights grouped by vertex, heaviest first, which is the order NIF's
        /// partitions expect.
        /// </summary>
        public Dictionary<ushort, List<(int Bone, float Weight)>> ByVertex()
        {
            var result = new Dictionary<ushort, List<(int, float)>>();

            for (int bone = 0; bone < Bones.Count; bone++)
            {
                foreach ((ushort vertex, float weight) in Bones[bone].Weights)
                {
                    if (weight <= 0f)
                        continue;

                    if (!result.TryGetValue(vertex, out var list))
                        result[vertex] = list = [];

                    list.Add((bone, weight));
                }
            }

            foreach (List<(int Bone, float Weight)> list in result.Values)
                list.Sort((a, b) => b.Weight.CompareTo(a.Weight));

            return result;
        }

        /// <summary>
        /// Drops all but the heaviest <paramref name="maxPerVertex"/> influences and
        /// renormalises so each vertex's weights still sum to one.
        /// </summary>
        /// <remarks>
        /// Skyrim reads four weights per vertex. Leaving more in place does not
        /// error, it just silently ignores the rest — and because the ignored ones
        /// still counted toward the total, every affected vertex ends up
        /// under-weighted and drifts toward the origin.
        /// </remarks>
        public void LimitInfluences(int maxPerVertex = 4)
        {
            var byVertex = ByVertex();
            var kept = new Dictionary<int, List<(ushort Vertex, float Weight)>>();

            foreach ((ushort vertex, List<(int Bone, float Weight)> influences) in byVertex)
            {
                int take = Math.Min(maxPerVertex, influences.Count);
                float total = 0f;

                for (int i = 0; i < take; i++)
                    total += influences[i].Weight;

                if (total <= 0f)
                    continue;

                for (int i = 0; i < take; i++)
                {
                    (int bone, float weight) = influences[i];

                    if (!kept.TryGetValue(bone, out var list))
                        kept[bone] = list = [];

                    list.Add((vertex, weight / total));
                }
            }

            for (int bone = 0; bone < Bones.Count; bone++)
            {
                Bones[bone].Weights.Clear();

                if (kept.TryGetValue(bone, out var list))
                    Bones[bone].Weights.AddRange(list);
            }
        }
    }
}
