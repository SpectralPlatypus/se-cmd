using MeshIO.Formats.Fbx;
using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>Knobs for the NIF to FBX direction.</summary>
    public sealed class NifToFbxOptions
    {
        /// <summary>Prefix prepended to texture paths written into materials.</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>Emit the tessellated geometry of Havok collision shapes.</summary>
        public bool ExportCollision { get; set; } = true;
    }

    /// <summary>
    /// Converts a loaded NIF into an FBX scene.
    /// </summary>
    /// <remarks>
    /// Follows `docs/fbx-nif-conversion-spec.md` §4, which is FBXWrangler's
    /// behaviour. The conventions that matter most, because getting them wrong is
    /// silent rather than loud:
    ///
    /// <list type="bullet">
    /// <item>No axis conversion. The FBX declares Max axes (Z-up, right-handed), so
    /// coordinates cross unchanged.</item>
    /// <item>A shape's own transform is baked into its vertices, not left on the
    /// node.</item>
    /// <item>V is flipped on UVs.</item>
    /// <item>A mesh never attaches to the scene root directly; a
    /// <c>&lt;name&gt;_support</c> node is interposed.</item>
    /// </list>
    /// </remarks>
    public sealed class NifToFbx(NifModel model, NifToFbxOptions? options = null)
    {
        private readonly NifModel _model = model;
        private readonly NifToFbxOptions _options = options ?? new NifToFbxOptions();
        private readonly Dictionary<NifItem, FbxObject> _built = [];

        /// <summary>Diagnostics gathered during conversion.</summary>
        public List<string> Warnings { get; } = [];

        /// <summary>Converts the model into a fresh FBX document.</summary>
        public FbxDocument Convert()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            foreach (NifItem root in FindRootBlocks())
                ConvertNode(scene, root, parent: null);

            scene.Flush();
            return document;
        }

        /// <summary>
        /// The blocks nothing else points at, which are the scene roots.
        /// </summary>
        /// <remarks>
        /// The footer names them explicitly, but it is not always present or
        /// correct, so fall back to the first block — which is the root in every
        /// file Bethesda ships.
        /// </remarks>
        private List<NifItem> FindRootBlocks()
        {
            var roots = new List<NifItem>();

            NifItem? footerRoots = _model.FindItem(_model.Footer, "Roots");

            if (footerRoots is not null)
            {
                foreach (NifItem link in footerRoots.Children)
                {
                    if (_model.GetBlock(link) is { } block)
                        roots.Add(block);
                }
            }

            if (roots.Count == 0 && _model.Blocks.Count > 0)
                roots.Add(_model.Blocks[0]);

            return roots;
        }

        private FbxObject? ConvertNode(FbxScene scene, NifItem block, FbxObject? parent)
        {
            if (_built.TryGetValue(block, out FbxObject? existing))
                return existing;

            // Geometry is an attribute of a node in FBX, not a node itself.
            if (_model.BlockInherits(block, "NiTriBasedGeom"))
            {
                ConvertGeometry(scene, block, parent);
                return null;
            }

            if (!_model.BlockInherits(block, "NiAVObject"))
                return null;

            string name = NameEncoding.Sanitize(_model.GetName(block));

            if (name.Length == 0)
                name = block.Name;

            FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", _model.GetTransform(block));
            _built[block] = node;

            if (parent is null)
                scene.ConnectToRoot(node);
            else
                scene.Connect(node, parent);

            foreach (NifItem child in _model.GetChildren(block))
                ConvertNode(scene, child, node);

            return node;
        }

        private void ConvertGeometry(FbxScene scene, NifItem shape, FbxObject? parent)
        {
            NifItem? data = _model.GetRef(shape, "Data");

            if (data is null)
            {
                Warnings.Add($"{_model.GetName(shape)} has no geometry data");
                return;
            }

            MeshGeometry mesh = ReadGeometry(shape, data);

            if (mesh.IsEmpty)
            {
                Warnings.Add($"{_model.GetName(shape)} has no vertices");
                return;
            }

            if (!mesh.IsWellFormed(out string? problem))
            {
                Warnings.Add($"{_model.GetName(shape)}: {problem}");
                return;
            }

            string name = NameEncoding.Sanitize(_model.GetName(shape));

            if (name.Length == 0)
                name = shape.Name;

            // FBX allows one mesh attribute per node, and refuses meshes parented
            // straight to the scene root, so a holder node is interposed in both
            // cases. The _support suffix is what FBXWrangler uses and what the
            // import side looks for.
            FbxObject holder = FbxMeshWriter.AddModel(scene, $"{name}_support", "Mesh", NifTransform.Identity);

            if (parent is null)
                scene.ConnectToRoot(holder);
            else
                scene.Connect(holder, parent);

            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, name, mesh);
            scene.Connect(geometry, holder);

            if (ReadMaterial(shape, name) is { } material)
            {
                FbxObject fbxMaterial = FbxMaterialWriter.AddMaterial(scene, material, _options.TexturePath);

                // A material belongs to the node carrying the mesh, not the mesh,
                // and the geometry's material element points at index 0.
                scene.Connect(fbxMaterial, holder);
                FbxMeshWriter.AddSingleMaterialElement(geometry);
            }

            _built[shape] = holder;
        }

        /// <summary>
        /// Reads a shape's shader and alpha properties into the neutral material
        /// form, or null when it has no shader property.
        /// </summary>
        private MaterialData? ReadMaterial(NifItem shape, string name)
        {
            NifItem? shader = _model.GetRef(shape, "Shader Property");

            if (shader is null || !_model.BlockInherits(shader, "BSLightingShaderProperty"))
                return null;

            var material = new MaterialData
            {
                Name = name,
                EmissiveColor = Color3Of(shader, "Emissive Color"),
                EmissiveMultiple = FloatOf(shader, "Emissive Multiple", 1f),
                SpecularColor = Color3Of(shader, "Specular Color"),
                SpecularStrength = FloatOf(shader, "Specular Strength"),
                Glossiness = FloatOf(shader, "Glossiness"),
                Alpha = FloatOf(shader, "Alpha", 1f),
                EnvironmentMapScale = FloatOf(shader, "Environment Map Scale"),
                UvOffset = Vector2Of(shader, "UV Offset", new NifVector2(0f, 0f)),
                UvScale = Vector2Of(shader, "UV Scale", new NifVector2(1f, 1f)),
                TextureClampMode = _model.GetUInt(shader, "Texture Clamp Mode")
            };

            // The shader path is stored on the NiObjectNET level, guarded by an
            // onlyT condition, and is written out by name rather than as a number.
            if (_model.FindItem(shader, "Shader Type") is { } shaderType
                && _model.Database.TryGetEnumOptionName(
                    shaderType.Type, shaderType.Value.ToUInt(), out string typeName))
            {
                material.ShaderType = typeName;
            }

            if (_model.GetRef(shader, "Texture Set") is { } textureSet
                && _model.FindItem(textureSet, "Textures") is { } textures)
            {
                foreach (NifItem texture in textures.Children)
                    material.Textures.Add(texture.Value.AsString());
            }

            if (_model.GetRef(shape, "Alpha Property") is { } alphaProperty)
            {
                material.AlphaProperty = AlphaSettings.FromFlags(
                    (ushort)_model.GetUInt(alphaProperty, "Flags"),
                    (byte)_model.GetUInt(alphaProperty, "Threshold"));
            }

            return material;
        }

        private float FloatOf(NifItem block, string field, float fallback = 0f) =>
            _model.FindItem(block, field) is { } item ? item.Value.ToFloat() : fallback;

        private NifColor3 Color3Of(NifItem block, string field) =>
            _model.FindItem(block, field)?.Value.Get<NifColor3>() ?? new NifColor3();

        private NifVector2 Vector2Of(NifItem block, string field, NifVector2 fallback) =>
            _model.FindItem(block, field) is { } item ? item.Value.Get<NifVector2>() : fallback;

        /// <summary>
        /// Reads a geometry data block into the neutral mesh form, baking the
        /// shape's own transform into the vertices and flipping V.
        /// </summary>
        private MeshGeometry ReadGeometry(NifItem shape, NifItem data)
        {
            var mesh = new MeshGeometry();

            NifTransform transform = _model.GetTransform(shape);

            foreach (NifVector3 v in _model.GetVertices(data))
                mesh.Vertices.Add(transform.Apply(v));

            foreach (NifVector3 n in _model.GetNormals(data))
                mesh.Normals.Add(transform.ApplyDirection(n));

            // NIF's V axis points the other way from FBX's.
            foreach (NifVector2 uv in _model.GetUvSet(data))
                mesh.Uvs.Add(new NifVector2(uv.X, 1f - uv.Y));

            mesh.Colors.AddRange(_model.GetVertexColors(data));
            mesh.Triangles.AddRange(_model.GetGeometryTriangles(data));

            return mesh;
        }
    }

    /// <summary>
    /// Builds the fixed scaffolding every FBX file carries around its object graph.
    /// </summary>
    public static class FbxDocumentTemplate
    {
        /// <summary>
        /// An empty FBX 7.4 document with the header, global settings and empty
        /// object and connection sections.
        /// </summary>
        /// <remarks>
        /// Global settings declare Max axes (Z-up, right-handed) and centimetres,
        /// matching what FBXWrangler sets on the scene. Those two declarations are
        /// what let coordinates pass through unconverted.
        /// </remarks>
        public static FbxDocument CreateEmpty()
        {
            var document = new FbxDocument { Version = FbxVersion.v7400 };

            var header = new FbxNode("FBXHeaderExtension");
            header.Nodes.Add(new FbxNode("FBXHeaderVersion", 1003));
            header.Nodes.Add(new FbxNode("FBXVersion", (int)FbxVersion.v7400));

            // Not decoration: readers reject a header without a timestamp.
            DateTime now = DateTime.Now;
            var stamp = new FbxNode("CreationTimeStamp");
            stamp.Nodes.Add(new FbxNode("Version", 1000));
            stamp.Nodes.Add(new FbxNode("Year", now.Year));
            stamp.Nodes.Add(new FbxNode("Month", now.Month));
            stamp.Nodes.Add(new FbxNode("Day", now.Day));
            stamp.Nodes.Add(new FbxNode("Hour", now.Hour));
            stamp.Nodes.Add(new FbxNode("Minute", now.Minute));
            stamp.Nodes.Add(new FbxNode("Second", now.Second));
            stamp.Nodes.Add(new FbxNode("Millisecond", now.Millisecond));
            header.Nodes.Add(stamp);

            header.Nodes.Add(new FbxNode("Creator", "se-cmd"));
            document.Nodes.Add(header);

            document.Nodes.Add(new FbxNode("Creator", "se-cmd"));

            var settings = new FbxNode("GlobalSettings");
            settings.Nodes.Add(new FbxNode("Version", 1000));

            var properties = new FbxNode("Properties70");
            settings.Nodes.Add(properties);

            var globals = new FbxProperties(properties);

            // Z-up, right-handed: FbxAxisSystem::Max, which is also NIF's convention.
            globals.Set("UpAxis", "int", "Integer", "", 2);
            globals.Set("UpAxisSign", "int", "Integer", "", 1);
            globals.Set("FrontAxis", "int", "Integer", "", 1);
            globals.Set("FrontAxisSign", "int", "Integer", "", -1);
            globals.Set("CoordAxis", "int", "Integer", "", 0);
            globals.Set("CoordAxisSign", "int", "Integer", "", 1);
            globals.Set("UnitScaleFactor", "double", "Number", "", 1.0);

            document.Nodes.Add(settings);

            var definitions = new FbxNode("Definitions");
            definitions.Nodes.Add(new FbxNode("Version", 100));
            definitions.Nodes.Add(new FbxNode("Count", 0));
            document.Nodes.Add(definitions);

            document.Nodes.Add(new FbxNode("Objects"));
            document.Nodes.Add(new FbxNode("Connections"));

            return document;
        }
    }
}
