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

## 7. Blocks deliberately dropped

Recalculated or regenerated rather than round-tripped: `BSXFlags`,
`NiDefaultAVObjectPalette`, `NiSkinPartition`, MOPP data, bounding spheres,
`NiTextKeyExtraData` on export.

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

---

## 10. Deviations in this port

| Area | Decision |
| --- | --- |
| FBX library | MeshIO's raw node layer, with scene semantics written here. No FBX SDK, so `EvaluateGlobalTransform`, `GenerateTangentsDataForAllUVSets`, `SplitMeshesPerMaterial`, `Triangulate` and `CreateMissingBindPoses` must be implemented directly. |
| ASCII FBX output | Not supported; MeshIO's ASCII writer emits invalid escapes. Binary only, which is what the reference emits anyway. |
| Miniball | Replaced with an equivalent bounding-sphere routine. |
| Havok | No SDK link. MOPP generation goes through `NifMopp.dll` as NifSkope does; shape tessellation and convex hulls are implemented directly. See §8. |
| Reference defects | Fixed unless behaviour depends on them, and listed in §9. |
