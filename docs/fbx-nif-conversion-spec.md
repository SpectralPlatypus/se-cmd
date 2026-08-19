# NIF ↔ FBX conversion specification

Extracted from **FBXWrangler** in [ck-cmd](https://github.com/aerisarn/ck-cmd)
(`src/core/FBXWrangler.cpp`, 6119 lines, plus `HKXWrangler`, `MathHelper` and
`EulerAngles`). This is the reference behaviour that `se-cmd` reimplements in C#.

Line references are to ck-cmd at the checkout used for extraction. Where this port
must deviate, that is stated explicitly in [§10](#10-deviations-in-this-port) rather
than silently.

---

## 1. Scope

FBXWrangler converts in both directions and covers:

| Area | NIF → FBX | FBX → NIF |
| --- | --- | --- |
| Node hierarchy and transforms | yes | yes |
| Trishape / tristrips geometry | yes | yes (NiTriShape only) |
| Materials, textures, alpha | yes | yes |
| Extra data (int/bool/string/float) | yes | yes |
| Skinning | yes | yes |
| Transform animation (KF sequences) | yes | yes |
| Property/float-track animation | yes | yes |
| Visibility animation | yes | yes |
| Havok collision shapes | yes | needs Havok SDK |
| Havok constraints | yes | needs Havok SDK |
| Bone LOD | yes | yes |
| Bounding box (`BSBound`) | yes | yes |

---

## 2. Scene conventions

Set once when the FBX scene is created (`NewScene`, L224–234):

| Setting | Value | Note |
| --- | --- | --- |
| Axis system | `FbxAxisSystem::Max` | Z-up, right-handed, front `-ParityOdd` |
| System unit | `FbxSystemUnit::cm` | |
| Root node scaling | `(1, 1, 1)` | |
| Export file version | `FBX_2013_00_COMPATIBLE` | FBX 7.4 binary |

Because the FBX declares Max axes, **no axis swizzle is applied to coordinates**.
`toNIF` (L3172–3182) is a plain component copy. This is the single most important
convention to preserve: introducing a conversion would double-transform every file.

`ConvertScene` (L237) exists to re-express a scene as Maya Y-up, and is *not* part of
the normal path.

Before export, `CreateMissingBindPoses` is called (L2528).

### 2.1 Havok scale factor

Havok data is in metres, NIF in Skyrim units.

- NIF → FBX: multiply by `bhkScaleFactor` (from the file, nominally `69.99125`).
- FBX → NIF: multiply by `bhkScaleFactorInverse = 0.01428f` (L4894).

These are not exact reciprocals in the original; the constants are reproduced as
written.

---

## 3. Name encoding

FBX node names cannot carry arbitrary characters, so names are escaped
(`MathHelper.cpp` L21–36):

| Character | Encoded as |
| --- | --- |
| `` (space) | `_s_` |
| `[` | `_ob_` |
| `]` | `_cb_` |
| `:` | `_dd_` |

`sanitizeString` applies these on the way to FBX, `unsanitizeString` reverses them on
the way back. Replacement is naive and unanchored, so a literal `_s_` in a NIF name
does not survive a round trip. Reproduce as-is.

Node lookup by name (`getBuiltNode`, L1076–1105) tries, in order: the raw name, the
sanitized name, a camel-cased variant (renaming the node if it hits), and finally
`<name>_support`.

### 3.1 Reserved name suffixes

The FBX → NIF direction keys entirely off node names:

| Suffix / pattern | Meaning |
| --- | --- |
| `_rb` | Rigid body (`bhkCollisionObject` + `bhkRigidBody`) |
| `_sp` | Simple shape phantom (`bhkSPCollisionObject`) |
| `_con_` | Constraint attach point; excluded from body detection |
| `_attach_point` | Suffix of a constraint node |
| `_support` | Interposed node holding a mesh attribute |
| `_transform`, `_list`, `_convex_list`, `_mopp`, `_sphere`, `_box`, `_capsule`, `_convex`, `_mesh` | Collision shape nodes, appended by `recursive_convert` |
| `_geometry` | The mesh attribute of a collision shape node |
| `BoundingBox` | Becomes a `BSBound` extra datum |
| `x_…` | Added to the Havok skeleton, not the NIF |
| `_attach_…` | Node attributes are not imported |

---

## 4. NIF → FBX

Driven by `FBXBuilderVisitor`, a recursive field visitor over the NIF block graph
(L550–2464). It keeps a `build_stack` of FBX nodes; each visited NIF object either
creates a node (pushed) or reuses the parent.

Order of operations (L1683–1691): visit the root node graph, then `processSkins`,
then `buildManagers` (animation), then `processBoneLodInfo`.

The FBX scene root is renamed to the NIF root block's name (L1687).

### 4.1 Node hierarchy and transforms

Any `NiAVObject` becomes an `FbxNode` (`build`, L2440–2452) with:

```
LclTranslation = translation
LclRotation    = Euler XYZ of rotation matrix, in degrees   (EulOrdXYZs)
LclScaling     = (scale, scale, scale)                      -- NIF scale is uniform
```

Non-`NiAVObject` blocks become a node named after the block type with an identity
transform (`setNullTransform`).

Rotation goes matrix → quaternion → Euler XYZ. Use the same order; a different Euler
order silently produces wrong rotations for non-trivial cases.

### 4.2 Geometry

`AddGeometry` (L741–931). Handles `NiTriShape`, `BSLODTriShape` (treated as
`NiTriShape`), and `NiTriStrips` (whose points are triangulated first).

Mesh construction:

- Control points: `verts[i]` transformed by the shape's **own** TRS
  (`getTransform(&node)`). The shape transform is therefore **baked into the
  vertices**, not left on the node.
- Normals: `eByControlPoint` / `eDirect`.
- UVs: element named exactly **`"UV Map"`** — a constant name is required or Blender
  will not merge UV maps across meshes (L855–857). `eByControlPoint` / `eDirect`.
  **V is flipped**: `(u, 1 - v)`.
- Vertex colours: `eByControlPoint` / `eDirect`, RGBA.
- Only UV set 0 is exported.
- Polygons: one triangle each, `BeginPolygon(-1)` / three `AddPolygon` / `EndPolygon`.

Parenting rules (L884–905), both necessary because FBX allows one mesh attribute per
node:

1. If the parent is the scene root, interpose a node named `<shapeName>_support`.
2. Else if the parent already has a mesh attribute, create a child named
   `<parentName>_<n>` with `n` the lowest free index from 1.
3. Otherwise attach to the parent directly.

Material, if any, is added to the **node** (not the mesh), then
`InitMaterialIndices(eAllSame)` with index 0 pointing at it.

An empty vertex list yields a bare node and no mesh.

### 4.3 Materials and textures

`create_material` (L584–…) builds an `FbxSurfacePhong` named `<name>_material` from a
`BSLightingShaderProperty`:

| FBX | NIF source |
| --- | --- |
| `Emissive` | `EmissiveColor` |
| `EmissiveFactor` | `EmissiveMultiple` |
| `Specular` | `SpecularColor` |
| `SpecularFactor` | `SpecularStrength / 999` (NIF stores 0–999) |
| `Shininess` | `Glossiness` |
| `Diffuse`, `Ambient` | white; `AmbientFactor` 1 |
| `ReflectionFactor` | 0 |
| `ShadingModel` | `"Phong"` |

Plus two user-defined properties: `shader_type` (string) and `environment_map_scale`
(double).

Textures come from the `BSShaderTextureSet` slot list:

| Slot | Bound to |
| --- | --- |
| 0 | `Diffuse` (and `TransparentColor` when an alpha property exists) |
| 1 | `NormalMap` |
| 2–8 | User-defined property `slot<N+1>` |

The diffuse texture also carries the shader's UV offset/scale and clamp mode mapped to
FBX wrap modes, and `Alpha` from the shader.

On import, texture paths are rewritten by `format_texture` (L3123): truncate to start
at `textures` (or `cube`), convert `/` to `\`, force a `.dds` extension.

#### 4.3.1 Effect shaders are not handled by the reference

`FBXWrangler.cpp` contains no occurrence of `EffectShader` in any casing. Both
directions assume a lighting shader:

- **Export** (L732, L738): `create_material(..., DynamicCast<BSLightingShaderProperty>(shape.GetShaderProperty()), ...)`.
  A `BSEffectShaderProperty` fails that cast and yields NULL, so the shape leaves with
  no material at all.
- **Import** (L3442): `BSLightingShaderProperty* shader = new BSLightingShaderProperty();`,
  unconditionally.

ck-cmd does handle effect shaders elsewhere — `ConvertNif.cpp` builds them when
converting Oblivion and Fallout 3 material properties to Skyrim, and `geometry.cpp`
reads their external emittance for `BSXFlags` bit 9 — so this is a gap in the FBX path
rather than in the tool. It is listed in §9.

This port departs here, because following it would silently drop every glow, decal,
blood splatter and magic effect in a file. See §5.3.2.

### 4.4 Alpha properties

`AlphaFlagsHandler` (L432–546) round-trips `NiAlphaProperty` through user-defined
properties on the material. The 16-bit flags word decomposes as:

| Bits | Field |
| --- | --- |
| 0 | `color_blending_enable` |
| 1–4 | `source_blend_mode` |
| 5–8 | `destination_blend_mode` |
| 9 | `alpha_test_enable` |
| 10–12 | `alpha_test_mode` |
| 13 | `no_sorter_flag` |

Blend modes are written as GL names (`ONE`, `ZERO`, `SRC_COLOR`,
`ONE_MINUS_SRC_COLOR`, `DST_COLOR`, `ONE_MINUS_DST_COLOR`, `SRC_ALPHA`,
`ONE_MINUS_SRC_ALPHA`, `DST_ALPHA`, `ONE_MINUS_DST_ALPHA`, `SRC_ALPHA_SATURATE`), test
modes as `ALWAYS`, `LESS`, `EQUAL`, `LEQUAL`, `GREATER`, `NOTEQUAL`, `GEQUAL`,
`NEVER`. The threshold is a separate `alpha_test_threshold` short (named for Blender).

Note the asymmetry in the original: `gl_blend_modes_to_value` compares against
`"GL_ONE"` while the writer emits `"ONE"`, so `ONE` falls through to the default. The
default is also `GL_ONE`, so behaviour is accidentally correct.

On import a property is only produced when the flags word is non-zero.

### 4.5 Extra data

| NIF block | FBX representation |
| --- | --- |
| `NiIntegerExtraData` | node property `ed_<name>` (int) |
| `NiBooleanExtraData` | node property `ed_<name>` (bool) |
| `NiStringExtraData` | node property `ed_<name>` (string) |
| `NiFloatExtraData` | node property `ed__f_<name>` (string) |
| `BSBound` | child node `BoundingBox` holding a box shape |
| `BSXFlags` | dropped; recalculated on export |
| `BSBoneLODExtraData` | node property `lod_distance` (int) per bone |

`NiFloatExtraData` whose name contains `:` and not `Phoneme` is a **float track**: the
name splits as `<track>:<node>`, and the value becomes a property named `<track>` on
the node named `<node>`. `Shield` and `Weapon` node names are upper-cased.

### 4.6 Skinning

`processSkins` (L1020–1145) runs after the graph walk, because bones must exist first.

Per `NiSkinInstance`, **per skin partition**, an `FbxSkin` named `<shape>_skin` is
created, and per bone in the partition an `FbxCluster` named `<bone>_cluster`:

- `SetLink(boneNode)`, `SetLinkMode(eNormalize)`.
- `SetTransformLinkMatrix(getTransform(bone))` — the bone's own TRS.
- Control point indices come from `vertexMap` / `boneIndices` / `vertexWeights`;
  weights of 0 are skipped.

The skin is attached to the mesh attribute whose name matches the shape, looking
through the `<shape>_support` child first.

`NiSkinInstance`, its data and its partition are marked visited so the generic walk
does not also emit them.

### 4.7 Animation

#### 4.7.1 KF sequences

`buildManagers` → `exportKFSequence` (L1414–1446). Each `NiControllerSequence`
becomes an `FbxAnimStack` named after the sequence, holding one `FbxAnimLayer` named
`"Default"`.

Each `ControlledBlock` resolves its target node name from the sequence's
`NiStringPalette` when present (offset into the palette, NUL-terminated), otherwise
from `nodeName`.

`NiTransformInterpolator` → `addTrack` (L1385–1412) writes into the node's
`LclTranslation`, `LclRotation` and `LclScaling` curves.

#### 4.7.2 Key conversion

| NIF `KeyType` | FBX interpolation |
| --- | --- |
| `CONST_KEY` (5) | `eInterpolationConstant` |
| `LINEAR_KEY` (1) | `eInterpolationLinear` |
| `QUADRATIC_KEY` (2) | `eInterpolationCubic` |

Times are seconds. Details:

- **Translation** (L1178): per-component X/Y/Z curves, interpolation from the key
  group.
- **Rotation, XYZ type** (L1232): three float groups, values converted **radians →
  degrees**, always written as `eInterpolationCubic`.
- **Rotation, quaternion type** (L1260): each key's quaternion is decomposed with
  `DecomposeSphericalXYZ` into Euler XYZ, written cubic.
- **Scale** (L1319): NIF scale is a single float, replicated to all three FBX
  components. Cubic keys get `eTangentBreak`.
- **Float properties** (L1345): single curve on the property, cubic keys get
  `eTangentBreak`.

#### 4.7.3 Property animation

`NiFloatExtraDataController` (L1727) animates the node property named by
`ExtraDataName` up to the first `:`. `NiVisController` (L1744) animates the node's
`Visibility`. Both go onto the current animation stack, creating `"Take 001"` +
`"Default"` layer if none exists, and widen the stack's local time span to cover the
controller's start/stop.

#### 4.7.4 How a property track is named

> This is one piece of a larger picture; §5A covers the animation layer in both
> directions, including where tracks are found, how keys convert, and what cannot
> travel.

FBX animates a *named property on a node*. A NIF names what it animates with four
strings in the sequence's controlled block — controller class, controller id,
interpolator id, property type — and all four are needed to say what a track drives. So
the FBX property name carries them, joined by `|`, with trailing empties dropped:

```
ControllerType|ControllerId|InterpolatorId|PropertyType
```

The node the track binds to is the `NiAVObject`, even when the controller hangs off a
property of it: a shader's fade is controlled from the shader property, but it is the
node an FBX curve can attach to.

`NiVisController` with no ids is the one exception, and is written as plain
`Visibility` — a standard FBX property, so a DCC tool given it actually hides the
object, where an encoded name would be a number nobody reads.

##### Worked example: `NiPSysEmitterCtlr`

The emitter controller drives two things, and the interpolator id is what separates
them. On `TestNifFile_Animated_LE.nif`'s `PCloud06` node:

| FBX property | Keys | Drives |
| --- | --- | --- |
| `NiPSysEmitterCtlr\|NiPSysCylinderEmitter:0\|BirthRate` | 5 | how fast particles are emitted |
| `NiPSysEmitterCtlr\|NiPSysCylinderEmitter:0\|EmitterActive` | 4, boolean | whether emission is on |

The controller id is the modifier the controller drives (`NiPSysCylinderEmitter:0`), and
the interpolator id names which of its two slots this is. That is what §5.6.0 reads back
to decide between `Interpolator` and `Visibility Interpolator` — the same pairing nif.xml
documents as `['BirthRate', 'EmitterActive']`.

A shader controller has no interpolator id, so its name has an empty third part:
`BSEffectShaderPropertyFloatController|5||BSEffectShaderProperty`.

##### Constant tracks

A NIF interpolator can hold a value and **no data block at all**, and that is a real
animation: it says "this value, for this whole sequence". The absence of the block is
the representation, not a missing piece of one.

It cannot be a curve, and there are three ways to get this wrong:

- **An empty curve** is not a curve, and most importers drop it.
- **A curve with one invented key** is a different animation that happens to look the
  same, and it comes back as a data block with one key rather than as a constant.
- **The model's resting value** is one value per *model*, where this is one per *take*.
  `TestNifFile_Animated_LE.nif` holds different constants for `EmitterActive` across its
  three sequences, so a per-model value cannot express it.

The `AnimationStack` is the only per-take place in FBX, so a constant goes there, named
`const_<node>|<property>`. It is written **typed** — `bool`, `Number` or `ColorRGB` —
because a boolean constant and a float one are the same number and different animations,
and nothing else on the stack says which this is.

### 4.8 Collision shapes

FBX has no shape primitives, so every Havok shape is **tessellated into a mesh**
(`recursive_convert`, L1802–2048). Container shapes create an intermediate node and
recurse; leaf shapes append geometry.

| Shape | Node suffix | Treatment |
| --- | --- | --- |
| `bhkTransformShape`, `bhkConvexTransformShape` | `_transform` | Node, recurse (transform commented out in the original) |
| `bhkListShape` | `_list` | Node, recurse into each sub-shape |
| `bhkConvexListShape` | `_convex_list` | Node, recurse |
| `bhkMoppBvTreeShape` | `_mopp` | Node, recurse into wrapped shape; MOPP data discarded |
| `bhkSphereShape` | `_sphere` | Tessellated sphere of `radius` |
| `bhkBoxShape` | `_box` | Tessellated box of `dimensions`, `radius` |
| `bhkCapsuleShape` | `_capsule` | Tessellated capsule between the two points |
| `bhkConvexVerticesShape` | `_convex` | Convex hull of the vertices |
| `bhkCompressedMeshShape` | `_mesh` | Decoded chunks, see below |

Vertices are scaled by `bhkScaleFactor` on emission (L2027).

Each shape contributes a `bhkCMSDMaterial` (Havok material + collision filter). A
`FbxSurfacePhong` is created per distinct (material, layer) pair, named after the
material, carrying a user-defined `CollisionLayer` string and coloured by the material.
The mesh gets `eByPolygon` / `eIndexToDirect` material mapping so each triangle
references its material.

#### 4.8.1 Compressed mesh decoding

`Accessor<bhkCompressedMeshShapeData>` (L278–381):

- "Big" verts/tris are emitted directly, each big triangle carrying its own material.
- Each chunk: vertices are `chunkOrigin + offset / 1000`, then transformed by the
  chunk's `transformIndex` entry (translation + rotation).
- Strip indices are unrolled to triangles with **winding alternating on odd `f`**.
- Remaining indices after the strips are plain triangles.
- All triangles of a chunk take `chunk.materialIndex`.

### 4.9 Rigid bodies

`visit_rigid_body` (L2318–2400). Creates a node named `<targetName>_rb`.

The rigid body's transform is a **world** matrix even when parented under a `NiNode`.
FBXWrangler therefore parents the node properly and, for `bhkBlendCollisionObject`
(`absolute = true`), stores the transform **relative** to the parent's global
transform:

```
rel = parent.EvaluateGlobalTransform().Inverse() * rb.EvaluateGlobalTransform()
```

Translation is scaled by `bhkScaleFactor`; rotation is quaternion → Euler XYZ degrees.
When exporting a rig, `body_part` (from the Havok filter) is stored as a property.

`bhkSPCollisionObject` produces `<name>_sp` with wireframe shading and no transform.

### 4.9A Structural controllers on a particle system

ck-cmd carries none of this: `FBXWrangler.cpp` has no occurrence of `NiParticleSystem`,
`NiPSysModifier`, or any particle controller, in either direction. See
`nif-particle-spec.md` for the whole picture; this is the part that touches animation.

A particle system is also a *shape*: it carries a shader property and an alpha property
like any other, and they are what the effect looks like. It has no geometry for them to
hang off — its vertices are a runtime buffer the file only sizes — so the geometry path
never sees it, and the material attaches to the node instead.

A `NiPSysUpdateCtlr` holds no interpolator and no keys. It is not animation — it is the
switch that makes the system run at all — and the animation layer cannot represent it,
because that layer recognises a controller by what its interpolator drives (§5A.4).

So it travels with the particle system's structure, as `particle_controllers` (a count)
and one `npc_<i>_` group per controller, which is also where it belongs: it says
something about the system, not about a timeline.

The split is on the interpolator. A controller that holds one is animation and goes the
other way; carrying it here as well would rebuild it twice. `Target` and
`Next Controller` are not carried, since both are rebuilt from the chain.

### 4.10 Constraints

`FbxConstraintBuilder` (L2050–2310). For each constraint entity pair, a node named
`<parent>_con_<child>_attach_point` is created under the **other** body's node, placed
at the constraint's B frame (`matB`), and tagged with a `constraint_type` property.

Frames are built from the descriptor axes as matrix columns, with the pivot scaled by
`bhkScaleFactor`:

| Constraint | Columns (A frame) | Extra properties |
| --- | --- | --- |
| Ragdoll | `twistA`, `planeA`, `motorA`, `pivotA` | `coneMaxAngle`, `planeMinAngle`, `planeMaxAngle`, `twistMinAngle`, `twistMaxAngle`, `maxFriction` |
| Hinge | `axleA`, `perp2AxleInA1`, `perp2AxleInA2`, `pivotA` | — |
| LimitedHinge | as Hinge | `maxAngle`, `minAngle`, `maxFriction` |
| Malleable | delegates to its wrapped type | — |
| Prismatic, BallAndSocket, StiffSpring | not implemented | — |

All numeric properties are written as **strings**.

### 4.11 Bone LOD

`BSBoneLODExtraData` is collected during the walk and applied afterwards
(`processBoneLodInfo`, L1458) as an `lod_distance` int property on each named bone.

---

## 5. FBX → NIF

`ImportScene` → `LoadMeshes` (L5302–5780) → `SaveNif` (L5793).

### 5.1 Preprocessing

Before anything else (L5307–5310):

1. `SplitMeshesPerMaterial(scene, true)` — NIF has one material per shape.
2. `Triangulate(scene, true)`.

### 5.2 Root and hierarchy

The first visited node becomes the conversion root: a `BSFadeNode`, or a plain
`NiNode` when exporting a skin. It is **named after the FBX file stem**, not the node.

If the FBX root carries a non-identity transform, a child `NiNode` named
`rootTransformProxy` is inserted to hold it, and the transform goes on the root.

Children become `NiNode`s named by `unsanitizeString`, with transforms from the FBX
local transform. Nodes named `_rb`/`_sp` are deferred into `physic_entities` and not
turned into nodes; `BoundingBox` becomes a `BSBound`; `x_…` goes to the Havok
skeleton.

`FbxSkeleton` attributes drive the Havok skeleton: `eRoot` creates it, anything else
adds a bone (skipped for nodes containing `_attach_`).

### 5.3 Mesh import

`importShape` (L3186–…). Per mesh attribute of a node, a `NiTriShape` +
`NiTriShapeData` named after the **node** (unsanitized).

- UVs: `InvertV` defaults **true**, `InvertU` false, applied to the whole direct array
  up front.
- `GenerateTangentsDataForAllUVSets()` is called, then tangents/binormals are read.
- Per polygon (skipping any with size ≠ 3) and per corner, attributes are fetched with
  `get_vertex_element`, which respects `eByControlPoint` / `eByPolygon` /
  `eByPolygonVertex` mapping and `eDirect` / index reference modes.
- **Vertices are de-duplicated** on the exact 18-tuple
  `(pos.xyz, normal.xyz, tangent.xyz, bitangent.xyz, uv.xy, colour.rgba)`. This splits
  vertices across UV/normal seams, which is what NIF requires.
- Bounding sphere via Miniball over the final vertices; centre and radius are stored.
- If no normals were present they are recalculated from the triangles.

Two exporter workarounds:

- **Blender**: if a *second* vertex-colour layer exists, alpha is taken as
  `max(r, g, b)` of that layer.
- **3ds Max 2017/2018**: vertex colours are read directly by control point index
  rather than through the mapping mode.

Alpha presence is detected from any colour with alpha < 1.

#### 5.2.1 Shared property blocks

Bethesda's files point several shapes at one `BSShaderTextureSet` or one
`NiAlphaProperty`, and *also* carry identical blocks side by side where the exporter
happened to make two. Both matter, and they rule out the two obvious approaches:

- Rebuilding one block per shape splits blocks that were one. Eight shapes sharing two
  alpha properties came back with eight, and two texture sets came back as twenty-seven.
- Merging blocks by content joins blocks that were separate. `multi_material_cube.nif`
  holds three texture sets that are identical and deliberately distinct.

Sharing is data, so it is carried like any other: the export records which source block
each part came from, as `nif_texture_set` and `nif_alpha_property` on the FBX material,
and the import shares by that. Same index, same block; different index, different block,
however alike they look.

The indices mean nothing outside the file they came from, which is the only place they
are read.

#### 5.2.3 Which skin instance class

`BSDismemberSkinInstance` carries body-part slots on top of a plain `NiSkinInstance`,
and the two are not interchangeable: the slots are what let a cuirass hide the body
under it and a limb come away. Rebuilding every skin as the dismember form was the
single largest difference across the game's meshes.

**Nothing about the mesh decides it.** Across the 26,940 skinned shapes Skyrim ships:

| | Count |
| --- | --- |
| `BSDismemberSkinInstance` | 15,728 |
| `NiSkinInstance` | 11,212 |

- The Bethesda version does not separate them — every one of these is bsver 100.
- The presence of dismember partitions correlates perfectly and says nothing: the field
  only exists on that class.
- The folder separates them in 214 of 237 directories, and fails on the one that
  matters. `meshes/actors/character` holds 11,433 of the first and 9,772 of the second.

So what is carried is not the class but **the body slots themselves** — one per skin
partition, saying which part of a body that partition is. The class then follows: a shape
with slots is a `BSDismemberSkinInstance`, one without is a plain `NiSkinInstance`, and
the two can never disagree because there is only one fact.

Slots travel on the FBX skin deformer as `body_slots` (a count) and one
`body_slot_<i>` / `body_slot_<i>_flags` pair each. They are written **by name**, since
the numbers differ between creature skeletons and a name is something a reader can
check; a name the schema does not know is parsed as a number, so a slot from a skeleton
this build has never seen still survives.

The array is sized to the *partition* count rather than the carried count, because it
describes those partitions and they are rebuilt rather than carried. A partition past
the end of the list takes the last slot.

A scene that never was a NIF has no slots to carry, and
`FbxToNifOptions.SkinInstanceType` decides — the dismember form by default, since new
Skyrim content is mostly armour and body parts.

ck-cmd carries none of this. Its export never mentions body parts at all, and its import
sets every partition to `SBP_32_BODY` with `PF_EDITOR_VISIBLE | PF_START_NET_BONESET`
(L3100) in the branch that cannot run. This port wrote every slot as zero — the torso —
until the slots were carried.

#### 5.3.0 Skinned SE vertex data

`BSTriShape` packs everything about a vertex inline, and for a skinned shape that
includes four bone weights and four bone indices — twelve bytes — announced by the
`Skinned` attribute (`0x40`) and located by `Skinning Data Offset`.

This matters more than the other vertex attributes because of where SE reads it from.
The skinning blocks — `NiSkinInstance`, `NiSkinData`, `NiSkinPartition` — can all be
present and correct, with every bone named, and the mesh will still render **rigid**,
because SE takes its weights from the vertex buffer rather than from `NiSkinData`. It
looks fully rigged in a NIF editor. LE is unaffected: `NiTriShapeData` keeps no
per-vertex skinning, and `NiSkinData` is where the engine reads it.

Two ordering constraints fall out of this:

- The skin has to be **read before the shape is built**, not after, because the vertex
  descriptor decides the width of a vertex and has to know whether the shape is skinned
  before a single one is sized.
- The bone indices are into the shape's own bone list, which is only settled once the
  skin has been written — a bone whose node is missing is dropped there, and every index
  after it moves. So the list is read back and matched by name rather than assumed to be
  the order the skin arrived in.

Weights are renormalised over the four that are kept, since a vertex may arrive with
more influences than the format holds and one summing to less than 1 is dragged towards
the origin.

#### 5.3.2 Effect shaders

The two shader classes share almost no fields: an effect shader has its own source and
greyscale textures rather than a `BSShaderTextureSet`, and a base colour rather than a
specular model. Rather than forcing them through the common material form, the block's
own fields ride across flat on the FBX material, as constraints and particle systems do
— `NifFieldCodec` with an `es_` prefix, alongside a `shader_block` property naming the
class.

Only an effect shader records `shader_block`; a lighting shader is what everything else
rebuilds as, which keeps a scene authored in a DCC tool working unchanged.

The controller chain and extra data are not carried. An animated shader is animated
through the sequences, which travel by their own route, and a carried link would point
into a block list that no longer has that block.

##### The two halves

The material is written twice over, and the halves answer different questions.

The **exact half** is the `es_` properties: one per field, as text, authoritative on
reimport. Nothing else is read back — the visible half below is derived from these and
is never the source of truth.

The **visible half** is the same shader expressed in FBX's own vocabulary, so the
surface looks like itself in a DCC tool:

| NIF | FBX |
| --- | --- |
| `Source Texture` | a `FileTexture` connected to `DiffuseColor` (and to `TransparentColor` when the shape has an alpha property) |
| `Greyscale Texture` | a texture on the `slot3` user property, following the convention the texture set uses for its later slots |
| `Base Color` (rgb) | `DiffuseColor`, and `EmissiveColor` |
| `Base Color` (alpha) | `TransparencyFactor`, as `1 - a` |
| `Base Color Scale` | `EmissiveFactor` |
| `UV Offset`, `UV Scale` | `ModelUVTranslation`, `ModelUVScaling` on the texture |

Without the second half the material is a white Phong with nothing connected. That is
the failure worth guarding against precisely because it is not a failure: the properties
still reimport perfectly, the tests still pass, and the only symptom is an artist
opening the file and seeing a blank surface next to correctly textured lighting-shader
ones.

This mirrors the collision material (§4.8), which is likewise both a name a DCC tool can
edit and an exact value on reimport.

#### 5.3.3 Dynamic shapes

`BSDynamicTriShape` keeps a second array of four-float vertices that the engine rewrites
as the mesh moves — a cloak, a hanging chain. In the files seen it is **not** a copy of
the static positions: those are zero, and the dynamic buffer is where the shape actually
is. A skinned dynamic shape has no `Vertex Data` array at all, since `Data Size` is zero
and the field is conditional on it.

That made the export wrong before it made the import wrong. Reading the static entries
gave 136 vertices all at the origin — the whole mesh collapsed onto a point, with every
count in the file correct, which is why nothing caught it. The positions are read from
the dynamic buffer when there is one and it lines up with the vertex count.

Coming back, three of the four floats are the position and need no carrying: they are
the mesh, and they travel as geometry. The fourth is carried as `dynamic_vertex_w`, one
number per vertex.

It is carried rather than derived on purpose. Its values sit in [-1, 1] and differ
between vertices that *share* a position, which is what a tangent-frame component does
at a seam — but that is an inference, and writing a guess into a buffer the engine reads
every frame is worse than moving the number across without examining it.

#### 5.3.1 Tangent space

ck-cmd does not compute tangents. It calls the FBX SDK's
`GenerateTangentsDataForAllUVSets()` (L3235), reads `GetElementTangent(0)` and
`GetElementBinormal(0)` per vertex — deduplicated alongside position, normal, UV and
colour in the same `uniques` map, so they split where everything else splits — and then
**swaps them** on the way in (L3437–3439):

```cpp
data->SetTangents(bitangents);
data->SetBitangents(tangents);
data->SetBsVectorFlags(... | BSVF_HAS_TANGENTS);
```

The comment reads `//switched to uniform with nifskope`. FBX's binormal becomes the
NIF's tangent and vice versa.

This port has no FBX SDK, and generates the frame directly from NifSkope's
`spTangentSpace` (`src/spells/tangentspace.cpp`) instead. That is the better source:
NifSkope both writes these and renders from them, so its pairing of the two vectors is
self-consistent, and generating them its way makes ck-cmd's swap unnecessary rather than
something to reproduce.

Two departures from the textbook algorithm are deliberate in the original and are kept:

- **The UV determinant is used for its sign only.** The usual method divides by it,
  weighting each triangle by UV area. NifSkope replaces the division with `±1`, and the
  original carries the commented-out reciprocal with the note that this *"seems to
  produce better results"*. A degenerate UV triangle therefore cannot blow up the sum.
- **Each triangle is normalised before accumulating**, so a large one counts for no more
  than a small one.

Per vertex the contributions are summed and then orthogonalised against the normal:
`t -= n(n·t)`, normalise; `b -= n(n·b)`, `b -= t(t·b)`, normalise. The bitangent is *not*
`n × t` — that line exists in the original and is commented out — so its handedness comes
from the UV layout rather than being imposed. A vertex no triangle contributed to gets
`t = (n.y, n.z, n.x)`, `b = n × t`: arbitrary, but a stable frame rather than a zero
vector for a shader to divide by.

NifSkope reads triangles from strips, from `Triangles`, or **from every partition** when
`bsver >= 100`, since that is where SE geometry keeps them.

`BSGeometryDataFlags` bit 12 (`0x1000`, `Has Tangents`) announces the arrays and is OR'd
into the existing flags, not assigned — the low six bits hold the UV set count. Writing
the arrays without the bit leaves them in the file for nothing to read.

The generated vectors agree with those in ck-cmd's own example files to four decimal
places, which is what establishes that this is the same algorithm.

### 5.4 Extra data

Node properties map back (L5380–5465):

| Property name | Becomes |
| --- | --- |
| `hk…` | `NiFloatExtraData` named `<prop>:<node>`, only kept when animated or `Phoneme` |
| `ed_<name>` int | `NiIntegerExtraData` |
| `ed_<name>` bool | `NiBooleanExtraData` |
| `ed_<name>` float | `NiFloatExtraData` |
| `ed__f_<name>` string | `NiFloatExtraData` (parsed) |
| `ed_<name>` string | `NiStringExtraData` |

`Shield`/`Weapon` suffixes are upper-cased. Animated properties additionally produce a
`NiFloatExtraDataController` via `handleInlineTracks`; `Visibility` produces a
`NiVisController`.

#### 5.2.2 Multi-bound volumes

A `BSMultiBoundNode` carries its own bounding volume, and the engine culls against that
instead of working one out from the geometry — which is the whole reason the class
exists. It is three blocks deep: the node names a `BSMultiBound`, which names a
`BSMultiBoundData`, which is an oriented box or a sphere.

The reference handles none of this: `FBXWrangler.cpp` has no occurrence of `MultiBound`
in either direction, and ck-cmd's only mention of it anywhere is `geometry.cpp` counting
`BSMultiBound` for the BSXFlags term annotated *"wrong"* (§9).

The class survives on its own (§5.2); this is the payload, and it is written **twice**,
as the collision material (§4.8) and the effect shader (§5.3.2) are:

- The **exact half** is `multi_bound_type` naming the data class, one `mb_` property per
  field, and the node's own `Culling Mode`. This is the authoritative copy and the only
  thing the import reads. A class the schema does not know, or one that is not
  `BSMultiBoundData`, is reported and dropped.
- The **visible half** is a tessellated mesh under the node, suffixed `_multibound`,
  positioned at the volume's centre and rotated by its matrix. An oriented box becomes a
  box of half its stated size — `Size` is the full length of each side — and a sphere
  becomes a sphere.

The import recognises the suffix and skips it, exactly as it skips `_rb` and `_sp`, so
the mesh never becomes geometry in the rebuilt file. Without that it would come back as
a box floating inside every multi-bound node.

A culling volume that exists only as six numbers is one nobody will ever notice is
wrong, which is the same reason the other two are written twice.

Losing it leaves a multi-bound node bounding nothing. Nothing looks wrong — the engine
culls against an empty volume, and the saving the node existed for is silently gone.

#### 5.4.1 What travels, and what does not

Extra data rides on the node it hangs from, as `extra_data` (a count) and one `xd_<i>_`
group per block, carrying the class, the name and every field through `NifFieldCodec`.
A class the schema does not know, or one that is not `NiExtraData`, is reported and
dropped rather than guessed at.

`BSXFlags` is deliberately excluded in both directions. It is extra data like the rest,
but it is recalculated from the rebuilt graph (§5.2, `bsxflags-spec.md`), so carrying it
as well leaves the file with two — and the engine reads the first it finds.

The rebuild appends rather than assigns, because the calculated `BSXFlags` is already on
the root's list by the time this runs.

Two fields are not carried. `Name` is written separately, and `Next Extra Data` is the
older chain form that the list supersedes; a carried link would point into a block list
that no longer has that block.

### 5.5 Skinning

`convertSkins` → `Accessor<AccessSkin>` (L2811–3110). Produces `NiSkinInstance` +
`NiSkinData` + `NiSkinPartition`, one partition per FBX skin deformer.

When exporting a skin with more than 60 bones, partitions are rebuilt with
`remake_partitions(shape, bones = 60, weights = 4)`.

### 5.6 Animation

`checkAnimatedNodes` (L4352) classifies each animation stack. A node is animated if
any of its nine TRS component curves has keys. If the node is a skinned bone (or an
external skeleton was supplied) the stack is *skinned*, otherwise *unskinned* and the
node is recorded as an unskinned bone.

Node properties whose name contains `hk` are collected as **annotations** (enum-typed)
or **float properties** (animated).

`buildKF` (L4285) builds one `NiControllerManager` on the root with:

- One `NiControllerSequence` per unskinned stack, named after the stack.
- A `NiMultiTargetTransformController` (flags 44, frequency 1, phase 0) targeting the
  root, with every animated node as an extra target.
- A `NiDefaultAVObjectPalette` listing every target plus the root.
- Manager flags 12, frequency 1, phase 0.

Per animated node, `convert` (L4029) emits a `ControlledBlock` with a
`NiTransformInterpolator` whose base transform is filled with `0xFF7FFFFF` sentinels
(meaning "unset"), and `controllerType = "NiTransformController"`.

Each sequence gets start 0, stop `local.stop - local.start`, frequency 1,
`CYCLE_CLAMP`, and a `NiTextKeyExtraData` with `start` at 0 and `end` at the stop time.

Curve interpolation maps back cubic → `QUADRATIC_KEY`, linear → `LINEAR_KEY`,
constant → `CONST_KEY`; when several components disagree the **highest** wins. Missing
curves count as `CONST_KEY`. Bezier tangents are adjusted by `AdjustBezier` and
singularities in Euler tracks handled by `handle_singularities`.

Skinned animations are written out as Havok (`.hkx`) behaviour files instead, and the
root gains a `BSBehaviorGraphExtraData` (`BGED`) pointing at the generated project
under `animations/<name>`.

### 5.7 Collision

`buildCollisions` (L5092). For each deferred `_rb`/`_sp` node, every mesh under the
nearest enclosing non-body ancestor is collected with its accumulated local transform,
and handed to `build_physics`.

`build_physics` (L4860) creates:

- `bhkCollisionObject` + `bhkRigidBodyT` normally; `bhkBlendCollisionObject` +
  `bhkRigidBody` when exporting a rig (with `HeirGain`/`VelGain` 1).
- Transform from the local transform (global when exporting a rig), translation scaled
  by `0.01428`.
- Shape, centre of mass, inertia tensor and mass from a Havok body fitted by
  `HKXWrapper::build_body`.

Motion settings by resulting collision layer:

| Layer | Motion system | Deactivation | Quality | Collision flags |
| --- | --- | --- | --- | --- |
| `ANIMSTATIC` / `BIPED` | `MO_SYS_BOX_INERTIA` | `LOW` | `MO_QUAL_FIXED` | `SET_LOCAL \| SYNC_ON_UPDATE` |
| `CLUTTER` | `MO_SYS_DYNAMIC` | `LOW` | `MO_QUAL_MOVING` | `SYNC_ON_UPDATE` |
| anything else (static) | `MO_SYS_BOX_STABILIZED` | `OFF` | `MO_QUAL_INVALID` | `SYNC_ON_UPDATE` |

Statics additionally get **mass 0 and a zeroed inertia tensor**. A rigid body with an
animated ancestor is forced to `ANIMSTATIC`; `BIPED` bodies read `body_part` from the
node property.

Havok shapes convert back via `convert_from_hk` (L4665), the mirror of §4.8, covering
list, convex transform, transform, MOPP, sphere, box, capsule, convex vertices and
compressed mesh. Capsule endpoints are **swapped** relative to Havok.

#### 5.7.1 Convex hull plane equations

`bhkConvexVerticesShape` stores the hull's faces as half spaces, and the convention is
stated in nif.xml rather than inferable:

> the normal points **to the exterior**, and the fourth component is **minus** the dot
> product of that normal with any vertex on the plane.

So a face at *x = +r* with normal `(1, 0, 0)` stores `-r`. Havok then tests containment
with `n·x + d <= 0`. ck-cmd never computes these — it copies
`hkpConvexVerticesShape::getPlaneEquations()` verbatim — so the convention only becomes
this port's problem, and it is one where a mistake is invisible: the planes still sit in
the right places, and what inverts is which side of each counts as solid. A hull built
with the sign flipped collides everywhere except where the object is.

A symmetric shape hides it completely. Negating every distance of a shape centred on the
origin maps its plane set onto itself, so a box round-trips correctly under either sign.
Testing this needs a hull that is not symmetric about any axis.

nif.xml also states that both `Vertices` and `Normals` are **lexicographically sorted**.
The shipped files carry Havok's own order; this port does not reproduce it.

#### 5.7.2 Mass, and the tensor that follows from it

Mass and inertia are different kinds of fact, and only one of them is authored.

The **mass is authored**: ck-cmd's own generated examples give a box and a sphere of
different sizes the same mass, `0.0232956`, which no density can produce. It has to be
carried; nothing about a scene implies it.

The **inertia tensor is derived** from that mass and the shape, which is why ck-cmd does
not carry it either — it asks `hkpInertiaTensorComputer`. Havok's tensors are the
textbook ones for a solid body of uniform density, so they can be computed directly, and
the check that this is the same computation is that it reproduces what the generated
files hold, given only the mass and shape those files also carry:

| Shape | Tensor | Reproduces |
| --- | --- | --- |
| Box, half-extents *h* | `m/3 (h² + h²)` per axis | `generate_rb_box.nif`, to 9 dp |
| Sphere, radius *r* | `2/5 m r²` | `generate_rb_sphere.nif`, to 9 dp |
| Capsule | cylinder + two hemispheres, parallel axis, rotated onto the axis | — |
| Convex hull | integrated over the faces | `generate_rb.nif`, to 9 dp |

The face integration has a trap. Over a tetrahedron the squared terms integrate with
`det/60` and the cross terms with `det/120`; sharing one constant between them gives a
diagonal exactly **half** of what it should be, with the products still correct.

**Statics keep neither.** The layer is the whole of the decision, per the table above,
and the carried mass is dropped rather than trusted — a static with a mass is treated as
movable, which is how a piece of scenery ends up falling through the world. Note that
all three `generate_rb*` examples are `SKYL_STATIC` and *still* carry a mass and tensor:
they come from ck-cmd's `generate_rb` generator, not from its import path, so they
disagree with ck-cmd's own rule. Importing them zeroes both, by design.

#### 5.6.0 Attached controllers and sequences are two halves

> §5A covers the animation layer end to end; this is the part that decides what a
> rebuilt sequence points at.

A file with a `NiControllerManager` carries each animated controller **twice over**, and
rebuilding only one half leaves an animation with nothing to apply it to:

- The **controller** hangs on the thing it drives — a shader property, a particle
  system — and holds a **blend** interpolator, which contains no keys. That is the slot
  the manager writes the mixed value into as it crossfades whatever is playing.
- Each **sequence** holds its own interpolator with the actual keys, and its controlled
  block names the attached controller.

So one controller serves every sequence: it is built once per host, class and controller
id, and found again after that. `TestNifFile_Animated_LE.nif` has three sequences —
`mBegin`, `mLoop`, `mEnd` — all naming the same two shader controllers and the same
emitter controller.

A controller may drive more than one thing, and the blend slots differ. nif.xml spells
out the case that matters: `NiPSysEmitterCtlr`'s two interpolators are
`['BirthRate', 'EmitterActive']`, the second on `Visibility Interpolator`. Its boolean
track belongs in that slot of the *same* controller — not on a second controller of the
same class, which is what keying on class alone produces.

#### 5.6.1 Undoing the invented sequence

A controller that no sequence names is attached directly to what it controls and runs on
its own — a shader fading, a texture scrolling, a node blinking. FBX cannot say that:
every animation there belongs to a stack. So the export gathers those controllers into
an invented sequence named `Take 001`, which is what FBXWrangler calls the stack it
invents for the same reason (§4.7.3), and the import has to undo the invention.

Writing that sequence back as a real one is wrong in both directions at once. It puts a
`NiControllerManager`, a `NiControllerSequence`, a `NiDefaultAVObjectPalette`, a
`NiMultiTargetTransformController` and a `NiTextKeyExtraData` into a file that had none
of them, and it leaves the controllers themselves unattached to what they control.

So a sequence by that name is unpacked instead: each property becomes a controller of
its recorded class, hung from the block it drives. Which block that is follows from the
class — a `...ShaderProperty...` controller from the shader property, a
`...AlphaProperty...` one from the alpha property, anything else from the node.

Two details are not obvious:

- **A controller may already be there.** A carrier that owns more of a controller than
  its keys rebuilds it first: a flipbook comes back complete with its texture list,
  needing only the interpolator that says which frame is showing. So an existing
  controller of the same class is reused rather than duplicated.
- **Only one that is still waiting.** The reuse matches a controller with **no
  interpolator**. One that already has keys is a different controller that happens to
  share a class, and a single shader can easily carry several — one scrolling U, another
  scrolling V. Matching on class alone collapses them into one.

### 5.8 Output file settings

`SaveNif` (L5793) writes version `20.2.0.7`, user version 12, user version 2 **83**
(Skyrim LE).

- Optional `mergeNodes` flattens one level of nested `NiNode`s, pushing the parent's
  transform onto each child and hoisting extra data to the root.
- Blocks are re-collected by `RebuildVisitor`.
- `BSXFlags` named `BSX` is recalculated from the block list; bit 0 is forced when
  skinned animations exist.
- When exporting a rig, a `SkeletonID` `NiIntegerExtraData` of `207579012` is added and
  every `NiNode` gets flags `524302`.

---

## 5A. Animation in this port, end to end

§4.7 and §5.6 record what FBXWrangler does. This section records what *this* does, in
both directions, because the two do not line up field for field and the differences are
the kind that are invisible when wrong.

Everything passes through one neutral form, `Conversion/AnimationData.cs`, so neither
side knows about the other:

```
AnimSequence   name, start, stop, tracks
  AnimTrack      node name, translation/rotation/scale curves, properties
    AnimProperty controller identity, one curve per component
      AnimCurve    keys
        AnimKey      time, value, interpolation
```

`AnimSequence` is a NIF `NiControllerSequence` and an FBX `AnimationStack`; `AnimTrack`
is everything animated on one node. A track is keyed by node *name*, which is what both
formats use to bind animation to a target, and what makes duplicate node names
unfixable in either.

### 5A.1 What FBX splits four ways

An `AnimationStack` is the take. An `AnimationLayer` under it holds the tracks — always
one, named `Default`, as FBXWrangler writes it. An `AnimationCurveNode` binds one
property of one model, and an `AnimationCurve` under that holds one component's keys.

Vector properties are addressed by axis (`d|X`, `d|Y`, `d|Z`) and scalar ones by their
own name (`d|` + the property name). That addressing is the only thing that says how
many curves to expect, so it has to match how the property was declared.

A property must be **declared on the model** as well as animated: a curve bound to a
property the model does not have is dropped by most importers without complaint, since
there is nothing for it to drive. So each property is declared with its first key's
value as the static one, typed by what it is — `ColorRGB` for a colour, `Visibility`
for visibility, `bool` or `Number` otherwise.

Time is FBX's integer unit, 46,186,158,000 per second, rounded on the way out.

Both spans are written on the stack — `LocalStart`/`LocalStop` and
`ReferenceStart`/`ReferenceStop` — because importers differ over which they trust.

### 5A.2 Key interpolation

| NIF `KeyType` | Neutral | FBX `KeyAttrFlags` |
| --- | --- | --- |
| 1 `LINEAR_KEY` | `Linear` | `0x00000004` |
| 2 `QUADRATIC_KEY` | `Cubic` | `0x00000008 \| 0x00000100` |
| 5 `CONST_KEY` | `Constant` | `0x00000002` |

Quadratic keys carry tangents FBX cannot express directly, so `TangentAuto` is set and
the importer chooses tangents that reproduce the shape.

FBX stores interpolation run-length encoded: `KeyAttrFlags` holds each distinct value
once and `KeyAttrRefCount` says how many consecutive keys share it.

Coming back, a NIF key group has **one** interpolation for all its keys where FBX has
one per key, so the group takes the smoothest present — constant is coarsest, then
linear, then quadratic. Taking the first key's would quietly flatten a curve whose first
segment happens to be linear.

### 5A.3 Rotation

The NIF side has two forms and they are read differently:

- **Quaternion keys** are decomposed to Euler XYZ, and written back as quaternions.
- **`XYZ Rotations`** (rotation type 4) are three separate float groups, in radians.
  They are read as three curves and always marked cubic, because the three groups can
  disagree about interpolation and a single track cannot.

FBX rotation is Euler XYZ in **degrees**, so radians convert on the way out and back.

### 5A.4 Where animation is found on the way out

`ReadAnimations` gathers from two places, and the second exists because FBX has no way
to say what it finds:

1. Every `NiSequence` in the file becomes a sequence, read through its controlled
   blocks.
2. Controllers **no sequence names** are gathered into one invented sequence called
   `Take 001` — the name FBXWrangler gives the stack it invents for the same reason
   (§4.7.3). §5.6.1 undoes this on the way back.

A controller is claimed by a sequence if any controlled block points at it, and claimed
controllers are skipped by the second pass. In a file like Bethesda's animated effects
the same controller block is both attached to its target and named by every sequence,
and reading it twice would play it twice.

The chains searched are the node's own and those of the properties hanging off it —
shader property, alpha property, and the older `Properties` list — because a shader's
fade is controlled from the property but binds to the node.

Two kinds are deliberately not gathered:

- **Transform controllers**, which move the node and are already the track's own
  translation, rotation and scale curves.
- **Flipbook controllers**, which travel by their own carrier with their texture list
  (§4.3). Gathering them here as well would write them twice, once with textures and
  once as a bare float track.

A controller is recognised by **what its interpolator drives**, not by its class name:
anything on a float, a boolean or a point3 interpolator is a named scalar or colour.
That is what lets `BSEffectShaderPropertyFloatController` and `NiPSysEmitterCtlr` travel
without either being mentioned by name.

### 5A.5 Where animation goes on the way back

`WriteAnimations` resolves every track's node first, since a sequence with no resolvable
target is a sequence with nothing to write and the manager should not exist for it. Then:

- A `NiControllerManager` on the root, with a `NiMultiTargetTransformController` naming
  every node whose **transform** moves — a node listed there without transform keys
  would be driven to nothing.
- A `NiDefaultAVObjectPalette` of those targets.
- One `NiControllerSequence` per sequence, with a `NiTextKeyExtraData` holding the
  start and end text keys.
- Per controlled block, an interpolator with the keys, the four identity strings, and
  the attached controller the entry drives (§5.6.0).

Sequences are written to play **from zero**: where they sat on the source timeline is
not something the engine has a use for, so the length is `stop - start` and every key
shifts by `-start`.

### 5A.6 Known limits

| Limit | Consequence |
| --- | --- |
| A track binds by node name | Duplicate names cannot be told apart, in either format |
| A controller needs an interpolator to be recognised | One with none carries no animation, so this layer cannot see it. Where such a controller matters it travels with the structure instead — see §4.9A for particle systems |
| One layer per stack | Layered animation is not represented |

---

## 6. Traversal invariants

Both directions depend on ordering that is easy to lose:

1. **Bones before skins.** `processSkins` runs after the whole graph walk because
   clusters need their bone nodes to exist.
2. **Bodies before constraints.** A constraint referencing a body not yet built throws
   `"Wrong Nif Hierarchy, entity referred before being built!"`.
3. **Collision nodes are leaves.** `_rb`/`_sp` children are deferred, never recursed
   into as ordinary nodes.
4. **Visited set.** Blocks consumed by a specialised handler (skin data, shader
   properties, texture sets, interpolators, controller sequences, `BSXFlags`) are
   marked visited so the generic walk does not emit them a second time.

---

## 6A. Meshes the game ships with NaN in them

A handful of vanilla effect meshes hold vertices that are **not numbers** — in
`meshes/magic/explosionilusiondark01.nif` the `lightRays` shape has 297 NaN vertices,
and the node above it has a rotation matrix that is NaN in all nine entries. Three such
meshes turned up in a 3,000-file sample, all under `meshes/magic/`.

This is the file's own data and not a decoding fault. The shape beside it in the same
file, `lightRaysIC01:0`, shares its vertex descriptor exactly — `0x0003B00007650408`,
half-precision positions — and decodes to real numbers through the same code.

Two consequences:

- The export **warns** rather than staying silent. A DCC tool handed a NaN vertex does
  not report a bad mesh; it misbehaves, and the person looking at it has no reason to
  suspect the source.
- The corpus sweep for collapsed geometry (§7) ignores an all-NaN mesh and fails only on
  a collapse onto a *finite* point, which is the shape of a field read from the wrong
  place. The fixture-level test admits neither, since no fixture has one.

---

## 7. What is not round-tripped

Three different things, and they are worth keeping apart. Something derived is not a
loss; something dropped is.

### 7.1 Derived rather than carried

These are computed from the rebuilt graph. Carrying them would describe the file the FBX
came from rather than the one just built, and for several of them the source value is
the thing most likely to be stale.

| What | Where |
| --- | --- |
| `BSXFlags` | `bsxflags-spec.md`; every bit is a fact about the block graph |
| Tangent space | §5.3.1, from NifSkope's algorithm |
| Inertia tensors | §5.7.2, from the mass and the shape |
| Convex hull planes | §5.7.1, from the hull |
| Collision shape size | §4.8; refitted from the tessellated geometry, so a DCC edit wins |
| Bounding spheres | recomputed from the vertices |
| `NiSkinPartition` | rebuilt from the weights |
| MOPP data | regenerated; see §8 |
| `NiDefaultAVObjectPalette`, `NiTextKeyExtraData` | rebuilt with the controller manager |

### 7.2 Deliberately discarded

| What | Why |
| --- | --- |
| A static body's mass and inertia | §5.7.2. A static carrying a mass is treated as movable, which is how scenery falls through the world. ck-cmd zeroes both the same way |
| The source's `BSXFlags` value | Recalculated as above, and carrying it as well would leave the file with two (§5.4.1) |
| Uninitialised fields | Some Havok fields are `0xCD` throughout in the files that ship — the debug heap's fill pattern. There is nothing there to reproduce |

### 7.3 Lost

Real gaps, each with its reason recorded where it bites.

| What | Consequence | Where |
| --- | --- | --- |
| A controller with no interpolator, outside a particle system | Not recognised as animation, and only particle systems carry these structurally so far | §5A.6, §4.9A |
| Array order within a rebuilt convex hull | The vertices and planes agree, but arrive in the fit's order rather than Havok's, which nif.xml says is lexicographic | §5.7.1 |

---

## 8. Havok dependencies

FBXWrangler links the Havok SDK directly. This port does not, and takes NifSkope's
approach instead: the one piece that genuinely needs Havok is loaded from an external
DLL at run time, and everything else is implemented directly.

| FBXWrangler dependency | Used for | How this port covers it |
| --- | --- | --- |
| `hkpMoppUtility` | Building MOPP bounding-volume trees | **`NifMopp.dll`**, see below |
| `hkpShapeConverter` | Tessellating primitives to geometry | Implemented directly; box, sphere and capsule tessellation is elementary |
| `hkGeometryUtility::createConvexGeometry` | Convex hulls | Implemented directly (quickhull) |
| `HKXWrapper::build_body` | Fitting a Havok body to FBX meshes | Implemented directly from the node naming conventions in §3.1 |
| VHACD | Approximate convex decomposition | Only needed for automatic decomposition, which is not part of the conversion itself |
| boundingmesh | Collision mesh simplification | As above |

### 8.1 NifMopp.dll

MOPP code indexes a mesh collision shape, and generating it needs the Havok SDK.
NifSkope ships a small DLL compiled against that SDK and loads it dynamically
(`src/spells/moppcode.cpp`). This port binds the **same library with the same
exported ABI**, so the identical binary works:

```c
int __stdcall GenerateMoppCode(int nVerts, Vector3 const* verts,
                               int nTris, Triangle const* tris);
int __stdcall GenerateMoppCodeWithSubshapes(int nShapes, int const* shapes,
                                            int nVerts, Vector3 const* verts,
                                            int nTris, Triangle const* tris);
int __stdcall RetrieveMoppCode(int nBuffer, char* buffer);
int __stdcall RetrieveMoppScale(float* value);
int __stdcall RetrieveMoppOrigin(Vector3* value);
```

`GenerateMoppCode` returns the code length, then `RetrieveMoppCode` fills a buffer of
that size and the origin and scale are read back separately.

Practical constraints, inherited from it being a Havok build: Windows only, and its
bitness must match the host process.

### 8.2 mopper.exe — the portable backend

[niftools/mopper](https://github.com/niftools/mopper) wraps the same Havok call in a
standalone executable that talks pure stdin/stdout, with no GUI and no COM. It
therefore **runs unmodified under Wine**, which is what makes MOPP generation possible
on Linux, and running it out-of-process also removes the bitness matching that
in-process P/Invoke demands.

Invocation:

| Command | Meaning |
| --- | --- |
| `mopper.exe -msm --` | Simple mesh shape, read from stdin |
| `mopper.exe -ccm --` | Full compressed mesh shape, read from stdin |
| `mopper.exe -msm <file>` | As above, from a file |
| `mopper.exe --` / `mopper.exe <file>` | Backward compatible aliases for `-msm` |

**Input** (`-msm`), whitespace-separated ASCII:

```
<vertex count>
<x> <y> <z>              x vertex count
<triangle count>
<a> <b> <c>              x triangle count
<material index count>
```

**Output**, one number per line:

```
origin.x
origin.y
origin.z
scale                    written with precision 16
<mopp code length>
<byte as integer>        x length
<triangle count>
<welding info>           x triangle count
```

Three things to get right:

- Floats must be written and parsed **invariantly**. A comma decimal separator makes
  mopper stop reading mid-vertex, and it will happily emit a truncated mesh's MOPP.
- The material index count must be **0**. mopper reads each index with `operator>>`
  into a `hkUint8`, which consumes a *character* rather than a number, so any non-zero
  count is misparsed.
- On failure mopper prints Havok's error text instead of numbers, so the parse must
  reject non-numeric output rather than trust the exit code.

`-ccm` additionally returns a whole `bhkCompressedMeshShape`: bounds, big verts and
tris, transforms, and per-chunk vertices, indices, strip lengths and welding info. Note
that it prints the **last MOPP byte first**, then bytes `0 .. n-2`; that rotation has
to be undone to recover the real code.

### 8.3 Availability

Absence of both backends is **not** an error. `IMoppGenerator` is resolved lazily,
everything that does not need MOPP keeps working, and a `bhkMoppBvTreeShape` can still
be written by reusing the MOPP data already present in a source NIF.

**NIF → FBX collision never needs any of this**: that direction only tessellates
shapes, and discards MOPP data outright (§4.8).

---

## 9. Known defects in the reference

Reproduced only where behaviour depends on them; otherwise fixed and noted.

| Location | Defect |
| --- | --- |
| L455 | `gl_blend_modes_to_value` tests `"GL_ONE"` but the writer emits `"ONE"`; masked by the default |
| L841 | `TOMATRIX3` has no `return` |
| L836 | `return device->write(v, 6) == 3` in `tUshortVector3` — always false |
| L1984 | `setPropertyAnimationOnDefaultStack` calls `span.SetStart` where `SetStop` is meant |
| L753, L760 | `vector<Triangle>& tris = vector<Triangle>(0)` binds a reference to a temporary |
| §3 | `unsanitizeString` is not injective; a literal `_s_` in a name is corrupted |
| L2818, L3096 | The skin path's dismember branch is commented out, so `export_skin` builds a plain `NiSkinInstance` and then casts it to `BSDismemberSkinInstanceRef` and dereferences `bsskin->partitions`. That cast yields NULL |
| §5.2.2 | The FBX path handles no `BSMultiBound`. A multi-bound node comes back bounding nothing, and the engine culls against an empty volume. Fixed here |
| §4.3.1 | The FBX path handles no `BSEffectShaderProperty`. Export casts the shader to `BSLightingShaderProperty` and takes the null, so the shape leaves with no material; import only ever builds a lighting shader. Handled elsewhere in ck-cmd, so this is a gap in the FBX path. Fixed here — see §5.3.2 |

---

## 10. Deviations in this port

| Area | Decision |
| --- | --- |
| FBX library | MeshIO's raw node layer, with scene semantics written here. No FBX SDK, so `EvaluateGlobalTransform`, `GenerateTangentsDataForAllUVSets`, `SplitMeshesPerMaterial`, `Triangulate` and `CreateMissingBindPoses` must be implemented directly. |
| ASCII FBX output | Not supported; MeshIO's ASCII writer emits invalid escapes. Binary only, which is what the reference emits anyway. |
| Miniball | Replaced with an equivalent bounding-sphere routine. |
| Havok | No SDK link. MOPP generation goes through `NifMopp.dll` as NifSkope does; shape tessellation and convex hulls are implemented directly. See §8. |
| Reference defects | Fixed unless behaviour depends on them, and listed in §9. |
| Havok material | Carried as ck-cmd carries it (§4.8): an FBX material on the collision mesh named after the enum, with the layer as a `CollisionLayer` property. The names come from nif.xml's own `SkyrimHavokMaterial` and `SkyrimLayer` rather than the table ck-cmd hand-wrote, so the two spellings cannot drift. |
| Effect shaders | Carried in both directions (§4.3.1, §5.3.2). The reference drops them: its export casts to `BSLightingShaderProperty` and takes the null, its import only builds lighting shaders. |
| Tangent space | Generated from NifSkope's `spTangentSpace` (§5.3.1) rather than obtained from the FBX SDK, which also removes the need for ck-cmd's tangent/binormal swap. |
| Inertia tensors | Computed directly (§5.7.2) rather than obtained from Havok, and held to the numbers ck-cmd's generated files carry. |
| Node kinds | The NIF block type of every node, and of the root, travels in a `nif_block_type` property. FBX has one kind of node; NIF has a dozen that differ in what the engine does with them. The root matters most: `BSXFlags` asks twice whether it is exactly `NiNode` (see `bsxflags-spec.md` §3.2, §3.4), so flattening it changes what the file claims about itself. |
| `bhkCOFlags` | Carried in a `nif_collision_flags` property rather than derived from the layer. ck-cmd derives them because an FBX authored in a DCC tool has none to carry; carrying wins where the data exists, and the derivation remains the fallback. |
| `BSXFlags` | Recalculated on import rather than carried, as ck-cmd does. See `bsxflags-spec.md`. |
