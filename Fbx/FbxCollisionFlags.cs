using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a collision object's flag word through FBX.
    /// </summary>
    /// <remarks>
    /// <c>bhkCOFlags</c> says how the engine keeps a body and the node it hangs from in
    /// step: <c>SET_LOCAL</c> reads the body's transform as local to the node rather
    /// than as a world transform, <c>SYNC_ON_UPDATE</c> makes the collision follow the
    /// node when it is animated, <c>RESET_TRANS</c> puts it back afterwards. None of it
    /// is visible in the shape, and none of it can be worked out from the scene.
    ///
    /// Rebuilding it as a bare <c>ACTIVE</c> is what the importer did, and it is wrong
    /// in the direction that is hardest to notice: the collision is still there, still
    /// the right size, still in roughly the right place, and stops tracking the thing
    /// it belongs to.
    /// </remarks>
    public static class FbxCollisionFlags
    {
        /// <summary>The property the flag word travels in.</summary>
        public const string Property = "nif_collision_flags";

        /// <summary>What a collision object gets when nothing travelled with it.</summary>
        /// <remarks>Bit 0, <c>BHKCO_ACTIVE</c>, which is what a fresh body needs at minimum.</remarks>
        public const uint Default = 1;

        /// <summary>Records the collision object's flags on the body's node.</summary>
        public static void Write(FbxObject bodyNode, NifModel model, NifItem collision)
        {
            if (model.FindItem(collision, "Flags") is not { } flags)
                return;

            bodyNode.Properties.SetUserString(
                Property, flags.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Puts them back on a rebuilt collision object.</summary>
        public static void Read(FbxObject bodyNode, NifModel model, NifItem collision)
        {
            string text = bodyNode.Properties.GetString(Property);

            uint value = uint.TryParse(
                text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
                ? parsed
                : Default;

            model.FindItem(collision, "Flags")?.Value.SetCount(value);
        }
    }
}
