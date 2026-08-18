namespace SECmd.Nif
{
    /// <summary>
    /// Finding a constraint's parameters, whichever way the block holds them.
    /// </summary>
    /// <remarks>
    /// A constraint block names its type one of two ways. Most are a class per type
    /// with the descriptor inline; the wrapped ones — breakable, malleable — carry a
    /// type number and a union, of which exactly one arm is live.
    /// </remarks>
    public static class NifConstraintAccess
    {
        /// <summary>The union holding a wrapped constraint's parameters, if it has one.</summary>
        public static NifItem? ConstraintWrapper(this NifModel model, NifItem constraint) =>
            model.FindItem(constraint, "Constraint Data") ?? model.FindItem(constraint, "Constraint");

        /// <summary>
        /// The item holding the constraint's parameters, which is often the block.
        /// </summary>
        /// <remarks>
        /// A plain constraint's descriptor is inlined: <c>bhkRagdollConstraint</c>'s
        /// pivots and limits are fields of the block itself, with no
        /// <c>Constraint</c> child to descend into. Only the polymorphic wrapper
        /// stays a child of its own, because its arms have to be told apart.
        /// </remarks>
        public static NifItem ConstraintDescriptor(this NifModel model, NifItem constraint)
        {
            if (model.ConstraintWrapper(constraint) is not { } wrapped)
                return constraint;

            if (!IsUnion(model, wrapped))
                return wrapped;

            // A union arm is picked out by its condition, so the live one is the
            // only compound child whose condition holds.
            NifItem? arm = wrapped.Children.FirstOrDefault(
                c => c.Children.Count > 0 && c.Name != "Constraint Info" && model.EvalCondition(c));

            return arm ?? wrapped;
        }

        /// <summary>
        /// Whether a wrapper is the polymorphic kind, holding one arm per type.
        /// </summary>
        /// <remarks>
        /// Told apart by the type number selecting the arms, which only a union has.
        /// A plain descriptor names its own kind by its class, and searching one for
        /// an arm finds the first compound inside it instead — a ragdoll's motor
        /// settings, say — and reads that as the whole constraint.
        /// </remarks>
        private static bool IsUnion(NifModel model, NifItem wrapped) =>
            model.FindItem(wrapped, "Type") is not null;

        /// <summary>
        /// Fields that say which bodies a constraint joins rather than how.
        /// </summary>
        /// <remarks>
        /// Block indices mean nothing once exported, and which bodies are joined is
        /// carried by the scene hierarchy instead, so both directions skip these.
        /// </remarks>
        public static bool IsEntityField(string name) =>
            name is "Entity A" or "Entity B" or "Constraint Info" or "Chained Entities";

        /// <summary>The property name a descriptor field is stored under.</summary>
        public static string FieldKey(string prefix, string fieldName) =>
            $"{prefix}{fieldName.Replace(' ', '_').ToLowerInvariant()}";
    }
}
