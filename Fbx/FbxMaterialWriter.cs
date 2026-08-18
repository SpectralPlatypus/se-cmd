using MeshIO.Formats.Fbx;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes a <see cref="MaterialData"/> into an FBX scene as a Phong
    /// <c>Material</c> plus the <c>Texture</c> objects it references.
    /// </summary>
    /// <remarks>
    /// Follows spec §4.3–4.4. Textures attach to a material through
    /// object-to-property connections, so a texture is bound to the *named
    /// property* it drives (<c>DiffuseColor</c>, <c>NormalMap</c>) rather than to
    /// the material as a whole.
    ///
    /// Everything FBX has no standard slot for — the shader path, the environment
    /// map scale, the alpha settings, texture slots past the second — is written as
    /// user-defined properties, which is how FBXWrangler smuggles it through a DCC
    /// tool and reads it back.
    /// </remarks>
    public static class FbxMaterialWriter
    {
        private const int MaterialVersion = 102;
        private const int TextureVersion = 202;

        /// <summary>
        /// Adds the material and its textures, and connects them together.
        /// </summary>
        /// <param name="texturePathPrefix">Prepended to each texture path.</param>
        public static FbxObject AddMaterial(
            FbxScene scene, MaterialData material, string texturePathPrefix = "")
        {
            FbxObject fbxMaterial = scene.AddObject("Material", $"{material.Name}_material", string.Empty);
            FbxNode node = fbxMaterial.Node;

            node.Nodes.Add(new FbxNode("Version", MaterialVersion));
            node.Nodes.Add(new FbxNode("ShadingModel", "Phong"));
            node.Nodes.Add(new FbxNode("MultiLayer", 0));

            FbxProperties properties = fbxMaterial.Properties;

            NifColor3 emissive = material.EmissiveColor;
            properties.Set("EmissiveColor", "Color", "", "A",
                (double)emissive.R, (double)emissive.G, (double)emissive.B);
            properties.Set("EmissiveFactor", "Number", "", "A", (double)material.EmissiveMultiple);

            // Diffuse and ambient are left white: NIF carries no diffuse colour, the
            // diffuse texture supplies it.
            properties.Set("DiffuseColor", "Color", "", "A", 1.0, 1.0, 1.0);
            properties.Set("AmbientColor", "Color", "", "A", 1.0, 1.0, 1.0);
            properties.Set("AmbientFactor", "Number", "", "A", 1.0);

            NifColor3 specular = material.SpecularColor;
            properties.Set("SpecularColor", "Color", "", "A",
                (double)specular.R, (double)specular.G, (double)specular.B);

            // NIF keeps specular strength over 0..999.
            properties.Set("SpecularFactor", "Number", "", "A", material.SpecularStrength / 999.0);
            properties.Set("ShininessExponent", "Number", "", "A", (double)material.Glossiness);
            properties.Set("ReflectionFactor", "Number", "", "A", 0.0);

            if (material.Alpha < 1f)
                properties.Set("TransparencyFactor", "Number", "", "A", 1.0 - material.Alpha);

            // No standard FBX slot for these, so they ride as user properties.
            if (material.ShaderType.Length > 0)
                properties.SetUserString("shader_type", material.ShaderType);

            properties.Set("environment_map_scale", "Number", "", FbxProperties.UserFlags,
                (double)material.EnvironmentMapScale);

            if (material.AlphaProperty is { } alpha)
                WriteAlphaSettings(properties, alpha);

            AddTextures(scene, fbxMaterial, material, texturePathPrefix);

            return fbxMaterial;
        }

        /// <summary>
        /// Spreads a <c>NiAlphaProperty</c> across user properties, since FBX has
        /// nowhere to put a packed flags word.
        /// </summary>
        private static void WriteAlphaSettings(FbxProperties properties, AlphaSettings alpha)
        {
            properties.Set("color_blending_enable", "bool", "", FbxProperties.UserFlags,
                alpha.ColorBlendingEnable ? 1 : 0);
            properties.SetUserString("source_blend_mode", AlphaSettings.NameOf(alpha.SourceBlendMode));
            properties.SetUserString("destination_blend_mode", AlphaSettings.NameOf(alpha.DestinationBlendMode));
            properties.Set("alpha_test_enable", "bool", "", FbxProperties.UserFlags,
                alpha.AlphaTestEnable ? 1 : 0);
            properties.SetUserString("alpha_test_mode", AlphaSettings.NameOf(alpha.AlphaTestMode));
            properties.Set("no_sorter_flag", "bool", "", FbxProperties.UserFlags, alpha.NoSorter ? 1 : 0);

            // Named for Blender, which surfaces a short here.
            properties.Set("alpha_test_threshold", "Short", "", FbxProperties.UserFlags, (int)alpha.Threshold);
        }

        private static void AddTextures(
            FbxScene scene, FbxObject fbxMaterial, MaterialData material, string prefix)
        {
            for (int slot = 0; slot < material.Textures.Count; slot++)
            {
                string path = material.Textures[slot];

                if (path.Length == 0)
                    continue;

                // Slots past the second have no standard FBX property, so the
                // material gets a user-defined one named after the slot and the
                // texture connects to that.
                string property = slot switch
                {
                    MaterialData.DiffuseSlot => "DiffuseColor",
                    MaterialData.NormalSlot => "NormalMap",
                    _ => $"slot{slot + 1}"
                };

                if (slot > MaterialData.NormalSlot)
                    fbxMaterial.Properties.SetUserString(property, path);

                FbxObject texture = AddTexture(scene, $"{material.Name}_{property}", path, prefix, material);

                scene.ConnectToProperty(texture, fbxMaterial, property);

                // A diffuse texture also drives transparency when the shape carried
                // an alpha property, matching FBXWrangler.
                if (slot == MaterialData.DiffuseSlot && material.AlphaProperty is not null)
                    scene.ConnectToProperty(texture, fbxMaterial, "TransparentColor");
            }
        }

        private static FbxObject AddTexture(
            FbxScene scene, string name, string path, string prefix, MaterialData material)
        {
            string full = prefix.Length > 0 ? Path.Combine(prefix, path) : path;

            FbxObject texture = scene.AddObject("Texture", name, string.Empty);
            FbxNode node = texture.Node;

            node.Nodes.Add(new FbxNode("Type", "TextureVideoClip"));
            node.Nodes.Add(new FbxNode("Version", TextureVersion));
            node.Nodes.Add(new FbxNode("TextureName", $"Texture::{name}"));

            FbxProperties properties = texture.Properties;
            properties.Set("UVSet", "KString", "", "", FbxMeshWriter.UvElementName);
            properties.Set("UseMaterial", "bool", "", "", 1);

            node.Nodes.Add(new FbxNode("FileName", full));
            node.Nodes.Add(new FbxNode("RelativeFilename", path));

            NifVector2 offset = material.UvOffset;
            NifVector2 scale = material.UvScale;

            node.Nodes.Add(new FbxNode("ModelUVTranslation", (double)offset.X, (double)offset.Y));
            node.Nodes.Add(new FbxNode("ModelUVScaling", (double)scale.X, (double)scale.Y));
            node.Nodes.Add(new FbxNode("Texture_Alpha_Source", "None"));
            node.Nodes.Add(new FbxNode("Cropping", 0, 0, 0, 0));

            return texture;
        }
    }
}
