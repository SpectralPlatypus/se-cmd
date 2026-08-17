using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>Knobs for the FBX to NIF direction.</summary>
    public sealed class FbxToNifOptions
    {
        /// <summary>Mirror U on import. Off by default, as in FBXWrangler.</summary>
        public bool InvertU { get; set; }

        /// <summary>Mirror V on import. On by default: NIF's V axis points the other way.</summary>
        public bool InvertV { get; set; } = true;

        /// <summary>Name given to the root block. Defaults to the file stem.</summary>
        public string RootName { get; set; } = "Scene";

        public uint Version { get; set; } = 0x14020007;

        public uint UserVersion { get; set; } = 12;

        /// <summary>Bethesda stream version. 83 is Skyrim LE, 100 Skyrim SE.</summary>
        public uint BSVersion { get; set; } = 83;
    }

    /// <summary>
    /// Converts an FBX scene into a NIF.
    /// </summary>
    /// <remarks>
    /// Follows `docs/fbx-nif-conversion-spec.md` §5. The root becomes a
    /// <c>BSFadeNode</c> named after the file rather than after any node in the
    /// scene, meshes become <c>NiTriShape</c> plus <c>NiTriShapeData</c>, and node
    /// names are decoded back through <see cref="NameEncoding"/>.
    ///
    /// Collision nodes (the <c>_rb</c> and <c>_sp</c> suffixes) are recognised and
    /// reported but not yet built; that is spec §5.7.
    /// </remarks>
    public sealed class FbxToNif(FbxScene scene, FbxToNifOptions? options = null)
    {
        private readonly FbxScene _scene = scene;
        private readonly FbxToNifOptions _options = options ?? new FbxToNifOptions();

        private NifModel _model = null!;

        /// <summary>Diagnostics gathered during conversion.</summary>
        public List<string> Warnings { get; } = [];

        /// <summary>Builds a NIF from the scene.</summary>
        public NifModel Convert(NifXmlDatabase database)
        {
            _model = NifModel.CreateNew(database, _options.Version, _options.UserVersion, _options.BSVersion);

            NifItem root = _model.InsertBlock("BSFadeNode");

            // Named after the file rather than after any node in the scene (§5.2).
            _model.SetString(root, "Name", _options.RootName);

            var rootModels = _scene.RootModels().ToList();
            var children = new List<NifItem>();

            // FBXWrangler renames the FBX *implicit* root to the NIF root's name, so
            // a scene it produced has the NIF root as the implicit root and no Model
            // standing for it. We export a real Model instead, which is friendlier
            // in a DCC tool but leaves one to collapse on the way back. A lone root
            // Model carrying no geometry of its own is exactly that node, so it maps
            // onto the NIF root rather than becoming a redundant child of it.
            if (rootModels.Count == 1 && !HasGeometry(rootModels[0]))
            {
                FbxObject sceneNode = rootModels[0];
                _model.SetTransform(root, ReadTransform(sceneNode));

                foreach (FbxObject child in _scene.ChildrenOf(sceneNode.Id).Where(o => o.Class == "Model"))
                    ConvertModel(child, children);
            }
            else
            {
                _model.SetTransform(root, NifTransform.Identity);

                foreach (FbxObject model in rootModels)
                    ConvertModel(model, children);
            }

            AttachChildren(root, children);

            _model.SetRoots([root]);
            _model.UpdateHeader();

            return _model;
        }

        /// <summary>
        /// Turns one FBX Model into a NIF block, recursing into its children.
        /// </summary>
        private void ConvertModel(FbxObject model, List<NifItem> into)
        {
            string name = NameEncoding.Unsanitize(model.Name);

            // Collision bodies are leaves keyed off their name suffix, not ordinary
            // nodes. Recognising them matters even while they are unimplemented, so
            // they are not silently turned into empty NiNodes.
            if (name.EndsWith("_rb", StringComparison.Ordinal) || name.EndsWith("_sp", StringComparison.Ordinal))
            {
                Warnings.Add($"{name}: collision bodies are not converted yet, skipping");
                return;
            }

            NifTransform transform = ReadTransform(model);

            // A mesh holder interposed on export carries no information of its own,
            // so unwrap it rather than emitting a redundant NiNode.
            bool isHolder = name.EndsWith("_support", StringComparison.Ordinal);

            var geometries = _scene.ChildrenOf(model.Id)
                .Where(o => o.Class == "Geometry")
                .ToList();

            var childModels = _scene.ChildrenOf(model.Id)
                .Where(o => o.Class == "Model")
                .ToList();

            if (isHolder && geometries.Count > 0 && childModels.Count == 0)
            {
                foreach (FbxObject geometry in geometries)
                {
                    if (BuildShape(geometry, model, transform) is { } shape)
                        into.Add(shape);
                }

                return;
            }

            NifItem node = _model.InsertBlock("NiNode");
            _model.SetString(node, "Name", name);
            _model.SetTransform(node, transform);

            var nodeChildren = new List<NifItem>();

            foreach (FbxObject geometry in geometries)
            {
                if (BuildShape(geometry, model, NifTransform.Identity) is { } shape)
                    nodeChildren.Add(shape);
            }

            foreach (FbxObject child in childModels)
                ConvertModel(child, nodeChildren);

            AttachChildren(node, nodeChildren);
            into.Add(node);
        }

        /// <summary>Builds a <c>NiTriShape</c> and its data from an FBX geometry.</summary>
        private NifItem? BuildShape(FbxObject geometry, FbxObject holder, NifTransform transform)
        {
            var readerOptions = new FbxMeshReader.Options
            {
                InvertU = _options.InvertU,
                InvertV = _options.InvertV
            };

            MeshGeometry? mesh = FbxMeshReader.Read(geometry, readerOptions);

            if (mesh is null || mesh.IsEmpty)
            {
                Warnings.Add($"{geometry.Name}: no usable geometry, skipping");
                return null;
            }

            if (mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{geometry.Name}: no triangles, skipping");
                return null;
            }

            // NIF expects normals; recompute rather than emit a shape that renders
            // with ambient light only.
            if (!mesh.HasNormals)
                mesh.RecalculateNormals();

            NifItem shape = _model.InsertBlock("NiTriShape");
            _model.SetString(shape, "Name", NameEncoding.Unsanitize(geometry.Name));
            _model.SetTransform(shape, transform);

            NifItem data = _model.InsertBlock("NiTriShapeData");
            WriteGeometryData(data, mesh);

            _model.SetRef(shape, "Data", data);

            BuildMaterial(shape, holder);

            return shape;
        }

        /// <summary>Fills a <c>NiTriShapeData</c> from the neutral mesh.</summary>
        private void WriteGeometryData(NifItem data, MeshGeometry mesh)
        {
            SetCount(data, "Num Vertices", (uint)mesh.Vertices.Count);
            SetBool(data, "Has Vertices", true);

            // The UV set count lives in the low six bits of the data flags, and the
            // UV array's length expression reads it, so it has to be set before the
            // array is sized.
            uint uvSets = mesh.HasUvs ? 1u : 0u;
            SetCount(data, "Data Flags", uvSets);
            SetCount(data, "BS Data Flags", uvSets);

            SetBool(data, "Has Normals", mesh.HasNormals);
            SetBool(data, "Has Vertex Colors", mesh.HasColors);

            WriteVector3Array(data, "Vertices", mesh.Vertices);

            if (mesh.HasNormals)
                WriteVector3Array(data, "Normals", mesh.Normals);

            if (mesh.HasColors && _model.FindItem(data, "Vertex Colors") is { } colors)
            {
                colors.InvalidateConditionsRecursive();
                _model.UpdateArraySize(colors);

                for (int i = 0; i < mesh.Colors.Count && i < colors.Children.Count; i++)
                    colors.Children[i].Value.Set(mesh.Colors[i]);
            }

            if (mesh.HasUvs && _model.FindItem(data, "UV Sets") is { } sets)
            {
                sets.InvalidateConditionsRecursive();
                _model.UpdateArraySize(sets);

                // Outer index is the set, inner the vertex.
                if (sets.Child(0) is { } set0)
                {
                    _model.UpdateArraySize(set0);

                    for (int i = 0; i < mesh.Uvs.Count && i < set0.Children.Count; i++)
                        set0.Children[i].Value.Set(mesh.Uvs[i]);
                }
            }

            (NifVector3 center, float radius) = mesh.ComputeBoundingSphere();
            _model.FindItem(data, @"Bounding Sphere\Center")?.Value.Set(center);
            _model.FindItem(data, @"Bounding Sphere\Radius")?.Value.SetFloat(radius);

            SetCount(data, "Num Triangles", (uint)mesh.Triangles.Count);
            SetCount(data, "Num Triangle Points", (uint)(mesh.Triangles.Count * 3));
            SetBool(data, "Has Triangles", true);

            if (_model.FindItem(data, "Triangles") is { } triangles)
            {
                triangles.InvalidateConditionsRecursive();
                _model.UpdateArraySize(triangles);

                for (int i = 0; i < mesh.Triangles.Count && i < triangles.Children.Count; i++)
                    triangles.Children[i].Value.Set(mesh.Triangles[i]);
            }
        }

        /// <summary>
        /// Rebuilds a shader property from the material attached to the mesh holder.
        /// </summary>
        private void BuildMaterial(NifItem shape, FbxObject holder)
        {
            FbxObject? material = _scene.ChildrenOf(holder.Id).FirstOrDefault(o => o.Class == "Material");

            if (material is null)
                return;

            NifItem shader = _model.InsertBlock("BSLightingShaderProperty");
            FbxProperties properties = material.Properties;

            SetFloat(shader, "Glossiness", (float)properties.GetDouble("ShininessExponent"));

            // FBX keeps the specular factor over 0..1, NIF over 0..999.
            SetFloat(shader, "Specular Strength", (float)(properties.GetDouble("SpecularFactor") * 999.0));

            (double sr, double sg, double sb) = properties.GetVector3("SpecularColor", 1.0);
            _model.FindItem(shader, "Specular Color")?.Value.Set(
                new NifColor3((float)sr, (float)sg, (float)sb));

            (double er, double eg, double eb) = properties.GetVector3("EmissiveColor");
            _model.FindItem(shader, "Emissive Color")?.Value.Set(
                new NifColor3((float)er, (float)eg, (float)eb));

            SetFloat(shader, "Emissive Multiple", (float)properties.GetDouble("EmissiveFactor", 1.0));
            SetFloat(shader, "Alpha", (float)(1.0 - properties.GetDouble("TransparencyFactor")));
            SetFloat(shader, "Environment Map Scale", (float)properties.GetDouble("environment_map_scale"));

            NifItem textureSet = BuildTextureSet(material);
            _model.SetRef(shader, "Texture Set", textureSet);

            _model.SetRef(shape, "Shader Property", shader);

            BuildAlphaProperty(shape, properties);
        }

        private NifItem BuildTextureSet(FbxObject material)
        {
            NifItem set = _model.InsertBlock("BSShaderTextureSet");

            // Skyrim always writes nine slots, whether or not they are used.
            const int SlotCount = 9;
            var paths = new string[SlotCount];

            foreach ((FbxObject texture, string property) in _scene.PropertyConnectionsTo(material.Id))
            {
                int slot = property switch
                {
                    "DiffuseColor" => MaterialData.DiffuseSlot,
                    "NormalMap" => MaterialData.NormalSlot,
                    _ when property.StartsWith("slot", StringComparison.Ordinal)
                        && int.TryParse(property.AsSpan(4), out int n) => n - 1,
                    _ => -1
                };

                if (slot < 0 || slot >= SlotCount)
                    continue;

                string path = texture.Child("RelativeFilename")?.Properties.FirstOrDefault() as string
                    ?? texture.Child("FileName")?.Properties.FirstOrDefault() as string
                    ?? string.Empty;

                paths[slot] = MaterialData.NormalizeTexturePath(path);
            }

            if (_model.SetArraySize(set, "Num Textures", "Textures", SlotCount) is { } textures)
            {
                for (int i = 0; i < SlotCount && i < textures.Children.Count; i++)
                    textures.Children[i].Value.Set(paths[i] ?? string.Empty);
            }

            return set;
        }

        /// <summary>
        /// Reassembles a <c>NiAlphaProperty</c> from the user properties the export
        /// side spread it across.
        /// </summary>
        private void BuildAlphaProperty(NifItem shape, FbxProperties properties)
        {
            if (!properties.Contains("source_blend_mode") && !properties.Contains("alpha_test_enable"))
                return;

            var alpha = new AlphaSettings
            {
                ColorBlendingEnable = properties.GetBool("color_blending_enable"),
                SourceBlendMode = AlphaSettings.ParseBlendMode(properties.GetString("source_blend_mode")),
                DestinationBlendMode = AlphaSettings.ParseBlendMode(properties.GetString("destination_blend_mode")),
                AlphaTestEnable = properties.GetBool("alpha_test_enable"),
                AlphaTestMode = AlphaSettings.ParseTestMode(properties.GetString("alpha_test_mode")),
                NoSorter = properties.GetBool("no_sorter_flag"),
                Threshold = (byte)properties.GetInt("alpha_test_threshold")
            };

            // An all-zero flags word means nothing was set; FBXWrangler emits no
            // property in that case rather than an inert one.
            if (alpha.ToFlags() == 0)
                return;

            NifItem block = _model.InsertBlock("NiAlphaProperty");
            SetCount(block, "Flags", alpha.ToFlags());
            SetCount(block, "Threshold", alpha.Threshold);

            _model.SetRef(shape, "Alpha Property", block);
        }

        // --- helpers ----------------------------------------------------------

        /// <summary>True when a model carries geometry, directly or via a holder.</summary>
        private bool HasGeometry(FbxObject model) =>
            _scene.ChildrenOf(model.Id).Any(o => o.Class == "Geometry");

        private NifTransform ReadTransform(FbxObject model)
        {
            (double tx, double ty, double tz) = model.Properties.GetVector3("Lcl Translation");
            (double rx, double ry, double rz) = model.Properties.GetVector3("Lcl Rotation");
            (double sx, double sy, double sz) = model.Properties.GetVector3("Lcl Scaling", 1.0);

            // NIF has no non-uniform scale, so a non-uniform one has to collapse.
            var scale = (float)((sx + sy + sz) / 3.0);

            if (Math.Abs(sx - sy) > 1e-4 || Math.Abs(sy - sz) > 1e-4)
                Warnings.Add($"{model.Name}: non-uniform scale ({sx:G4}, {sy:G4}, {sz:G4}) averaged to {scale:G4}");

            return new NifTransform(
                new NifVector3((float)tx, (float)ty, (float)tz),
                NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz),
                scale);
        }

        private void AttachChildren(NifItem node, List<NifItem> children)
        {
            if (children.Count == 0)
                return;

            if (_model.SetArraySize(node, "Num Children", "Children", children.Count) is not { } array)
                return;

            for (int i = 0; i < children.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetLink(_model.IndexOf(children[i]));
        }

        private void WriteVector3Array(NifItem block, string field, IReadOnlyList<NifVector3> values)
        {
            if (_model.FindItem(block, field) is not { } array)
                return;

            array.InvalidateConditionsRecursive();
            _model.UpdateArraySize(array);

            for (int i = 0; i < values.Count && i < array.Children.Count; i++)
                array.Children[i].Value.Set(values[i]);
        }

        private void SetCount(NifItem block, string field, uint value) =>
            _model.FindItem(block, field)?.Value.SetCount(value);

        private void SetFloat(NifItem block, string field, float value) =>
            _model.FindItem(block, field)?.Value.SetFloat(value);

        private void SetBool(NifItem block, string field, bool value) =>
            _model.FindItem(block, field)?.Value.SetCount(value ? 1u : 0u);
    }
}
