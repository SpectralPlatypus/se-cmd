using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// NIF to FBX and back, compared field by field.
    /// </summary>
    /// <remarks>
    /// The comparison walks both graphs from the root and follows references, so block
    /// order does not enter into it: what is being asked is whether the rebuilt file
    /// says the same things, not whether it says them in the same places.
    ///
    /// Byte identity is the goal and is not reached yet. What holds today, and what
    /// this pins, is that the ck-cmd example files come back with the same graph — the
    /// same blocks, of the same kinds, linked the same way — and differ only in the
    /// fields listed in <see cref="KnownGaps"/>. Each of those is either derived on
    /// import by design or a gap with a reason recorded against it.
    /// </remarks>
    public class RoundTripTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel RoundTrip(NifModel source)
        {
            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            var converter = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    // The root's name is a property of the file, and the option exists
                    // for FBX that never was a NIF. Carrying it keeps the comparison
                    // about the conversion rather than about the option.
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                });

            return converter.Convert(Db);
        }

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        /// <summary>
        /// Fields the round trip is not expected to reproduce, and why.
        /// </summary>
        /// <remarks>
        /// A field is listed by the name it ends in, since the same gap shows up under
        /// every shape in a file. Shrinking this list is the work; it exists so that
        /// anything *not* on it fails rather than being absorbed into a count.
        /// </remarks>
        public static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
        {
            // Derived on import by design. The size of a collision shape comes back
            // from its tessellated geometry, which is the half of the shape a DCC tool
            // can edit; carrying the original would ignore whatever was done to it.
            ["Radius"] = "refitted from the tessellated collision geometry",
            ["Dimensions"] = "refitted from the tessellated collision geometry",


            // Deliberately dropped, not lost. These bodies carry a mass on a layer
            // their own filter calls SKYL_STATIC, and a static with a mass is treated
            // as movable -- which is how scenery ends up falling through the world. So
            // the static profile zeroes both, as ck-cmd's does, and the source file
            // disagrees with itself rather than with the importer.
            ["Mass"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m11"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m22"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m33"] = "zeroed by the static motion profile, as ck-cmd does",

            // 0xCD in every byte is the debug heap's fill pattern: these are fields the
            // exporter that wrote the fixture never initialised. There is nothing to
            // reproduce.
            ["Auto Remove Level"] = "uninitialised in the source file (0xCD)",
            ["Response Modifier Flags"] = "uninitialised in the source file (0xCD)",
            ["Num Shape Keys in Contact Point"] = "uninitialised in the source file (0xCD)",
            ["Force Collided Onto PPU"] = "uninitialised in the source file (0xCD)",

            // Real gaps, each its own piece of work.
            ["Consistency Flags"] = "not carried",
            ["Shader Flags 2"] = "one flag differs; the shader flag words are not carried verbatim",
            ["Bounding Sphere"] = "recomputed rather than carried",
            ["Center"] = "recomputed rather than carried",
            // The hull is refitted, so its vertices and planes come back in the order
            // the fit produced rather than the order Havok emitted. The values agree:
            // the plane convention is checked directly, against a shipped hull, in
            // ConvexHullPlaneTests.
            ["Vertices"] = "convex hull refitted from the tessellation, so the order differs",
            ["Normals"] = "convex hull refitted from the tessellation, so the order differs",
        };

        public static TheoryData<string> CkCmdExamples() =>
            new("generate_rb.nif", "generate_rb_box.nif", "generate_rb_sphere.nif", "multi_material_cube.nif");

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void TheGraphSurvivesTheRoundTrip(string name)
        {
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            // Same blocks, of the same kinds. The comparison below follows references
            // and would miss a block that nothing points at.
            Assert.Equal(
                source.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal),
                rebuilt.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal));
        }

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void OnlyTheKnownGapsDiffer(string name)
        {
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            var unexplained = NifComparer.Compare(source, rebuilt)
                .Where(d => !KnownGaps.ContainsKey(d.Field))
                .ToList();

            Assert.True(
                unexplained.Count == 0,
                $"{name} differs in {unexplained.Count} fields that are not known gaps:\n  "
                + string.Join("\n  ", unexplained.Take(20)));
        }

        [Fact]
        public void TheRootKeepsItsKind()
        {
            // Half of BSXFlags is a question about the root, twice asking whether it is
            // exactly NiNode, so rebuilding one kind as another changes what the file
            // claims about itself.
            NifModel source = Load("nifly/TestNifFile_Static_SE.nif");

            Assert.Equal("NiNode", source.Blocks[0].Name);

            NifModel rebuilt = RoundTrip(source);
            NifItem root = rebuilt.GetBlock(rebuilt.FindItem(rebuilt.Footer, "Roots")!.Children[0])!;

            Assert.Equal("NiNode", root.Name);
        }

        [Theory]
        [InlineData("nifly/TestNifFile_OrderedNode_SE.nif", "BSOrderedNode")]
        [InlineData("nifly/TestNifFile_MultiBound_SE.nif", "BSMultiBoundNode")]
        public void ANodeKeepsItsKind(string name, string kind)
        {
            NifModel source = Load(name);

            Assert.Contains(source.Blocks, b => b.Name == kind);

            NifModel rebuilt = RoundTrip(source);

            Assert.Contains(rebuilt.Blocks, b => b.Name == kind);
        }

        [Fact]
        public void AnSeShapeKeepsItsVertexLayoutAndGainsTangents()
        {
            // SE packs its vertices inline, and the descriptor says which attributes
            // are in them and how wide one is. Getting it wrong does not fail loudly:
            // the reader walks the buffer at the wrong stride and produces geometry
            // that is merely wrong.
            NifModel source = Load("nifly/TestNifFile_Static_SE.nif");
            NifItem sourceShape = source.Blocks.First(b => source.BlockInherits(b, "BSTriShape"));

            ulong descriptor = source.FindItem(sourceShape, "Vertex Desc")!.Value.ToUInt64();

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltShape = rebuilt.Blocks.First(b => rebuilt.BlockInherits(b, "BSTriShape"));

            Assert.Equal(descriptor, rebuilt.FindItem(rebuiltShape, "Vertex Desc")!.Value.ToUInt64());

            NifItem sourceVertices = source.FindItem(sourceShape, "Vertex Data")!;
            NifItem rebuiltVertices = rebuilt.FindItem(rebuiltShape, "Vertex Data")!;

            Assert.Equal(sourceVertices.Children.Count, rebuiltVertices.Children.Count);

            // Tangents are regenerated rather than carried, so they agree to about the
            // precision the source stores them at rather than exactly.
            NifVector3 expected = source.FindItem(sourceVertices.Children[0], "Tangent")!.Value.Get<NifVector3>();
            NifVector3 actual = rebuilt.FindItem(rebuiltVertices.Children[0], "Tangent")!.Value.Get<NifVector3>();

            Assert.Equal(expected.X, actual.X, 2);
            Assert.Equal(expected.Y, actual.Y, 2);
            Assert.Equal(expected.Z, actual.Z, 2);
        }

        [Fact]
        public void TheCullingVolumeIsVisibleInTheScene()
        {
            // Six numbers on a node is a volume nobody will ever notice is wrong. So
            // it is drawn as a mesh as well, the way collision shapes are, and the
            // exact numbers stay on the properties.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifVector3 size = source.FindItem(
                source.Blocks.First(b => b.Name == "BSMultiBoundOBB"), "Size")!.Value.Get<NifVector3>();

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject volume = Assert.Single(
                scene.Objects, o => o.Class == "Model" && FbxMultiBound.IsVolumeMesh(o.Name));

            FbxObject geometry = Assert.Single(scene.ChildrenOf(volume.Id), o => o.Class == "Geometry");

            MeshGeometry mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options())!;

            // A box of the right size, centred on its node: the extents are half of
            // what the volume calls its size.
            Assert.Equal(size.X / 2f, mesh.Vertices.Max(v => v.X), 2);
            Assert.Equal(size.Y / 2f, mesh.Vertices.Max(v => v.Y), 2);
            Assert.Equal(size.Z / 2f, mesh.Vertices.Max(v => v.Z), 2);
        }

        [Fact]
        public void TheCullingVolumeDoesNotBecomeGeometry()
        {
            // It is a picture of the bound, not part of the model. Left unrecognised
            // it would come back as a box floating inside every multi-bound node.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            int shapes = source.Blocks.Count(b => source.BlockInherits(b, "BSTriShape"));

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(shapes, rebuilt.Blocks.Count(b => rebuilt.BlockInherits(b, "BSTriShape")));
        }

                [Fact]
        public void AMultiBoundNodeKeepsItsVolume()
        {
            // The volume is the whole point of the class: the engine culls against it
            // instead of working one out from the geometry, which is how a room's
            // walls are drawn only when the player can see in. Losing it leaves a
            // multi-bound node bounding nothing, and nothing looks wrong.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifItem before = Assert.Single(source.Blocks, b => b.Name == "BSMultiBoundOBB");

            NifModel rebuilt = RoundTrip(source);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSMultiBoundOBB");

            Assert.Equal(
                source.FindItem(before, "Center")!.Value.Get<NifVector3>(),
                rebuilt.FindItem(after, "Center")!.Value.Get<NifVector3>());

            Assert.Equal(
                source.FindItem(before, "Size")!.Value.Get<NifVector3>(),
                rebuilt.FindItem(after, "Size")!.Value.Get<NifVector3>());

            // And it is reachable from the node, not merely present in the file.
            NifItem node = Assert.Single(rebuilt.Blocks, b => b.Name == "BSMultiBoundNode");

            Assert.Equal(after, rebuilt.GetRef(rebuilt.GetRef(node, "Multi Bound")!, "Data"));
        }

                [Fact]
        public void ExtraDataSurvives()
        {
            // Almost every NIF has some, and none of it has an FBX equivalent: a
            // behaviour graph path, a furniture marker, a string nothing else reads.
            // Dropping it changes what the game does with the file and leaves nothing
            // to see.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            var expected = source.Blocks
                .Where(b => source.BlockInherits(b, "NiExtraData") && b.Name != "BSXFlags")
                .GroupBy(b => b.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.NotEmpty(expected);

            NifModel rebuilt = RoundTrip(source);

            foreach ((string type, int count) in expected)
            {
                Assert.Equal(count, rebuilt.Blocks.Count(b => b.Name == type));
            }
        }

        [Fact]
        public void TheCalculatedBsxFlagsIsNotCarriedAsWell()
        {
            // BSXFlags is extra data too, and is recalculated rather than carried --
            // so carrying it here as well would leave the file with two, and the
            // engine reads the first it finds.
            NifModel source = Load("generate_rb_box.nif");

            Assert.Single(source.Blocks, b => b.Name == "BSXFlags");

            NifModel rebuilt = RoundTrip(source);

            Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");
        }

                [Fact]
        public void SharedPropertyBlocksAreSharedAgain()
        {
            // Eight shapes pointing at two alpha properties came back with eight, and
            // two texture sets came back as twenty-seven. Sharing is data: it says the
            // shapes are the same material, not merely alike.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            int alphas = source.Blocks.Count(b => b.Name == "NiAlphaProperty");
            int sets = source.Blocks.Count(b => b.Name == "BSShaderTextureSet");

            Assert.NotEqual(0, alphas);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(alphas, rebuilt.Blocks.Count(b => b.Name == "NiAlphaProperty"));
            Assert.Equal(sets, rebuilt.Blocks.Count(b => b.Name == "BSShaderTextureSet"));
        }

        [Fact]
        public void IdenticalBlocksKeptApartStayApart()
        {
            // The other half, and the reason sharing cannot be decided by comparing
            // content: this file carries three texture sets that are identical and
            // separate. Merging equal blocks would be as wrong as never merging.
            NifModel source = Load("multi_material_cube.nif");

            var sets = source.Blocks.Where(b => b.Name == "BSShaderTextureSet").ToList();

            Assert.True(sets.Count > 1, "the fixture is supposed to have several");

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(sets.Count, rebuilt.Blocks.Count(b => b.Name == "BSShaderTextureSet"));
        }

                [Fact]
        public void AParticleSystemKeepsItsShader()
        {
            // A particle system is a shape: it has a shader and an alpha property like
            // any other, and they are what the effect actually looks like. It has no
            // geometry for them to hang off, which is why the geometry path never saw
            // them and they were dropped.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            NifItem system = Assert.Single(source.Blocks, b => b.Name == "NiParticleSystem");

            string texture = source.GetString(source.GetRef(system, "Shader Property")!, "Source Texture");

            Assert.NotEqual(string.Empty, texture);

            NifModel rebuilt = RoundTrip(source);
            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiParticleSystem");

            Assert.NotNull(rebuilt.GetRef(after, "Alpha Property"));
            Assert.Equal(texture, rebuilt.GetString(rebuilt.GetRef(after, "Shader Property")!, "Source Texture"));
        }

                [Fact]
        public void AParticleSystemKeepsTheControllerThatRunsIt()
        {
            // NiPSysUpdateCtlr holds no interpolator and no keys. It is not animation
            // -- it is the switch that makes the system run at all -- so the animation
            // layer cannot see it: that layer recognises a controller by what its
            // interpolator drives, and this one has none.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            Assert.Contains(source.Blocks, b => b.Name == "NiPSysUpdateCtlr");

            NifModel rebuilt = RoundTrip(source);

            NifItem update = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPSysUpdateCtlr");
            NifItem system = Assert.Single(rebuilt.Blocks, b => b.Name == "NiParticleSystem");

            // On the system's chain and pointing back at it, not merely present.
            Assert.Equal(system, rebuilt.GetRef(update, "Target"));

            var chain = new List<NifItem>();

            for (NifItem? c = rebuilt.GetRef(system, "Controller");
                 c is not null;
                 c = rebuilt.GetRef(c, "Next Controller"))
            {
                chain.Add(c);
            }

            Assert.Contains(update, chain);
        }

                [Fact]
        public void ASequencedControllerIsAttachedAndBlended()
        {
            // Attached controllers and sequences are two halves of one arrangement.
            // The controller hangs on what it drives and holds a blend interpolator --
            // the slot the manager mixes every playing sequence into -- while each
            // sequence holds its own interpolator with the keys and names that
            // controller. Rebuilding only the sequences leaves an animation with
            // nothing to apply it to.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            Assert.Contains(source.Blocks, b => b.Name == "NiControllerManager");

            NifModel rebuilt = RoundTrip(source);

            foreach (string type in new[]
                     {
                         "BSEffectShaderPropertyFloatController",
                         "NiPSysEmitterCtlr",
                         "NiBlendFloatInterpolator",
                         "NiBlendBoolInterpolator"
                     })
            {
                Assert.Equal(
                    source.Blocks.Count(b => b.Name == type),
                    rebuilt.Blocks.Count(b => b.Name == type));
            }

            // One controller serves all three sequences rather than one each.
            NifItem emitter = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPSysEmitterCtlr");

            // Its two tracks go in different slots: nif.xml names them BirthRate and
            // EmitterActive, the second on Visibility Interpolator.
            Assert.Equal("NiBlendFloatInterpolator", rebuilt.GetRef(emitter, "Interpolator")!.Name);
            Assert.Equal("NiBlendBoolInterpolator", rebuilt.GetRef(emitter, "Visibility Interpolator")!.Name);
        }

                [Fact]
        public void StandaloneControllersComeBackStandalone()
        {
            // A controller no sequence names is attached to what it controls and runs
            // on its own. FBX has no way to say that -- every animation there belongs
            // to a stack -- so the export invents a sequence and the import has to
            // undo the invention. Writing it back as a real sequence puts a controller
            // manager, an object palette and a text key block into a file that had
            // none, and leaves the controllers pointing at nothing.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            Assert.DoesNotContain(source.Blocks, b => b.Name == "NiControllerManager");

            int controllers = source.Blocks.Count(b => b.Name == "BSEffectShaderPropertyFloatController");

            Assert.NotEqual(0, controllers);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(
                controllers,
                rebuilt.Blocks.Count(b => b.Name == "BSEffectShaderPropertyFloatController"));

            // And nothing was invented to hold them.
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiControllerManager");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiControllerSequence");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiDefaultAVObjectPalette");

            // Each is on a shader property, with keys, rather than loose in the file.
            foreach (NifItem controller in rebuilt.Blocks.Where(
                         b => b.Name == "BSEffectShaderPropertyFloatController"))
            {
                Assert.NotNull(rebuilt.GetRef(controller, "Interpolator"));
            }
        }

                [Fact]
        public void AnEffectShaderLooksLikeItselfInTheScene()
        {
            // The es_ properties reimport perfectly on their own, which is what makes
            // a blank material easy to ship: nothing fails, and an artist opening the
            // file sees an untextured surface beside correctly textured ones.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject material = scene.Objects.First(
                o => o.Class == "Material" && FbxEffectShader.WasWritten(o));

            var connected = scene.PropertyConnectionsTo(material.Id).ToList();

            // Its own texture, on the channel a DCC tool renders from.
            (FbxObject texture, _) = Assert.Single(connected, c => c.Property == "DiffuseColor");

            Assert.Equal(
                source.GetString(
                    source.Blocks.First(b => b.Name == "BSEffectShaderProperty"), "Source Texture"),
                texture.Child("RelativeFilename")?.Properties.FirstOrDefault());
        }

                [Fact]
        public void EffectShadersSurviveWithTheirOwnFields()
        {
            // ck-cmd's FBX path drops these: its export casts the shader to
            // BSLightingShaderProperty and takes the null when that fails, and its
            // import only ever builds a lighting shader. Following it would lose every
            // glow, decal and magic effect in a file.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            int shaders = source.Blocks.Count(b => b.Name == "BSEffectShaderProperty");

            Assert.NotEqual(0, shaders);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(shaders, rebuilt.Blocks.Count(b => b.Name == "BSEffectShaderProperty"));

            // An effect shader shares almost no fields with a lighting one, so the
            // check is on its own: its texture, its flags and its colour.
            NifItem before = source.Blocks.First(b => b.Name == "BSEffectShaderProperty");
            NifItem after = rebuilt.Blocks.First(b => b.Name == "BSEffectShaderProperty");

            foreach (string field in new[] { "Shader Flags 1", "Shader Flags 2", "Falloff Start Angle" })
                Assert.Equal(source.FindItem(before, field)!.Value.ToString(), rebuilt.FindItem(after, field)!.Value.ToString());

            Assert.Equal(source.GetString(before, "Source Texture"), rebuilt.GetString(after, "Source Texture"));
        }

                [Fact]
        public void BonesNamedLikeSkyrimsResolve()
        {
            // FBX names cannot hold a space or a bracket, so "NPC R Thigh [RThg]" goes
            // out as NPC_s_R_s_Thigh_s__ob_RThg_cb_ and has to be decoded on the way
            // back. Left encoded it matches no node, and because a skin whose bones all
            // fail to resolve is dropped whole, every Skyrim body part loses its
            // skinning -- with the mesh, the shader and the bones themselves all intact.
            NifModel source = Load("nifly/TestNifFile_LooseBlocks_SE.nif");

            Assert.Contains(source.Blocks, b => source.GetName(b).Contains('['));

            int partitions = source.Blocks.Count(b => b.Name == "NiSkinPartition");

            Assert.NotEqual(0, partitions);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(partitions, rebuilt.Blocks.Count(b => b.Name == "NiSkinPartition"));
        }

                [Fact]
        public void ASkinnedSeShapeCarriesItsWeightsInTheVertex()
        {
            // SE reads a skinned mesh's weights from the vertex buffer, not from
            // NiSkinData. A shape with the skinning blocks but not these is fully
            // rigged in a NIF editor and rigid in game, which is as quiet as this
            // gets.
            NifModel source = Load("nifly/TestNifFile_Skinned_SE.nif");
            NifItem sourceShape = source.Blocks.First(b => source.BlockInherits(b, "BSTriShape"));

            ulong descriptor = source.FindItem(sourceShape, "Vertex Desc")!.Value.ToUInt64();

            NifModel rebuilt = RoundTrip(source);
            NifItem shape = rebuilt.Blocks.First(b => rebuilt.BlockInherits(b, "BSTriShape"));

            // Same layout, which for a skinned shape means the wider vertex: the
            // twelve bytes of weights and indices, and the bit announcing them.
            Assert.Equal(descriptor, rebuilt.FindItem(shape, "Vertex Desc")!.Value.ToUInt64());

            NifItem vertices = rebuilt.FindItem(shape, "Vertex Data")!;

            Assert.NotEmpty(vertices.Children);

            foreach (NifItem vertex in vertices.Children)
            {
                float total = rebuilt.FindItem(vertex, "Bone Weights")!
                    .Children.Sum(c => c.Value.ToFloat());

                // Every vertex is fully weighted. A vertex summing to less is one the
                // engine drags towards the origin.
                Assert.Equal(1f, total, 3);
            }
        }

                [Fact]
        public void TheCollisionObjectKeepsItsFlags()
        {
            // bhkCOFlags says how the body and its node keep in step: SET_LOCAL reads
            // the body transform as local, SYNC_ON_UPDATE follows the node when it is
            // animated. Rebuilding it as a bare ACTIVE leaves the collision the right
            // size and in roughly the right place, no longer tracking what it belongs
            // to.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceCollision = source.Blocks.First(b => b.Name == "bhkCollisionObject");

            uint expected = source.GetUInt(sourceCollision, "Flags");

            Assert.Equal(9u, expected);

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltCollision = rebuilt.Blocks.First(b => b.Name == "bhkCollisionObject");

            Assert.Equal(expected, rebuilt.GetUInt(rebuiltCollision, "Flags"));
        }

        [Fact]
        public void TheCollisionMaterialSurvives()
        {
            // Nothing in the tessellated triangles says wood rather than stone, and the
            // engine reads it for footstep sound and impact response.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceShape = source.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(source, sourceShape));

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltShape = rebuilt.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(rebuilt, rebuiltShape));
        }

        [Fact]
        public void TheShaderKeepsAnIdentityUvTransform()
        {
            // A zero UV scale does not fail loudly: it multiplies every texture
            // coordinate in the mesh to nothing.
            NifModel rebuilt = RoundTrip(Load("multi_material_cube.nif"));

            foreach (NifItem shader in rebuilt.Blocks.Where(b => b.Name == "BSLightingShaderProperty"))
                Assert.Equal(new NifVector2(1f, 1f), rebuilt.FindItem(shader, "UV Scale")!.Value.Get<NifVector2>());
        }

        [Fact]
        public void TheImporterCalculatesBsxFlags()
        {
            NifModel rebuilt = RoundTrip(Load("generate_rb_box.nif"));

            NifItem bsx = Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");

            Assert.Equal(rebuilt.Calculate(), rebuilt.GetUInt(bsx, "Integer Data"));
        }
    }
}
