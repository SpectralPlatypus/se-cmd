namespace SECmd.Nif
{
    /// <summary>
    /// Puts a file's blocks into the order a NIF is meant to store them in.
    /// </summary>
    /// <remarks>
    /// Block order is not free. A Havok block has to come **before** the block that
    /// references it, which is the reverse of everything else, and a constraint has to
    /// come after the bodies it joins. Every mesh the game ships obeys this; a file
    /// built by walking a scene and appending blocks as it goes does not.
    ///
    /// This is NifSkope's <c>spSanitizeBlockOrder</c>
    /// (`src/spells/sanitize.cpp`), which is the only written-down statement of the
    /// rule there is:
    ///
    /// <list type="bullet">
    /// <item>Walk from the roots. For each block, first emit the referenced blocks that
    /// belong before it — those deriving from <c>bhkRefObject</c> that are not
    /// constraints — then the block, then everything else it references.</item>
    /// <item>A <c>bhkConstraint</c>'s entities come before it. They are pointers rather
    /// than references, so the ordinary walk never reaches them.</item>
    /// </list>
    ///
    /// Reordering means renumbering, so every link in the file is remapped. A block
    /// nothing references keeps its place at the end rather than being dropped: this
    /// reorders a file, it does not prune one.
    /// </remarks>
    public static class NifBlockOrder
    {
        /// <summary>
        /// Whether a block belongs before the block that references it.
        /// </summary>
        /// <remarks>
        /// NifSkope's rule is "a `bhkRefObject` that is not a `bhkConstraint`", and the
        /// game's files say that is not quite it. A
        /// <c>bhkBallSocketConstraintChain</c> inherits <c>bhkSerializable</c> rather
        /// than <c>bhkConstraint</c>, so the rule as written puts it before the body
        /// that references it — and `TestNifFile_DeepGraph_SE.nif` has it after.
        ///
        /// The principle underneath is that a thing which *joins* bodies comes after
        /// them, and a chain joins bodies whatever it inherits from. It carries a
        /// <c>bhkConstraintChainCInfo</c>, which is how one is recognised without
        /// naming every class that might be one.
        /// </remarks>
        public static bool BeforeItsParent(NifModel model, NifItem block) =>
            model.BlockInherits(block, "bhkRefObject")
            && !model.BlockInherits(block, "bhkConstraint")
            && !IsConstraintChain(model, block);

        /// <summary>Whether a block joins a chain of bodies rather than being one.</summary>
        private static bool IsConstraintChain(NifModel model, NifItem block) =>
            model.FindItem(block, "Constraint Chain Info") is not null;

        /// <summary>The order the blocks should be written in.</summary>
        public static List<NifItem> Sorted(NifModel model)
        {
            var ordered = new List<NifItem>(model.Blocks.Count);
            var seen = new HashSet<NifItem>();

            foreach (NifItem root in Roots(model))
                Add(model, root, ordered, seen);

            // Anything the walk did not reach keeps its place, at the end. An
            // unreferenced block is a file's problem to have, not this one's to solve.
            foreach (NifItem block in model.Blocks)
            {
                if (seen.Add(block))
                    ordered.Add(block);
            }

            return ordered;
        }

        private static void Add(NifModel model, NifItem block, List<NifItem> ordered, HashSet<NifItem> seen)
        {
            if (!seen.Add(block))
                return;

            // A constraint's entities are pointers, so the reference walk below never
            // finds them; they still have to exist by the time it is read.
            if (model.BlockInherits(block, "bhkConstraint"))
            {
                foreach (string field in new[] { "Entity A", "Entity B" })
                {
                    if (model.FindItem(block, field) is { } entity && model.GetBlock(entity) is { } target)
                        Add(model, target, ordered, seen);
                }
            }

            var referenced = References(model, block).ToList();

            foreach (NifItem child in referenced.Where(c => BeforeItsParent(model, c)))
                Add(model, child, ordered, seen);

            ordered.Add(block);

            foreach (NifItem child in referenced.Where(c => !BeforeItsParent(model, c)))
                Add(model, child, ordered, seen);
        }

        private static IEnumerable<NifItem> Roots(NifModel model)
        {
            if (model.FindItem(model.Footer, "Roots") is { } roots)
            {
                foreach (NifItem link in roots.Children)
                {
                    if (model.GetBlock(link) is { } root)
                        yield return root;
                }
            }
        }

        /// <summary>
        /// The blocks a block references, in field order.
        /// </summary>
        /// <remarks>
        /// References only. A pointer is the upward half of a two-way link and would
        /// walk back out of the subtree.
        /// </remarks>
        private static IEnumerable<NifItem> References(NifModel model, NifItem block)
        {
            foreach (NifItem link in Links(block))
            {
                if (model.GetBlock(link) is { } target)
                    yield return target;
            }
        }

        private static IEnumerable<NifItem> Links(NifItem item)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Value.Type == NifValueType.Link)
                {
                    yield return child;
                }
                else if (child.Value.Type != NifValueType.UpLink)
                {
                    foreach (NifItem nested in Links(child))
                        yield return nested;
                }
            }
        }
    }
}
