namespace SECmd.Nif
{
    /// <summary>
    /// A node in the tree a NIF is decoded into: one field, one array, one struct,
    /// or one block.
    /// </summary>
    /// <remarks>
    /// Items carry a shared <see cref="NifFieldDef"/> rather than their own copy of
    /// the descriptor. Array elements and template substitutions need a *modified*
    /// descriptor, so one derived def is built per array or substitution and shared
    /// by every item that uses it — the same allocation pattern NifSkope uses, and
    /// the reason a 200k-field NIF does not compile 200k expressions.
    ///
    /// Condition results are cached per item, because evaluating them walks up the
    /// tree and re-resolves sibling names.
    /// </remarks>
    public sealed class NifItem
    {
        private bool? _condition;
        private bool? _versionCondition;

        public NifItem(NifFieldDef def, NifItem? parent)
        {
            Def = def;
            Parent = parent;
        }

        /// <summary>The descriptor this item was created from, possibly derived.</summary>
        public NifFieldDef Def { get; internal set; }

        public NifItem? Parent { get; }

        public List<NifItem> Children { get; } = [];

        /// <summary>The item's own data. Branches (arrays, structs, blocks) leave this as None.</summary>
        public NifValue Value;

        /// <summary>This item's index within its parent.</summary>
        public int Row { get; internal set; }

        public string Name => Def.Name;

        public string Type => Def.Type;

        public string Template => Def.Template;

        public string Arg => Def.Arg;

        public bool IsArray => Def.IsArray;

        public bool IsMultiArray => Def.IsMultiArray;

        public bool IsCompound => Def.IsCompound;

        public bool IsBinary => Def.IsBinary;

        public bool IsAbstract => Def.IsAbstract;

        public bool IsConditionless => Def.IsConditionless;

        public bool HasChildren => Children.Count > 0;

        public bool IsCount => Value.IsCount;

        public bool IsFloat => Value.IsFloat;

        public bool IsLink => Value.IsLink;

        public bool IsFileVersion => Value.Type == NifValueType.FileVersion;

        /// <summary>
        /// The item's value as an integer, for the count-like and version types that
        /// conditions and array lengths are allowed to refer to.
        /// </summary>
        public uint CountValue => Value.ToUInt();

        // --- tree --------------------------------------------------------

        public NifItem AddChild(NifItem child)
        {
            child.Row = Children.Count;
            Children.Add(child);
            return child;
        }

        public NifItem? Child(int index) =>
            index >= 0 && index < Children.Count ? Children[index] : null;

        public void RemoveChildrenFrom(int index)
        {
            if (index < Children.Count)
                Children.RemoveRange(index, Children.Count - index);
        }

        /// <summary>The first child with this name, ignoring conditions.</summary>
        public NifItem? ChildByName(string name)
        {
            foreach (NifItem child in Children)
            {
                if (string.Equals(child.Name, name, StringComparison.Ordinal))
                    return child;
            }

            return null;
        }

        public bool IsDescendantOf(NifItem? ancestor)
        {
            for (NifItem? p = Parent; p is not null; p = p.Parent)
            {
                if (ReferenceEquals(p, ancestor))
                    return true;
            }

            return false;
        }

        // --- condition caching -------------------------------------------

        internal bool? CachedCondition => _condition;

        internal bool? CachedVersionCondition => _versionCondition;

        internal void SetCondition(bool value) => _condition = value;

        internal void SetVersionCondition(bool value) => _versionCondition = value;

        /// <summary>
        /// Drops this item's cached condition. Needed whenever a field that a later
        /// condition refers to has just been read.
        /// </summary>
        public void InvalidateCondition() => _condition = null;

        public void InvalidateVersionCondition() => _versionCondition = null;

        /// <summary>Drops cached conditions for this item and everything under it.</summary>
        public void InvalidateConditionsRecursive()
        {
            _condition = null;
            _versionCondition = null;

            foreach (NifItem child in Children)
                child.InvalidateConditionsRecursive();
        }

        public override string ToString() =>
            HasChildren || Value.Type == NifValueType.None
                ? $"{Name} : {Type}"
                : $"{Name} : {Type} = {Value}";
    }
}
