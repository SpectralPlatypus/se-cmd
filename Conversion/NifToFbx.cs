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

        /// <summary>Emit the model's transform animation as FBX animation stacks.</summary>
        public bool ExportAnimation { get; set; } = true;
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

            // After the tree, because a track binds to a model by name and every
            // model has to exist before anything can be bound to it.
            // Both need the whole tree: a constraint joins two bodies and a track
            // binds to a model by name, so nothing can be bound until every node
            // that could be its target exists.
            if (_options.ExportCollision)
                ConvertConstraints(scene);

            if (_options.ExportAnimation)
                ConvertAnimation(scene);

            scene.Flush();
            return document;
        }

        /// <summary>The rigid bodies converted so far, and the nodes standing for them.</summary>
        private readonly Dictionary<NifItem, (FbxObject Node, string Name)> _bodies = [];

        /// <summary>
        /// Emits the constraints between converted rigid bodies.
        /// </summary>
        /// <remarks>
        /// A constraint is listed by the bodies it joins, and a chain by every body
        /// along it, so the same block is reached more than once and is written the
        /// first time only.
        /// </remarks>
        private void ConvertConstraints(FbxScene scene)
        {
            var written = new HashSet<NifItem>();

            foreach (NifItem body in _bodies.Keys.ToList())
            {
                foreach (NifItem constraint in _model.GetRefArray(body, "Constraints"))
                {
                    if (!written.Add(constraint))
                        continue;

                    if (FbxConstraintWriter.AddConstraint(scene, _model, constraint, _bodies) is null)
                        Warnings.Add($"{constraint.Name}: neither body was converted, the constraint is dropped");
                }
            }
        }

        /// <summary>Writes the model's sequences as FBX animation stacks.</summary>
        private void ConvertAnimation(FbxScene scene)
        {
            foreach (AnimSequence sequence in _model.ReadAnimations())
            {
                foreach (string missing in FbxAnimWriter.AddSequence(scene, sequence, _modelsByName))
                    Warnings.Add($"{sequence.Name}: no node named \"{missing}\", its animation is dropped");
            }
        }

        /// <summary>
        /// The converted models by their NIF name, for binding animation to.
        /// </summary>
        /// <remarks>
        /// Keyed on the unsanitised name, because that is what a controlled block
        /// names its target with.
        /// </remarks>
        private readonly Dictionary<string, FbxObject> _modelsByName = new(StringComparer.Ordinal);

        /// <summary>Records a converted block under its NIF name, first one wins.</summary>
        /// <remarks>
        /// Duplicate names are legal in a NIF and a controlled block cannot tell them
        /// apart either, so binding to the first is as much as the format allows.
        /// </remarks>
        private void Remember(NifItem block, FbxObject node)
        {
            string name = _model.GetName(block);

            if (name.Length > 0)
                _modelsByName.TryAdd(name, node);
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
            //
            // Two unrelated block families carry it. NiTriBasedGeom keeps its data
            // in a separate NiTriShapeData; BSTriShape, which Skyrim SE uses, packs
            // everything inline and inherits NiAVObject directly rather than
            // NiTriBasedGeom, so it needs testing for separately.
            if (_model.BlockInherits(block, "NiTriBasedGeom") || _model.BlockInherits(block, "BSTriShape"))
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
            Remember(block, node);

            FbxNodeType.Write(node, block);

            // A particle system has no geometry to export -- its vertices are a
            // runtime buffer the file only sizes -- so it stays an empty node with
            // the system carried alongside it.
            if (FbxParticleWriter.IsParticleSystem(_model, block))
                FbxParticleWriter.AddParticleSystem(scene, node, _model, block);

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
                transform = new NifTransform(scaled, NifTransform.RotationFromQuaternion(rotation), 1f);
            }

            FbxObject bodyNode = FbxMeshWriter.AddModel(scene, name + suffix, "Null", transform);
            scene.Connect(bodyNode, parent);

            // Constraints join two bodies and are emitted once the walk has seen
            // both, so the bodies are remembered as they are converted.
            _bodies[body] = (bodyNode, name + suffix);

            NifItem? shape = _model.GetRef(body, "Shape");

            if (shape is null)
            {
                Warnings.Add($"{name}: collision body has no shape");
                return;
            }

            RememberOwner(shape, body);
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

            AddCollisionMaterial(scene, shape, holder, geometry);
        }

        /// <summary>
        /// Attaches the shape's Havok material to its mesh, as ck-cmd does.
        /// </summary>
        /// <remarks>
        /// Nothing in the tessellated triangles records whether the shape is wood or
        /// stone, and the engine reads that for footstep sound and impact response. It
        /// travels as an FBX material named after the enum, which a DCC tool can show
        /// and edit. Materials are shared between shapes that agree, so a file with one
        /// material comes back with one.
        /// </remarks>
        private void AddCollisionMaterial(
            FbxScene scene, NifItem shape, FbxObject holder, FbxObject geometry)
        {
            string material = FbxCollisionMaterial.NameOf(_model, shape);

            if (material.Length == 0)
                return;

            string layer = FbxCollisionMaterial.LayerOf(_model, _shapeOwners.GetValueOrDefault(shape));
            string key = $"{material}/{layer}";

            if (!_collisionMaterials.TryGetValue(key, out FbxObject? fbxMaterial))
            {
                fbxMaterial = scene.AddObject("Material", material, string.Empty);
                fbxMaterial.Node.Nodes.Add(new FbxNode("Version", 102));
                fbxMaterial.Node.Nodes.Add(new FbxNode("ShadingModel", "Phong"));
                fbxMaterial.Node.Nodes.Add(new FbxNode("MultiLayer", 0));

                fbxMaterial.Properties.Set(
                    FbxCollisionMaterial.LayerProperty, "KString", "", FbxProperties.UserFlags, layer);

                _collisionMaterials[key] = fbxMaterial;
            }

            scene.Connect(fbxMaterial, holder);
            FbxMeshWriter.AddSingleMaterialElement(geometry);
        }

        /// <summary>
        /// Records which body a shape belongs to, following the tree down.
        /// </summary>
        /// <remarks>
        /// The collision layer lives on the body's filter, not on the shape, so a leaf
        /// several containers below still has to find the body above it.
        /// </remarks>
        private void RememberOwner(NifItem shape, NifItem body, int depth = 0)
        {
            if (depth > 16 || !_shapeOwners.TryAdd(shape, body))
                return;

            foreach (NifItem child in ChildShapesOf(shape))
                RememberOwner(child, body, depth + 1);
        }

        /// <summary>Collision materials emitted so far, keyed by material and layer.</summary>
        private readonly Dictionary<string, FbxObject> _collisionMaterials = new(StringComparer.Ordinal);

        /// <summary>The body each shape hangs from, for the layer its filter names.</summary>
        private readonly Dictionary<NifItem, NifItem> _shapeOwners = [];

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

            "bhkCompressedMeshShape" => ReadCompressedMesh(shape),

            _ => null
        };

        /// <summary>
        /// Decodes a <c>bhkCompressedMeshShape</c> back into triangles (spec §4.8.1).
        /// </summary>
        /// <remarks>
        /// The mesh is stored in two parts. "Big" vertices and triangles sit in the
        /// data block directly, at full precision. Everything else is chunked:
        /// vertices are 16-bit offsets from a per-chunk origin, scaled by 1/1000 and
        /// placed by a shared transform, and the triangles are held partly as strips
        /// and partly as a plain index list.
        /// </remarks>
        private MeshGeometry? ReadCompressedMesh(NifItem shape)
        {
            NifItem? data = _model.GetRef(shape, "Data");

            if (data is null)
                return null;

            var mesh = new MeshGeometry();

            // Big geometry is stored ready to use.
            if (_model.FindItem(data, "Big Verts") is { } bigVerts)
            {
                foreach (NifItem item in bigVerts.Children)
                {
                    NifVector4 v = item.Value.Get<NifVector4>();
                    mesh.Vertices.Add(new NifVector3(v.X, v.Y, v.Z));
                }
            }

            if (_model.FindItem(data, "Big Tris") is { } bigTris)
            {
                foreach (NifItem item in bigTris.Children)
                {
                    NifTriangle t = _model.FindItem(item, "Triangle")?.Value.Get<NifTriangle>() ?? default;

                    if (t.V1 < mesh.Vertices.Count && t.V2 < mesh.Vertices.Count && t.V3 < mesh.Vertices.Count)
                        mesh.Triangles.Add(t);
                }
            }

            var transforms = new List<NifTransform>();

            if (_model.FindItem(data, "Chunk Transforms") is { } chunkTransforms)
            {
                foreach (NifItem item in chunkTransforms.Children)
                {
                    NifVector4 t = _model.FindItem(item, "Translation")?.Value.Get<NifVector4>() ?? default;
                    NifQuat r = _model.FindItem(item, "Rotation")?.Value.Get<NifQuat>() ?? NifQuat.Identity;

                    transforms.Add(new NifTransform(
                        new NifVector3(t.X, t.Y, t.Z), NifTransform.RotationFromQuaternion(r), 1f));
                }
            }

            if (_model.FindItem(data, "Chunks") is not { } chunks)
                return mesh;

            foreach (NifItem chunk in chunks.Children)
            {
                NifVector4 origin = _model.FindItem(chunk, "Translation")?.Value.Get<NifVector4>() ?? default;
                int transformIndex = (int)_model.GetUInt(chunk, "Transform Index");

                NifTransform placement = transformIndex >= 0 && transformIndex < transforms.Count
                    ? transforms[transformIndex]
                    : NifTransform.Identity;

                var offsets = ReadUShorts(chunk, "Vertices");
                var indices = ReadUShorts(chunk, "Indices");
                var strips = ReadUShorts(chunk, "Strips");

                int firstVertex = mesh.Vertices.Count;

                // Vertices are millimetre offsets from the chunk's own origin.
                for (int i = 0; i + 2 < offsets.Count; i += 3)
                {
                    var local = new NifVector3(
                        origin.X + offsets[i] / 1000f,
                        origin.Y + offsets[i + 1] / 1000f,
                        origin.Z + offsets[i + 2] / 1000f);

                    mesh.Vertices.Add(placement.Apply(local));
                }

                int at = 0;

                // Strips first, alternating winding as a triangle strip does.
                foreach (ushort length in strips)
                {
                    for (int f = 0; f + 2 < length; f++)
                    {
                        if (at + f + 2 >= indices.Count)
                            break;

                        int a = firstVertex + indices[at + f];
                        int b = firstVertex + indices[at + f + 1];
                        int c = firstVertex + indices[at + f + 2];

                        mesh.Triangles.Add((f & 1) == 1
                            ? new NifTriangle((ushort)c, (ushort)b, (ushort)a)
                            : new NifTriangle((ushort)a, (ushort)b, (ushort)c));
                    }

                    at += length;
                }

                // Whatever follows the strips is a plain triangle list.
                for (int f = at; f + 2 < indices.Count; f += 3)
                {
                    mesh.Triangles.Add(new NifTriangle(
                        (ushort)(firstVertex + indices[f]),
                        (ushort)(firstVertex + indices[f + 1]),
                        (ushort)(firstVertex + indices[f + 2])));
                }
            }

            mesh.RecalculateNormals();
            return mesh;
        }

        private List<ushort> ReadUShorts(NifItem parent, string field)
        {
            var values = new List<ushort>();

            if (_model.FindItem(parent, field) is not { } array)
                return values;

            foreach (NifItem item in array.Children)
                values.Add((ushort)item.Value.ToUInt());

            return values;
        }

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

        private void ConvertGeometry(FbxScene scene, NifItem shape, FbxObject? parent)
        {
            MeshGeometry? mesh;

            if (_model.BlockInherits(shape, "BSTriShape"))
            {
                mesh = ReadBsTriShapeGeometry(shape);
            }
            else
            {
                NifItem? data = _model.GetRef(shape, "Data");

                if (data is null)
                {
                    Warnings.Add($"{_model.GetName(shape)} has no geometry data");
                    return;
                }

                mesh = ReadGeometry(shape, data);
            }

            if (mesh is null)
            {
                Warnings.Add($"{_model.GetName(shape)} has no readable geometry");
                return;
            }

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

            ConvertSkin(scene, shape, geometry);

            if (ReadMaterial(shape, name) is { } material)
            {
                FbxObject fbxMaterial = FbxMaterialWriter.AddMaterial(scene, material, _options.TexturePath);

                // A material belongs to the node carrying the mesh, not the mesh,
                // and the geometry's material element points at index 0.
                scene.Connect(fbxMaterial, holder);
                FbxMeshWriter.AddSingleMaterialElement(geometry);
            }

            // A flipbook controller hangs off a property rather than off the shape,
            // but the node is what an importer has to put it back on.
            FbxFlipWriter.AddFlipControllers(holder, _model, shape);

            _built[shape] = holder;

            // The holder is the node with the transform, so it is what an animation
            // track has to drive; the geometry under it never moves on its own.
            Remember(shape, holder);
        }

        /// <summary>
        /// Attaches a skin to a converted mesh, if the shape has one.
        /// </summary>
        /// <remarks>
        /// Bones are FBX Models, so they must already exist. They do: a bone is a
        /// NiNode somewhere in the hierarchy, and the walk converts the whole tree
        /// before any geometry beneath it. A bone the walk never reached is reported
        /// rather than silently dropping that bone's influence.
        /// </remarks>
        private void ConvertSkin(FbxScene scene, NifItem shape, FbxObject geometry)
        {
            SkinData? skin = _model.ReadSkin(shape);

            if (skin is null)
                return;

            var bones = new Dictionary<string, FbxObject>(StringComparer.Ordinal);

            foreach ((NifItem block, FbxObject node) in _built)
            {
                if (node.Class != "Model")
                    continue;

                string name = _model.GetName(block);

                if (name.Length > 0)
                    bones[name] = node;
            }

            foreach (string problem in FbxSkinIO.AddSkin(scene, geometry, skin, bones, NifTransform.Identity))
                Warnings.Add($"{_model.GetName(shape)}: {problem}");
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

        /// <summary>
        /// Reads a <c>BSTriShape</c>, which packs its vertex data inline rather than
        /// in a separate data block.
        /// </summary>
        /// <remarks>
        /// Each vertex is a struct whose fields are present or absent according to
        /// the flags in <c>Vertex Desc</c>, and whose positions may be full floats
        /// or halves depending on the same flags. The reader already resolves all of
        /// that — the array is a "fixed compound", so the layout is decided once
        /// from the first element — which leaves only reading the values out.
        ///
        /// The bitangent is the awkward one: it is split across three separate
        /// fields, X alongside the position and Y and Z alongside the normal and
        /// tangent, because it is packed into the spare lanes of those vectors.
        /// </remarks>
        private MeshGeometry? ReadBsTriShapeGeometry(NifItem shape)
        {
            NifItem? vertexData = _model.FindItem(shape, "Vertex Data");

            // Each entry is a partition and the vertex map that translates its
            // triangle indices; a shape holding its own geometry has one entry with
            // no map.
            var triangleSources = new List<(NifItem Source, List<ushort>? VertexMap)>();

            // A skinned Skyrim SE shape keeps nothing in itself: the vertex data and
            // the triangles both live in the skin partition, and the shape's own
            // counts are zero. Follow the skin when that is the case.
            if (vertexData is null || vertexData.Children.Count == 0)
            {
                NifItem? partition = FindSkinPartition(shape);

                if (partition is null)
                    return null;

                // The vertex array is shared by every partition; only the triangles
                // and the maps into it are per partition.
                vertexData = _model.FindItem(partition, "Vertex Data");

                if (_model.FindItem(partition, "Partitions") is { } partitions)
                {
                    foreach (NifItem entry in partitions.Children)
                    {
                        List<ushort>? map = null;

                        if (_model.FindItem(entry, "Vertex Map") is { } mapItem)
                        {
                            map = [];

                            foreach (NifItem vertex in mapItem.Children)
                                map.Add((ushort)vertex.Value.ToUInt());
                        }

                        triangleSources.Add((entry, map));
                    }
                }
            }
            else
            {
                triangleSources.Add((shape, null));
            }

            if (vertexData is null || vertexData.Children.Count == 0)
                return null;

            var mesh = new MeshGeometry();
            NifTransform transform = _model.GetTransform(shape);

            // Which attributes are present is fixed for the whole array.
            NifItem first = vertexData.Children[0];

            bool hasNormals = _model.FindItem(first, "Normal") is not null;
            bool hasTangents = _model.FindItem(first, "Tangent") is not null;
            bool hasUvs = _model.FindItem(first, "UV") is not null;
            bool hasColors = _model.FindItem(first, "Vertex Colors") is not null;

            foreach (NifItem vertex in vertexData.Children)
            {
                NifVector3 position = _model.FindItem(vertex, "Vertex")?.Value.Get<NifVector3>() ?? new NifVector3();
                mesh.Vertices.Add(transform.Apply(position));

                if (hasNormals)
                {
                    NifVector3 normal = _model.FindItem(vertex, "Normal")?.Value.Get<NifVector3>() ?? new NifVector3();
                    mesh.Normals.Add(transform.ApplyDirection(normal));
                }

                if (hasTangents)
                {
                    NifVector3 tangent = _model.FindItem(vertex, "Tangent")?.Value.Get<NifVector3>() ?? new NifVector3();
                    mesh.Tangents.Add(transform.ApplyDirection(tangent));

                    // Reassembled from the three lanes it was packed into.
                    var bitangent = new NifVector3(
                        _model.FindItem(vertex, "Bitangent X")?.Value.ToFloat() ?? 0f,
                        ByteToSNorm(_model.FindItem(vertex, "Bitangent Y")),
                        ByteToSNorm(_model.FindItem(vertex, "Bitangent Z")));

                    mesh.Bitangents.Add(transform.ApplyDirection(bitangent));
                }

                if (hasUvs)
                {
                    NifVector2 uv = _model.FindItem(vertex, "UV")?.Value.Get<NifVector2>() ?? new NifVector2();
                    mesh.Uvs.Add(new NifVector2(uv.X, 1f - uv.Y));
                }

                if (hasColors)
                    mesh.Colors.Add(_model.FindItem(vertex, "Vertex Colors")?.Value.Get<NifColor4>()
                                    ?? new NifColor4(1f, 1f, 1f, 1f));
            }

            // Every partition contributes triangles over the shared vertex array, so
            // the mesh is the union of them all. Converting only the first drops
            // whole sections of anything split across several, which real armour
            // routinely is.
            foreach ((NifItem source, List<ushort>? vertexMap) in triangleSources)
            {
                if (_model.FindItem(source, "Triangles") is not { } triangles)
                    continue;

                foreach (NifItem item in triangles.Children)
                {
                    NifTriangle t = item.Value.Get<NifTriangle>();

                    // Partition triangles index the partition's own vertex list.
                    if (vertexMap is not null)
                    {
                        if (t.V1 >= vertexMap.Count || t.V2 >= vertexMap.Count || t.V3 >= vertexMap.Count)
                            continue;

                        t = new NifTriangle(vertexMap[t.V1], vertexMap[t.V2], vertexMap[t.V3]);
                    }

                    if (t.V1 < mesh.Vertices.Count && t.V2 < mesh.Vertices.Count && t.V3 < mesh.Vertices.Count)
                        mesh.Triangles.Add(t);
                }
            }

            return mesh;
        }

        /// <summary>The skin partition a shape's geometry lives in, if it is skinned.</summary>
        private NifItem? FindSkinPartition(NifItem shape)
        {
            NifItem? skin = _model.GetRef(shape, "Skin");

            if (skin is null)
                return null;

            // The partition may hang off the skin instance or off its data.
            if (_model.GetRef(skin, "Skin Partition") is { } fromInstance)
                return fromInstance;

            NifItem? data = _model.GetRef(skin, "Data");

            return data is null ? null : _model.GetRef(data, "Skin Partition");
        }

        /// <summary>
        /// Expands a packed byte back to the -1..1 range, as the vertex formats
        /// store the spare bitangent lanes.
        /// </summary>
        private static float ByteToSNorm(NifItem? item) =>
            item is null ? 0f : (float)(item.Value.ToUInt() / 255.0 * 2.0 - 1.0);
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
