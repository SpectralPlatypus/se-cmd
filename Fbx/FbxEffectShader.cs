using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a <c>BSEffectShaderProperty</c> through FBX.
    /// </summary>
    /// <remarks>
    /// ck-cmd's FBX path does not handle these at all. Its export casts a shape's
    /// shader to <c>BSLightingShaderProperty</c> and takes the null when that fails, so
    /// an effect-shader shape leaves with no material; its import only ever constructs
    /// a lighting shader. (Effect shaders are handled elsewhere in ck-cmd — the LE to
    /// SE converter builds them, and the BSXFlags calculation reads their external
    /// emittance — so the gap is in the FBX path rather than in the tool.)
    ///
    /// Copying that would lose every glow, decal, blood splatter and magic effect in a
    /// file, so this departs from the reference. The two shader classes share almost no
    /// fields — an effect shader has its own source and greyscale textures rather than
    /// a texture set, and a base colour rather than a specular model — so rather than
    /// forcing them through the common material form, the block's own fields ride
    /// across flat, as constraints and particle systems do.
    /// </remarks>
    public static class FbxEffectShader
    {
        /// <summary>The property naming which shader block a material stands for.</summary>
        public const string BlockProperty = "shader_block";

        /// <summary>The block this carries.</summary>
        public const string BlockName = "BSEffectShaderProperty";

        /// <summary>Prefix on the shader's own fields.</summary>
        public const string Prefix = "es_";

        /// <summary>
        /// Fields the rebuild supplies for itself.
        /// </summary>
        /// <remarks>
        /// The controller chain is not carried: an animated shader is animated through
        /// the sequences, which travel by their own route, and a stale link here would
        /// point into a block list that no longer has that block.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Name", "Controller", "Extra Data List", "Num Extra Data List"
        };

        /// <summary>Whether a shape's shader is one of these.</summary>
        public static bool Is(NifModel model, NifItem shader) =>
            model.BlockInherits(shader, BlockName);

        /// <summary>Writes the shader's fields onto the material standing for it.</summary>
        public static void Write(FbxObject material, NifModel model, NifItem shader)
        {
            material.Properties.SetUserString(BlockProperty, BlockName);

            NifFieldCodec.Write(
                model, shader, Prefix,
                (name, value) => material.Properties.SetUserString(name, value),
                child => Skipped.Contains(child.Name));
        }

        /// <summary>Whether a material carries one of these rather than a lighting shader.</summary>
        public static bool WasWritten(FbxObject material) =>
            material.Properties.GetString(BlockProperty) == BlockName;

        /// <summary>Rebuilds the shader from a material that carries one.</summary>
        public static NifItem Read(FbxObject material, NifModel model)
        {
            NifItem shader = model.InsertBlock(BlockName);

            NifFieldCodec.Read(
                model, shader, Prefix,
                name => material.Properties.GetString(name) is { Length: > 0 } value ? value : null,
                child => Skipped.Contains(child.Name));

            return shader;
        }
    }
}
