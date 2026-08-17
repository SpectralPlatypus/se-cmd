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

            if (_options.ExportCollision)
                ConvertCollision(scene, block, node);

            foreach (NifItem child in _model.GetChildren(block))
                ConvertNode(scene, child, node);

            return node;
        }

        /// <summary>
        /// Emits a node's Havok collision as tessellated geometry.
        /// </summary>
        /// <remarks>
        /// The rigid body becomes a node suffixed <c>_rb</c>, which is the marker
        /// the import side keys off (spec §3.1), and its shape becomes a mesh under
        /// it. A body's transform is a *world* transform even when parented, so it
        /// is written as-is rather than composed with anything.
        /// </remarks>
        private void ConvertCollision(FbxScene scene, NifItem block, FbxObject parent)
        {
            NifItem? collision = _model.GetRef(block, "Collision Object");

            if (collision is null)
                return;

            NifItem? body = _model.GetRef(collision, "Body");

            if (body is null)
                return;

            string name = NameEncoding.Sanitize(_model.GetName(block));

            if (name.Length == 0)
                name = block.Name;

            bool isPhantom = _model.BlockInherits(body, "bhkSimpleShapePhantom");
            string suffix = isPhantom ? "_sp" : "_rb";

            NifTransform transform = NifTransform.Identity;

            if (!isPhantom && _model.FindItem(body, "Translation") is { } translation)
            {
                // Havok works in metres; the rest of the file is in Skyrim units.
                NifVector4 t = translation.Value.Get<NifVector4>();
                var scaled = new NifVector3(
                    t.X * ShapeTessellator.BhkScaleFactor,
                    t.Y * ShapeTessellator.BhkScaleFactor,
                    t.Z * ShapeTessellator.BhkScaleFactor);

                NifQuat rotation = _model.FindItem(body, "Rotation")?.Value.Get<NifQuat>() ?? NifQuat.Identity;
                transform = new NifTransform(scaled, RotationFromQuaternion(rotation), 1f);
            }

            FbxObject bodyNode = FbxMeshWriter.AddModel(scene, name + suffix, "Null", transform);
            scene.Connect(bodyNode, parent);

            NifItem? shape = _model.GetRef(body, "Shape");

            if (shape is null)
            {
                Warnings.Add($"{name}: collision body has no shape");
                return;
            }

            ConvertShape(scene, shape, bodyNode, name + suffix);
        }

        /// <summary>
        /// Walks a shape tree, emitting a mesh for each leaf and a node for each
        /// container, with the suffixes the import side recognises.
        /// </summary>
        private void ConvertShape(FbxScene scene, NifItem shape, FbxObject parent, string parentName, int depth = 0)
        {
            if (depth > 16)
            {
                Warnings.Add($"{parentName}: collision shape nests too deeply, stopping");
                return;
            }

            // Containers: emit a node and recurse.
            string? containerSuffix = shape.Name switch
            {
                "bhkTransformShape" or "bhkConvexTransformShape" => "_transform",
                "bhkListShape" => "_list",
                "bhkConvexListShape" => "_convex_list",
                "bhkMoppBvTreeShape" => "_mopp",
                _ => null
            };

            if (containerSuffix is not null)
            {
                string name = parentName + containerSuffix;
                FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", NifTransform.Identity);
                scene.Connect(node, parent);

                // A MOPP tree just wraps the shape it indexes; the tree itself is
                // regenerated on import and carries nothing to convert.
                foreach (NifItem child in ChildShapesOf(shape))
                    ConvertShape(scene, child, node, name, depth + 1);

                return;
            }

            MeshGeometry? mesh = TessellateShape(shape);

            if (mesh is null)
            {
                Warnings.Add($"{parentName}: {shape.Name} is not a shape this converts yet");
                return;
            }

            if (mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{parentName}: {shape.Name} tessellated to nothing");
                return;
            }

            ShapeTessellator.Scale(mesh, ShapeTessellator.BhkScaleFactor);

            string shapeName = parentName + ShapeSuffix(shape.Name);
            FbxObject holder = FbxMeshWriter.AddModel(scene, shapeName, "Mesh", NifTransform.Identity);
            scene.Connect(holder, parent);

            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, shapeName + "_geometry", mesh);
            scene.Connect(geometry, holder);
        }

        private IEnumerable<NifItem> ChildShapesOf(NifItem shape)
        {
            if (_model.GetRef(shape, "Shape") is { } single)
                yield return single;

            foreach (NifItem child in _model.GetRefArray(shape, "Sub Shapes"))
                yield return child;
        }

        private static string ShapeSuffix(string blockName) => blockName switch
        {
            "bhkSphereShape" => "_sphere",
            "bhkBoxShape" => "_box",
            "bhkCapsuleShape" => "_capsule",
            "bhkConvexVerticesShape" => "_convex",
            "bhkCompressedMeshShape" => "_mesh",
            _ => "_shape"
        };

        /// <summary>Tessellates a leaf shape, or null when it is not one we handle.</summary>
        private MeshGeometry? TessellateShape(NifItem shape) => shape.Name switch
        {
            "bhkSphereShape" => ShapeTessellator.Sphere(
                _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            "bhkBoxShape" => ShapeTessellator.Box(
                _model.FindItem(shape, "Dimensions")?.Value.Get<NifVector3>() ?? new NifVector3()),

            "bhkCapsuleShape" => ShapeTessellator.Capsule(
                _model.FindItem(shape, "First Point")?.Value.Get<NifVector3>() ?? new NifVector3(),
                _model.FindItem(shape, "Second Point")?.Value.Get<NifVector3>() ?? new NifVector3(),
                _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            "bhkConvexVerticesShape" => ShapeTessellator.ConvexHull(ReadConvexVertices(shape)),

            _ => null
        };

        /// <summary>
        /// A convex shape's vertices, which are stored as Vector4 with the fourth
        /// component unused.
        /// </summary>
        private List<NifVector3> ReadConvexVertices(NifItem shape)
        {
            var points = new List<NifVector3>();

            if (_model.FindItem(shape, "Vertices") is not { } vertices)
                return points;

            foreach (NifItem item in vertices.Children)
            {
                NifVector4 v = item.Value.Get<NifVector4>();
                points.Add(new NifVector3(v.X, v.Y, v.Z));
            }

            return points;
        }

        private static NifMatrix33 RotationFromQuaternion(NifQuat q)
        {
            float x = q.X, y = q.Y, z = q.Z, w = q.W;

            return new NifMatrix33
            {
                M11 = 1f - 2f * (y * y + z * z),
                M12 = 2f * (x * y + z * w),
                M13 = 2f * (x * z - y * w),
                M21 = 2f * (x * y - z * w),
                M22 = 1f - 2f * (x * x + z * z),
                M23 = 2f * (y * z + x * w),
                M31 = 2f * (x * z + y * w),
                M32 = 2f * (y * z - x * w),
                M33 = 1f - 2f * (x * x + y * y)
            };
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
