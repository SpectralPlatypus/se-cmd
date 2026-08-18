using SECmd.Havok;
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

        /// <summary>
        /// Target Skyrim Legendary Edition rather than Special Edition.
        /// </summary>
        /// <remarks>
        /// nif.xml distinguishes the two only by the Bethesda stream version: both
        /// are file version 20.2.0.7 with user version 12, LE being
        /// <c>V20_2_0_7_SKY</c> at 83 and SE <c>V20_2_0_7_SSE</c> at 100.
        ///
        /// That one number changes which geometry block is legal. BSTriShape is
        /// declared <c>versions="#SSE# #FO4# #F76#"</c> and so does not exist in LE,
        /// while NiTriShape is unrestricted. Writing NiTriShape into an SE file
        /// parses, but is not what the engine expects — converting between the two
        /// is the entire purpose of SSE NIF Optimizer.
        /// </remarks>
        public bool LegendaryEdition { get; set; }

        /// <summary>Rebuild the scene's animation stacks as NIF controller sequences.</summary>
        public bool ImportAnimation { get; set; } = true;

        /// <summary>Rebuild Havok constraints from the scene's attachment points.</summary>
        public bool ImportConstraints { get; set; } = true;

        /// <summary>The Bethesda stream version implied by the target edition.</summary>
        public uint BSVersion => LegendaryEdition ? 83u : 100u;
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
    /// Collision nodes (the <c>_rb</c> and <c>_sp</c> suffixes) become collision
    /// objects attached to their parent rather than children of it (spec §5.7).
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

            // The root's kind is carried like any other node's, and matters more:
            // BSXFlags asks twice whether the root is exactly NiNode.
            var sceneRoots = _scene.RootModels().ToList();

            string rootType = sceneRoots.Count == 1 && !HasGeometry(sceneRoots[0])
                ? FbxNodeType.Read(sceneRoots[0], _model, "BSFadeNode")
                : "BSFadeNode";

            NifItem root = _model.InsertBlock(rootType);

            // Named after the file rather than after any node in the scene (§5.2).
            _model.SetString(root, "Name", _options.RootName);
            _nodesByName[_options.RootName] = root;

            var rootModels = sceneRoots;
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

            // Collision sitting directly under the scene root belongs to the root
            // block, and would otherwise be left unattached.
            BuildCollisionFrom(root, 0);

            // Skins are wired up last: a bone is a node elsewhere in the scene, so
            // they can only be resolved once the whole tree exists.
            BuildPendingSkins(root);

            // A particle system's emitter and gravity objects are nodes elsewhere in
            // the scene, which the walk may not have reached when the system was built.
            ResolveParticleLinks();

            // Constraints join two bodies, so they wait until every body exists.
            if (_options.ImportConstraints)
                _model.WriteConstraints(_scene.ReadConstraints(), _bodiesByName, Warnings);

            // Animation last of all, for the same reason: a track names the node it
            // moves, and the manager has to list blocks that already exist.
            if (_options.ImportAnimation)
                _model.WriteAnimations(root, _scene.ReadAnimations(), _nodesByName, Warnings);

            // Last, because it is an answer about the finished graph.
            AddBsxFlags(root);

            _model.SetRoots([root]);
            _model.UpdateHeader();

            return _model;
        }

        /// <summary>
        /// Hangs a calculated <c>BSXFlags</c> off the root.
        /// </summary>
        /// <remarks>
        /// Every bit is a fact about the block graph -- whether it animates, collides,
        /// is a skeleton, is one collision or many -- so the value is worked out from
        /// what was just built rather than carried across from the source file, which
        /// would describe a graph this is not. See `docs/bsxflags-spec.md`.
        ///
        /// The root has to be linked before the calculation runs, because the walk
        /// behind bits 5 and 7 starts from the footer, and the block itself has to be
        /// attached afterwards so it does not appear in the graph it describes.
        /// </remarks>
        private void AddBsxFlags(NifItem root)
        {
            _model.SetRoots([root]);

            uint flags = _model.Calculate();

            NifItem bsx = _model.InsertBlock("BSXFlags");

            _model.SetString(bsx, "Name", NifBsxFlags.BlockName);
            _model.FindItem(bsx, "Integer Data")?.Value.SetCount(flags);

            AddExtraData(root, bsx);
        }

        /// <summary>Appends one block to another's extra data list.</summary>
        private void AddExtraData(NifItem block, NifItem extra)
        {
            var existing = _model.GetRefArray(block, "Extra Data List").ToList();

            NifItem? array = _model.SetArraySize(
                block, "Num Extra Data List", "Extra Data List", existing.Count + 1);

            if (array is null)
                return;

            for (int i = 0; i < existing.Count; i++)
                array.Children[i].Value.SetLink(_model.IndexOf(existing[i]));

            array.Children[existing.Count].Value.SetLink(_model.IndexOf(extra));
        }

        /// <summary>
        /// Turns one FBX Model into a NIF block, recursing into its children.
        /// </summary>
        private void ConvertModel(FbxObject model, List<NifItem> into)
        {
            string name = NameEncoding.Unsanitize(model.Name);

            // Collision bodies are leaves keyed off their name suffix, not ordinary
            // nodes, and are attached to their parent rather than listed as a child.
            if (name.EndsWith("_rb", StringComparison.Ordinal) || name.EndsWith("_sp", StringComparison.Ordinal))
            {
                _pendingCollision.Add(model);
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

            // An attachment point is a marker, not a node: it says where a joint is
            // and is rebuilt as part of the body that owns it.
            if (FbxConstraintReader.IsAttachmentPoint(model))
                return;

            // A modifier node is part of the particle system above it, which built it
            // already. Left to the walk it would become an empty NiNode instead.
            if (FbxParticleWriter.IsModifierNode(model))
                return;

            // A node carrying a particle system becomes the system rather than a
            // NiNode: it is the same node, and emitting both would leave the system
            // parented under a copy of itself.
            // A node is rebuilt as whatever kind of node it was. FBX has one kind
            // and NIF has a dozen, and they differ in what the engine does with them
            // rather than in where they sit.
            string blockType = FbxNodeType.Read(model, _model, "NiNode");

            NifItem node = NifParticleWriter.HasParticleSystem(model)
                ? _model.WriteParticleSystem(_scene, model, name, Warnings, _pendingParticleLinks)
                  ?? _model.InsertBlock(blockType)
                : _model.InsertBlock(blockType);

            _model.SetString(node, "Name", name);
            _model.SetTransform(node, transform);
            _nodesByName[name] = node;

            // Collision found under this node attaches to it rather than becoming a
            // child, so collect it before recursing into the real children.
            int collisionMark = _pendingCollision.Count;

            var nodeChildren = new List<NifItem>();

            foreach (FbxObject geometry in geometries)
            {
                if (BuildShape(geometry, model, NifTransform.Identity) is { } shape)
                    nodeChildren.Add(shape);
            }

            foreach (FbxObject child in childModels)
                ConvertModel(child, nodeChildren);

            AttachChildren(node, nodeChildren);
            BuildCollisionFrom(node, collisionMark);
            into.Add(node);
        }

        /// <summary>Collision bodies seen since <paramref name="mark"/>, awaiting a node to attach to.</summary>
        private readonly List<FbxObject> _pendingCollision = [];

        /// <summary>The rigid bodies built so far, by the node name they came from.</summary>
        private readonly Dictionary<string, NifItem> _bodiesByName = new(StringComparer.Ordinal);

        /// <summary>Particle links naming a node, waiting for that node to exist.</summary>
        private readonly List<NifParticleWriter.PendingParticleLink> _pendingParticleLinks = [];

        /// <summary>
        /// Points a particle system's links at the nodes they name.
        /// </summary>
        /// <remarks>
        /// An emitter that has lost its emitter object emits from the origin and a
        /// gravity modifier that has lost its gravity object pulls towards it, and
        /// neither shows up as anything but the effect being wrong.
        /// </remarks>
        private void ResolveParticleLinks()
        {
            foreach (NifParticleWriter.PendingParticleLink pending in _pendingParticleLinks)
            {
                if (_nodesByName.TryGetValue(pending.TargetName, out NifItem? target))
                    pending.Link.Value.SetLink(_model.IndexOf(target));
                else
                    Warnings.Add(
                        $"{pending.Context}: no node named \"{pending.TargetName}\", "
                        + "the particle system's reference to it is dropped");
            }

            _pendingParticleLinks.Clear();
        }

        /// <summary>
        /// Builds the collision object for a node from any bodies found beneath it.
        /// </summary>
        /// <remarks>
        /// A NIF node holds exactly one collision object, so when several bodies sit
        /// under one node only the first becomes it and the rest are reported. That
        /// is rare enough in practice to be worth saying rather than silently
        /// merging or dropping.
        /// </remarks>
        private void BuildCollisionFrom(NifItem node, int mark)
        {
            if (_pendingCollision.Count <= mark)
                return;

            var bodies = _pendingCollision.GetRange(mark, _pendingCollision.Count - mark);
            _pendingCollision.RemoveRange(mark, _pendingCollision.Count - mark);

            for (int i = 1; i < bodies.Count; i++)
                Warnings.Add($"{NameEncoding.Unsanitize(bodies[i].Name)}: only one collision body per node, ignored");

            if (BuildRigidBody(bodies[0]) is { } collision)
            {
                _model.SetRef(node, "Collision Object", collision);
                _model.SetRef(collision, "Target", node);
            }
        }

        /// <summary>
        /// Builds a collision object and its body from an <c>_rb</c> or <c>_sp</c> node.
        /// </summary>
        private NifItem? BuildRigidBody(FbxObject bodyNode)
        {
            string name = NameEncoding.Unsanitize(bodyNode.Name);
            bool isPhantom = name.EndsWith("_sp", StringComparison.Ordinal);

            NifItem? shape = BuildShapeFrom(bodyNode, name);

            if (shape is null)
            {
                Warnings.Add($"{name}: no collision shape found beneath it");
                return null;
            }

            if (isPhantom)
            {
                NifItem phantomCollision = _model.InsertBlock("bhkSPCollisionObject");
                NifItem phantom = _model.InsertBlock("bhkSimpleShapePhantom");

                _model.SetRef(phantom, "Shape", shape);
                _model.SetRef(phantomCollision, "Body", phantom);

                return phantomCollision;
            }

            NifItem collision = _model.InsertBlock("bhkCollisionObject");

            // bhkRigidBodyT applies its own transform; the plain body ignores it.
            NifItem body = _model.InsertBlock("bhkRigidBodyT");

            // A constraint names the bodies it joins by the node they came from.
            _bodiesByName[name] = body;

            _model.SetRef(body, "Shape", shape);
            WriteBodyTransform(body, bodyNode);
            WriteStaticMotion(body);

            _model.SetRef(collision, "Body", body);

            return collision;
        }

        /// <summary>
        /// Writes a body's placement, converting Skyrim units back to Havok metres.
        /// </summary>
        private void WriteBodyTransform(NifItem body, FbxObject bodyNode)
        {
            NifTransform transform = ReadTransform(bodyNode);
            NifVector3 t = transform.Translation;

            _model.FindItem(body, @"Rigid Body Info\Translation")?.Value.Set(new NifVector4(
                t.X * ShapeTessellator.BhkScaleFactorInverse,
                t.Y * ShapeTessellator.BhkScaleFactorInverse,
                t.Z * ShapeTessellator.BhkScaleFactorInverse,
                0f));

            _model.FindItem(body, @"Rigid Body Info\Rotation")?.Value.Set(transform.ToQuaternion());
        }

        /// <summary>
        /// Applies the motion settings a static body needs (spec §5.7).
        /// </summary>
        /// <remarks>
        /// Statics also get zero mass and a zero inertia tensor. Leaving a mass on a
        /// static body makes Havok treat it as movable, which is how a piece of
        /// scenery ends up falling through the world.
        /// </remarks>
        private void WriteStaticMotion(NifItem body)
        {
            SetEnum(body, @"Rigid Body Info\Motion System", "Motion System", "MO_SYS_BOX_STABILIZED");
            SetEnum(body, @"Rigid Body Info\Solver Deactivation", "Solver Deactivation", "SOLVER_DEACTIVATION_OFF");
            SetEnum(body, @"Rigid Body Info\Quality Type", "Motion Quality", "MO_QUAL_INVALID");

            SetFloat(body, @"Rigid Body Info\Mass", 0f);

            // Havok wants the tensor cleared, not merely small.
            for (int row = 1; row <= 3; row++)
            {
                for (int column = 1; column <= 4; column++)
                    SetFloat(body, $@"Rigid Body Info\Inertia Tensor\m{row}{column}", 0f);
            }
        }

        /// <summary>
        /// Sets an enum field by option name, so the intent survives even though the
        /// numeric values differ between enums.
        /// </summary>
        private void SetEnum(NifItem block, string path, string enumType, string optionName)
        {
            NifItem? item = _model.FindItem(block, path);

            if (item is null)
                return;

            if (_model.Database.TryGetEnumOptionValue(item.Type, optionName, out uint value)
                || _model.Database.TryGetEnumOptionValue(enumType, optionName, out value))
            {
                item.Value.SetCount(value);
            }
        }

        /// <summary>
        /// Finds the shape beneath a body node and fits a Havok primitive to it,
        /// choosing which by the node's name suffix.
        /// </summary>
        private NifItem? BuildShapeFrom(FbxObject parent, string parentName, int depth = 0)
        {
            if (depth > 16)
            {
                Warnings.Add($"{parentName}: collision nodes nest too deeply, stopping");
                return null;
            }

            foreach (FbxObject child in _scene.ChildrenOf(parent.Id).Where(o => o.Class == "Model"))
            {
                string name = NameEncoding.Unsanitize(child.Name);

                // Containers pass straight through: the tree they describe is
                // rebuilt by Havok, so only the leaf shape matters here.
                if (name.EndsWith("_transform", StringComparison.Ordinal)
                    || name.EndsWith("_list", StringComparison.Ordinal)
                    || name.EndsWith("_convex_list", StringComparison.Ordinal)
                    || name.EndsWith("_mopp", StringComparison.Ordinal))
                {
                    if (BuildShapeFrom(child, name, depth + 1) is { } nested)
                        return nested;

                    continue;
                }

                if (ReadShapePoints(child) is not { Count: > 0 } points)
                    continue;

                // The suffix decides the primitive. Guessing from the geometry would
                // silently swap a sphere for a box: their tessellations are not
                // reliably distinguishable.
                NifItem? built = null;

                if (name.EndsWith("_box", StringComparison.Ordinal))
                    built = BuildBox(points);
                else if (name.EndsWith("_sphere", StringComparison.Ordinal))
                    built = BuildSphere(points);
                else if (name.EndsWith("_capsule", StringComparison.Ordinal))
                    built = BuildCapsule(points);
                else if (name.EndsWith("_convex", StringComparison.Ordinal))
                    built = BuildConvex(points);
                else if (name.EndsWith("_mesh", StringComparison.Ordinal))
                    built = BuildCompressedMesh(child, name);

                if (built is null)
                    continue;

                // Size comes back from the geometry; the material cannot, because
                // nothing in the triangles says wood rather than stone.
                ReadCollisionMaterial(built, child, name);

                return built;
            }

            return null;
        }

        /// <summary>
        /// The vertices of a collision node's mesh, converted back to Havok metres.
        /// </summary>
        private List<NifVector3>? ReadShapePoints(FbxObject node)
        {
            FbxObject? geometry = _scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry");

            if (geometry is null)
                return null;

            MeshGeometry? mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options
            {
                // Collision geometry carries no UVs, so the flips are irrelevant.
                InvertU = false,
                InvertV = false
            });

            if (mesh is null || mesh.IsEmpty)
                return null;

            var points = new List<NifVector3>(mesh.Vertices.Count);

            foreach (NifVector3 v in mesh.Vertices)
            {
                points.Add(new NifVector3(
                    v.X * ShapeTessellator.BhkScaleFactorInverse,
                    v.Y * ShapeTessellator.BhkScaleFactorInverse,
                    v.Z * ShapeTessellator.BhkScaleFactorInverse));
            }

            return points;
        }

        /// <summary>
        /// Restores the Havok material from the FBX material on the collision mesh.
        /// </summary>
        /// <remarks>
        /// The export names the material after the enum, as ck-cmd does, so a shape
        /// that came from a NIF arrives with its material spelled out and a shape
        /// authored in a DCC tool arrives with whatever the artist named it. An
        /// unrecognised name is reported rather than silently left as stone: the
        /// material decides footstep sound and impact response, and a wrong one is not
        /// visible in the mesh.
        /// </remarks>
        private void ReadCollisionMaterial(NifItem shape, FbxObject holder, string name)
        {
            FbxObject? material = _scene.ChildrenOf(holder.Id)
                .FirstOrDefault(o => o.Class == "Material");

            if (material is null)
                return;

            string spelled = NameEncoding.Unsanitize(material.Name);

            if (spelled.Length == 0 || FbxCollisionMaterial.Apply(_model, shape, spelled))
                return;

            Warnings.Add(
                $"{name}: \"{spelled}\" is not a Skyrim Havok material, "
                + "the shape keeps the default");
        }

        private NifItem BuildBox(IReadOnlyList<NifVector3> points)
        {
            (_, NifVector3 half) = ShapeFitter.FitBox(points);

            NifItem shape = _model.InsertBlock("bhkBoxShape");
            _model.FindItem(shape, "Dimensions")?.Value.Set(half);
            SetFloat(shape, "Radius", MathF.Min(half.X, MathF.Min(half.Y, half.Z)));

            return shape;
        }

        private NifItem BuildSphere(IReadOnlyList<NifVector3> points)
        {
            (_, float radius) = ShapeFitter.FitSphere(points);

            NifItem shape = _model.InsertBlock("bhkSphereShape");
            SetFloat(shape, "Radius", radius);

            return shape;
        }

        private NifItem BuildCapsule(IReadOnlyList<NifVector3> points)
        {
            (NifVector3 first, NifVector3 second, float radius) = ShapeFitter.FitCapsule(points);

            NifItem shape = _model.InsertBlock("bhkCapsuleShape");
            _model.FindItem(shape, "First Point")?.Value.Set(first);
            _model.FindItem(shape, "Second Point")?.Value.Set(second);
            SetFloat(shape, "Radius", radius);
            SetFloat(shape, "Radius 1", radius);
            SetFloat(shape, "Radius 2", radius);

            return shape;
        }

        /// <summary>
        /// Builds a <c>bhkCompressedMeshShape</c>, which needs Havok to chunk and
        /// quantise the mesh and to build the MOPP tree that indexes it.
        /// </summary>
        /// <remarks>
        /// This is the one shape that cannot be produced from open code: the chunk
        /// layout, the transforms and the MOPP tree all come out of the same Havok
        /// pass, so they have to be generated together by mopper. Without it, the
        /// shape is reported rather than approximated — a mesh collision fitted to a
        /// primitive would be silently wrong in a way that only shows up in game.
        /// </remarks>
        private NifItem? BuildCompressedMesh(FbxObject node, string name)
        {
            IMoppGenerator? generator = MoppGenerator.Resolve();

            if (generator is null)
            {
                Warnings.Add($"{name}: mesh collision needs MOPP generation. {MoppGenerator.DescribeUnavailability()}");
                return null;
            }

            FbxObject? geometry = _scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry");
            MeshGeometry? mesh = geometry is null
                ? null
                : FbxMeshReader.Read(geometry, new FbxMeshReader.Options { InvertU = false, InvertV = false });

            if (mesh is null || mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{name}: mesh collision node has no geometry");
                return null;
            }

            // Havok works in metres.
            var vertices = new List<NifVector3>(mesh.Vertices.Count);

            foreach (NifVector3 v in mesh.Vertices)
            {
                vertices.Add(new NifVector3(
                    v.X * ShapeTessellator.BhkScaleFactorInverse,
                    v.Y * ShapeTessellator.BhkScaleFactorInverse,
                    v.Z * ShapeTessellator.BhkScaleFactorInverse));
            }

            CompressedMeshResult? built = generator.GenerateCompressedMesh(
                [new MoppGeometry(vertices, mesh.Triangles)]);

            if (built is null)
            {
                Warnings.Add($"{name}: MOPP generation failed for the mesh collision shape");
                return null;
            }

            NifItem shape = _model.InsertBlock("bhkCompressedMeshShape");
            NifItem data = _model.InsertBlock("bhkCompressedMeshShapeData");

            SetFloat(shape, "Radius", 0.005f);
            SetFloat(shape, "Radius Copy", 0.005f);
            _model.FindItem(shape, "Scale")?.Value.Set(new NifVector4(1f, 1f, 1f, 0f));
            _model.FindItem(shape, "Scale Copy")?.Value.Set(new NifVector4(1f, 1f, 1f, 0f));
            _model.SetRef(shape, "Data", data);

            WriteCompressedMeshData(data, built);

            // Havok reaches the shape through a MOPP tree, never directly.
            NifItem mopp = _model.InsertBlock("bhkMoppBvTreeShape");
            _model.SetRef(mopp, "Shape", shape);
            WriteMoppCode(mopp, built.Mopp);

            return mopp;
        }

        /// <summary>Writes the MOPP tree and the quantisation it was built against.</summary>
        /// <remarks>
        /// The scale sits on the shape, the offset and the code inside the
        /// <c>MOPP Code</c> block. The code itself is a <em>binary</em> array, so it
        /// is one blob sized by <c>Data Size</c> rather than a byte per element.
        /// </remarks>
        private void WriteMoppCode(NifItem mopp, MoppResult result)
        {
            SetFloat(mopp, "Scale", result.Scale);

            _model.FindItem(mopp, @"MOPP Code\Offset")?.Value.Set(
                new NifVector4(result.Origin.X, result.Origin.Y, result.Origin.Z, 0f));

            // mopper builds with chunk subdivision enabled.
            SetEnum(mopp, @"MOPP Code\Build Type", "hkMoppCodeBuildType", "BUILT_WITH_CHUNK_SUBDIVISION");

            if (_model.SetArraySize(mopp, @"MOPP Code\Data Size", @"MOPP Code\Data", result.Code.Length)
                is { Children.Count: > 0 } blob)
            {
                blob.Children[0].Value.Set(result.Code);
            }
        }

        /// <summary>Writes the chunked mesh Havok produced.</summary>
        private void WriteCompressedMeshData(NifItem data, CompressedMeshResult built)
        {
            _model.FindItem(data, @"AABB\Min")?.Value.Set(built.BoundsMin);
            _model.FindItem(data, @"AABB\Max")?.Value.Set(built.BoundsMax);

            // The index widths Havok packs chunk vertices with.
            SetCount(data, "Bits Per Index", 17);
            SetCount(data, "Bits Per W Index", 18);
            SetCount(data, "Mask W Index", 262143);
            SetCount(data, "Mask Index", 131071);
            SetFloat(data, "Error", 0.001f);

            if (_model.SetArraySize(data, "Num Big Verts", "Big Verts", built.BigVertices.Count) is { } bigVerts)
            {
                for (int i = 0; i < built.BigVertices.Count && i < bigVerts.Children.Count; i++)
                    bigVerts.Children[i].Value.Set(built.BigVertices[i]);
            }

            if (_model.SetArraySize(data, "Num Big Tris", "Big Tris", built.BigTriangles.Count) is { } bigTris)
            {
                for (int i = 0; i < built.BigTriangles.Count && i < bigTris.Children.Count; i++)
                {
                    var (a, b, c, material, welding) = built.BigTriangles[i];
                    NifItem entry = bigTris.Children[i];

                    _model.FindItem(entry, "Triangle")?.Value.Set(new NifTriangle((ushort)a, (ushort)b, (ushort)c));
                    _model.FindItem(entry, "Material")?.Value.SetCount(material);
                    _model.FindItem(entry, "Welding Info")?.Value.SetCount(welding);
                }
            }

            if (_model.SetArraySize(data, "Num Transforms", "Chunk Transforms", built.Transforms.Count)
                is { } transforms)
            {
                for (int i = 0; i < built.Transforms.Count && i < transforms.Children.Count; i++)
                {
                    NifItem entry = transforms.Children[i];
                    _model.FindItem(entry, "Translation")?.Value.Set(built.Transforms[i].Translation);
                    _model.FindItem(entry, "Rotation")?.Value.Set(built.Transforms[i].Rotation);
                }
            }

            if (_model.SetArraySize(data, "Num Chunks", "Chunks", built.Chunks.Count) is not { } chunks)
                return;

            for (int i = 0; i < built.Chunks.Count && i < chunks.Children.Count; i++)
            {
                CompressedMeshChunk source = built.Chunks[i];
                NifItem chunk = chunks.Children[i];

                _model.FindItem(chunk, "Translation")?.Value.Set(source.Offset);
                _model.FindItem(chunk, "Material Index")?.Value.SetCount(source.MaterialInfo);
                _model.FindItem(chunk, "Transform Index")?.Value.SetCount(source.TransformIndex);

                // mopper prints a hard-coded 65535 here, which is what Havok expects.
                _model.FindItem(chunk, "Reference")?.Value.SetCount(65535);

                WriteUShorts(chunk, "Num Vertices", "Vertices", source.Vertices);
                WriteUShorts(chunk, "Num Indices", "Indices", source.Indices);
                WriteUShorts(chunk, "Num Strips", "Strips", source.StripLengths);
                WriteUShorts(chunk, "Num Welding Info", "Welding Info", source.WeldingInfo);
            }
        }

        private void WriteUShorts(NifItem parent, string countField, string arrayField, IReadOnlyList<ushort> values)
        {
            if (_model.SetArraySize(parent, countField, arrayField, values.Count) is not { } array)
                return;

            for (int i = 0; i < values.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetCount(values[i]);
        }

        private NifItem BuildConvex(IReadOnlyList<NifVector3> points)
        {
            (List<NifVector4> vertices, List<NifVector4> planes) = ShapeFitter.FitConvex(points);

            NifItem shape = _model.InsertBlock("bhkConvexVerticesShape");
            SetFloat(shape, "Radius", 0.01f);

            if (_model.SetArraySize(shape, "Num Vertices", "Vertices", vertices.Count) is { } vertexArray)
            {
                for (int i = 0; i < vertices.Count && i < vertexArray.Children.Count; i++)
                    vertexArray.Children[i].Value.Set(vertices[i]);
            }

            // Havok needs the face planes as well as the points; it does not derive
            // them from the hull.
            if (_model.SetArraySize(shape, "Num Normals", "Normals", planes.Count) is { } planeArray)
            {
                for (int i = 0; i < planes.Count && i < planeArray.Children.Count; i++)
                    planeArray.Children[i].Value.Set(planes[i]);
            }

            return shape;
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

            // BSTriShape does not exist before Skyrim SE, so the edition decides
            // which geometry block to emit.
            NifItem shape = _options.LegendaryEdition
                ? BuildNiTriShape(geometry, mesh)
                : BuildBsTriShape(geometry, mesh);

            _model.SetTransform(shape, transform);

            // A shape can be animated too, and its holder was unwrapped, so it has
            // to be findable by name. TryAdd, because a real node of the same name
            // is the better target of the two.
            _nodesByName.TryAdd(NameEncoding.Unsanitize(geometry.Name), shape);

            BuildMaterial(shape, holder);

            // After the material, since a flipbook controller joins the shader
            // property's chain and the shader property is what the material builds.
            if (NifFlipWriter.HasFlipControllers(holder))
            {
                _model.WriteFlipControllers(
                    holder, shape, _model.GetRef(shape, "Shader Property") ?? shape, Warnings);
            }

            // Deferred: the bones are nodes elsewhere in the scene and may not have
            // been converted yet, so skins are wired up once the whole tree is
            // built.
            if (FbxSkinIO.ReadSkin(_scene, geometry) is { } skin)
                _pendingSkins.Add((shape, skin, mesh.Vertices.Count, mesh.Triangles));

            return shape;
        }

        /// <summary>Skins waiting for the whole node tree to exist.</summary>
        /// <remarks>
        /// The triangles come along because a partition carries its own copy of
        /// them, remapped to the vertices that partition lists.
        /// </remarks>
        private readonly List<(NifItem Shape, SkinData Skin, int VertexCount, List<NifTriangle> Triangles)>
            _pendingSkins = [];

        /// <summary>Nodes by name, for resolving bones.</summary>
        private readonly Dictionary<string, NifItem> _nodesByName = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds every skin once the nodes its bones refer to exist.
        /// </summary>
        private void BuildPendingSkins(NifItem root)
        {
            foreach ((NifItem shape, SkinData skin, int vertexCount, var triangles) in _pendingSkins)
            {
                var missing = _model.WriteSkin(shape, skin, _nodesByName, root, vertexCount, triangles);

                foreach (string bone in missing)
                    Warnings.Add($"{_model.GetName(shape)}: no node named \"{bone}\", its influence is dropped");
            }

            _pendingSkins.Clear();
        }

        private NifItem BuildNiTriShape(FbxObject geometry, MeshGeometry mesh)
        {
            NifItem shape = _model.InsertBlock("NiTriShape");
            _model.SetString(shape, "Name", NameEncoding.Unsanitize(geometry.Name));

            NifItem data = _model.InsertBlock("NiTriShapeData");
            WriteGeometryData(data, mesh);

            _model.SetRef(shape, "Data", data);

            return shape;
        }

        /// <summary>
        /// Builds a <c>BSTriShape</c>, which packs its vertices inline rather than
        /// referencing a data block.
        /// </summary>
        /// <remarks>
        /// The layout is described by <c>Vertex Desc</c>: its top bits say which
        /// attributes each vertex carries, and its low nibbles record the stride and
        /// the offset of each attribute within a vertex. The array's fields are
        /// conditional on those same flags, so the descriptor has to be written
        /// before the array is sized or the elements come out the wrong shape.
        /// </remarks>
        private NifItem BuildBsTriShape(FbxObject geometry, MeshGeometry mesh)
        {
            NifItem shape = _model.InsertBlock("BSTriShape");
            _model.SetString(shape, "Name", NameEncoding.Unsanitize(geometry.Name));

            var descriptor = BuildVertexDescriptor(mesh);

            _model.FindItem(shape, "Vertex Desc")?.Value.SetCount(descriptor.Value);

            SetCount(shape, "Num Vertices", (uint)mesh.Vertices.Count);
            SetCount(shape, "Num Triangles", (uint)mesh.Triangles.Count);

            // Stored rather than derived, though nif.xml gives the formula.
            SetCount(shape, "Data Size",
                (uint)(descriptor.VertexSize * mesh.Vertices.Count + mesh.Triangles.Count * 6));

            (NifVector3 center, float radius) = mesh.ComputeBoundingSphere();
            _model.FindItem(shape, @"Bounding Sphere\Center")?.Value.Set(center);
            _model.FindItem(shape, @"Bounding Sphere\Radius")?.Value.SetFloat(radius);

            // The descriptor is set, so sizing now produces elements with the right
            // fields present.
            if (_model.FindItem(shape, "Vertex Data") is { } vertexData)
            {
                vertexData.InvalidateConditionsRecursive();
                _model.UpdateArraySize(vertexData);

                for (int i = 0; i < mesh.Vertices.Count && i < vertexData.Children.Count; i++)
                    WriteVertex(vertexData.Children[i], mesh, i);
            }

            if (_model.FindItem(shape, "Triangles") is { } triangles)
            {
                triangles.InvalidateConditionsRecursive();
                _model.UpdateArraySize(triangles);

                for (int i = 0; i < mesh.Triangles.Count && i < triangles.Children.Count; i++)
                    triangles.Children[i].Value.Set(mesh.Triangles[i]);
            }

            return shape;
        }

        /// <summary>Writes one packed vertex.</summary>
        private void WriteVertex(NifItem vertex, MeshGeometry mesh, int index)
        {
            _model.FindItem(vertex, "Vertex")?.Value.Set(mesh.Vertices[index]);

            if (mesh.HasUvs && index < mesh.Uvs.Count)
            {
                NifVector2 uv = mesh.Uvs[index];

                // Back to NIF's V direction.
                _model.FindItem(vertex, "UV")?.Value.Set(new NifVector2(uv.X, 1f - uv.Y));
            }

            if (mesh.HasNormals && index < mesh.Normals.Count)
                _model.FindItem(vertex, "Normal")?.Value.Set(mesh.Normals[index]);

            if (mesh.HasTangents && index < mesh.Tangents.Count)
            {
                _model.FindItem(vertex, "Tangent")?.Value.Set(mesh.Tangents[index]);

                // The bitangent is split across three lanes: X sits beside the
                // position, Y and Z beside the normal and tangent.
                NifVector3 bitangent = mesh.Bitangents[index];

                _model.FindItem(vertex, "Bitangent X")?.Value.SetFloat(bitangent.X);
                _model.FindItem(vertex, "Bitangent Y")?.Value.SetCount(SNormToByte(bitangent.Y));
                _model.FindItem(vertex, "Bitangent Z")?.Value.SetCount(SNormToByte(bitangent.Z));
            }

            if (mesh.HasColors && index < mesh.Colors.Count)
                _model.FindItem(vertex, "Vertex Colors")?.Value.Set(mesh.Colors[index]);
        }

        private static uint SNormToByte(float value) =>
            (uint)Math.Clamp(MathF.Round((value + 1f) / 2f * 255f), 0f, 255f);

        /// <summary>
        /// Works out the vertex descriptor for a mesh: which attributes are present,
        /// how large a vertex is, and where each attribute sits inside one.
        /// </summary>
        private static (ulong Value, int VertexSize) BuildVertexDescriptor(MeshGeometry mesh)
        {
            var flags = VertexFlags.Vertex;

            if (mesh.HasUvs)
                flags |= VertexFlags.UV;

            if (mesh.HasNormals)
                flags |= VertexFlags.Normal;

            if (mesh.HasTangents)
                flags |= VertexFlags.Tangent;

            if (mesh.HasColors)
                flags |= VertexFlags.Colors;

            // Field order and sizes follow BSVertexDataSSE: a full-precision
            // position, a float taking the fourth lane (the bitangent's X),
            // half-precision UVs, then signed bytes for the normal and tangent with
            // the rest of the bitangent packed into their spare lanes.
            //
            // The position has no offset member: it is always first.
            var desc = new BSVertexDesc { Flags = flags };
            uint offset = 16;

            if (mesh.HasUvs)
            {
                desc.Set(BSVertexDesc.Member.UV1Offset, offset);
                offset += 4;
            }

            if (mesh.HasNormals)
            {
                desc.Set(BSVertexDesc.Member.NormalOffset, offset);
                offset += 4;
            }

            if (mesh.HasTangents)
            {
                desc.Set(BSVertexDesc.Member.TangentOffset, offset);
                offset += 4;
            }

            if (mesh.HasColors)
            {
                desc.Set(BSVertexDesc.Member.ColorOffset, offset);
                offset += 4;
            }

            desc.VertexSize = offset;

            return (desc.Value, (int)offset);
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

            ReadUvTransform(shader, material);

            NifItem textureSet = BuildTextureSet(material);
            _model.SetRef(shader, "Texture Set", textureSet);

            _model.SetRef(shape, "Shader Property", shader);

            BuildAlphaProperty(shape, properties);
        }

        /// <summary>
        /// Recovers the shader's UV offset and scale from the material's textures.
        /// </summary>
        /// <remarks>
        /// FBX carries these per texture, as <c>ModelUVTranslation</c> and
        /// <c>ModelUVScaling</c>, while a NIF shader has one pair for all of its
        /// slots. The first texture that names them wins, which is the same pair the
        /// export wrote onto every slot.
        ///
        /// The default matters more than it looks. A shader is authored with an
        /// identity scale of one, not zero, and a zero here does not fail loudly --
        /// it multiplies every texture coordinate in the mesh to nothing.
        /// </remarks>
        private void ReadUvTransform(NifItem shader, FbxObject material)
        {
            var offset = new NifVector2(0f, 0f);
            var scale = new NifVector2(1f, 1f);

            foreach ((FbxObject texture, _) in _scene.PropertyConnectionsTo(material.Id))
            {
                if (Pair(texture, "ModelUVTranslation") is { } t)
                    offset = t;

                if (Pair(texture, "ModelUVScaling") is { } s)
                {
                    scale = s;
                    break;
                }
            }

            _model.FindItem(shader, "UV Offset")?.Value.Set(offset);
            _model.FindItem(shader, "UV Scale")?.Value.Set(scale);
        }

        /// <summary>Reads a two-double FBX record, if it is there and well formed.</summary>
        private static NifVector2? Pair(FbxObject texture, string name)
        {
            if (texture.Child(name) is not { } node || node.Properties.Count < 2)
                return null;

            try
            {
                return new NifVector2(
                    System.Convert.ToSingle(node.Properties[0]),
                    System.Convert.ToSingle(node.Properties[1]));
            }
            catch (Exception e) when (e is InvalidCastException or FormatException)
            {
                return null;
            }
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
