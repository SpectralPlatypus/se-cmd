# Particle systems: what a NIF stores, and what FBX can hold

Extracted from `nif.xml` 0.9.1.0 (vendored at `External/nifxml/nif.xml`) and from the
one particle system in the test corpus, `TestNifFile_Animated_LE.nif`. The FBX side is
an assessment of the format's object model against that, and of the choice se-cmd has
made.

---

## 1. The block families

nif.xml declares **98** particle-related blocks. They fall into five groups.

**Systems** — the scene node. `NiParticles` → `NiParticleSystem` →
`BSStripParticleSystem`, `NiMeshParticleSystem`. Bethesda also has
`BSMasterParticleSystem`, a `NiNode` holding several systems at once.

**Data** — `NiParticlesData` → `NiPSysData` → `BSStripPSysData`, `NiMeshPSysData`.

**Modifiers** — the substance. `NiPSysModifier` has 28 direct subclasses, including
the emitter family:

```
NiPSysEmitter
  NiPSysMeshEmitter
  NiPSysVolumeEmitter
    BSPSysArrayEmitter, NiPSysBoxEmitter, NiPSysCylinderEmitter,
    NiPSysSphereEmitter → NiPSysTrailEmitter
```

and the force fields (`NiPSysFieldModifier`: air, drag, gravity, radial, turbulence,
vortex), the colliders (`NiPSysColliderManager` → `NiPSysPlanarCollider`,
`NiPSysSphericalCollider`), and Bethesda's own (`BSPSysLODModifier`,
`BSPSysScaleModifier`, `BSPSysSimpleColorModifier`, `BSPSysSubTexModifier`,
`BSPSysInheritVelocityModifier`, `BSPSysRecycleBoundModifier`,
`BSPSysStripUpdateModifier`, `BSPSysHavokUpdateModifier`).

**Controllers** — `NiPSysModifierCtlr` and its 20-odd subclasses, which animate one
value of one modifier, plus `NiPSysUpdateCtlr` and `NiPSysResetOnLoopCtlr`.

**Legacy** — `NiParticleModifier` and the `Ni3dsParticleSystem` / `NiPS*` /
`NiPhysXPS*` families, none of which appear in Skyrim.

---

## 2. What a Skyrim particle system actually stores

### 2.1 No per-particle data

`NiParticlesData` declares `Radii`, `Sizes`, `Rotations`, `Rotation Angles` and
`Rotation Axes` — and every one of them carries `vercond="!#BS202#"`, where `#BS202#`
expands to `((#VER# #EQ# 20.2.0.7) #AND# (#BSVER# #GT# 0))`. So in **every Bethesda
20.2 file** — Fallout 3, Skyrim LE, Skyrim SE alike — those arrays are not in the
format at all. The `Has …` booleans remain and describe a buffer that only ever exists
at runtime.

The corpus fixture agrees: `Vertices = 0`, `BS Max Vertices = 18`. Eighteen is the
capacity of a buffer the engine fills, not eighteen particles that were saved.

ck-cmd's NIF version converter states the same fact from the other side. Upgrading an
older file, `ConvertNif.cpp`'s `visit_object(NiParticleSystem&)` does:

```cpp
data->SetBsMaxVertices(data->GetVertices().size());
data->NiGeometryData::SetVertices(vector<Vector3>());
data->SetVertices(vector<Vector3>());
data->SetVertexColors(vector<Color4>{});
data->SetRadii(vector<float>{});
data->SetSizes(vector<float>{});
```

— it deliberately empties them and keeps only the count.

> **Consequence.** There is no geometry to export. A particle system is a *description*
> of how particles will be made, not a record of any that were.

### 2.2 What is left

The data block keeps the texture atlas and the size-over-speed curve:
`Num Subtexture Offsets`, `Subtexture Offsets` (a `Vector4` per frame),
`Aspect Ratio`, `Aspect Flags`, `Speed to Aspect Aspect 2`,
`Speed to Aspect Speed 1`, `Speed to Aspect Speed 2` — all `vercond="#BS202#"`, i.e.
Bethesda-only additions.

The system block keeps `World Space` (are particles born in world or object space),
the `Far Begin`/`Far End`/`Near Begin`/`Near End` fade distances, and the modifier
list.

### 2.3 LE and SE differ in layout

`NiParticleSystem` is the one block where Bethesda's 20.2 inheritance shift shows
through. nif.xml handles it by doubling up `NiGeometry`'s rows with `onlyT` and
`excludeT` on `NiParticleSystem`:

| Field | LE (`#BSVER#` 83) | SE (`#BSVER#` 100) |
| --- | --- | --- |
| `Bounding Sphere`, `Skin` | from `NiGeometry` | on `NiGeometry`, `onlyT="NiParticleSystem"` |
| `Data` | `NiGeometry`, `vercond="#NI_BS_LT_SSE#"` | on `NiParticleSystem` itself |
| `Vertex Desc` | absent | present, `vercond="#BS_GTE_SSE#"` |
| `Skin Instance`, `Material Data` | from `NiGeometry` | `excludeT="NiParticleSystem"` — absent |

So an SE particle system carries a `BSVertexDesc` like a `BSTriShape`, and reaches its
data through its own ref rather than `NiGeometry`'s. Anything walking these blocks by
field name gets this for free from nif.xml; anything with hand-written block classes
has to encode the shift twice.

---

## 3. The modifier stack

A system's `Modifiers` array is the stack, and each modifier carries:

- `Name` — how a controller finds it (§4).
- `Order` — a `NiPSysModifierOrder` value fixing where in the frame it runs.
- `Target` — a `Ptr` back to the owning system.
- `Active`.

`Order` is coarse and shared: `ORDER_KILLOLDPARTICLES` 0, `ORDER_BSLOD` 1,
`ORDER_EMITTER` 1000, `ORDER_SPAWN` 2000, `ORDER_GENERAL` 3000, `ORDER_FORCE` 4000,
`ORDER_COLLIDER` 5000, `ORDER_POS_UPDATE` 6000, `ORDER_POSTPOS_UPDATE` 6500,
`ORDER_BOUND_UPDATE` 7000. The fixture's eleven modifiers use four values between them,
with four modifiers sharing `ORDER_GENERAL`, so **array order is the tie-break and is
itself data**.

### 3.1 The links out of the stack

Three kinds of reference leave a modifier, and they are what makes a particle system
part of a scene rather than a self-contained blob:

| Link | On | Points at | In the fixture |
| --- | --- | --- | --- |
| `Emitter Object` | `NiPSysVolumeEmitter` | `NiNode` | `PCloud06-Emitter` |
| `Emitter Meshes` | `NiPSysMeshEmitter` | `NiAVObject[]` | — |
| `Gravity Object` | `NiPSysGravityModifier` | `NiNode` | `Gravity01` |
| `Spawn Modifier` | `NiPSysAgeDeathModifier` | another modifier | `NiPSysSpawnModifier:1` |
| `Collider` | `NiPSysColliderManager` | `NiPSysCollider` | — |

The first three point at **named nodes elsewhere in the scene**. An emitter that has
lost its emitter object emits from the origin; a gravity modifier that has lost its
gravity object pulls towards the origin. Neither failure is visible in the file.

---

## 4. Controllers

`NiPSysModifierCtlr` adds one field — `Modifier Name` — and binds by that string
rather than by reference. The fixture's `NiPSysEmitterCtlr` names
`"NiPSysCylinderEmitter:0"`, which is the emitter's `Name`.

This matters for anything carrying the animation separately: the binding survives as
long as modifier names do, and needs no block indices. se-cmd's property tracks already
carry it — the controlled block's `Controller ID` is that same string (see
`docs/fbx-nif-conversion-spec.md` §4.7.3 and `Nif/NifAnimAccess.cs`).

---

## 5. What ck-cmd does

**In the FBX pipeline: nothing.** Neither `FBXWrangler.cpp` nor `HKXWrangler.cpp`
contains the word *particle*. A `NiParticleSystem` exported through FBXWrangler reaches
FBX as a bare node — no data block, no modifiers — and cannot come back.

The only particle code in ck-cmd is in `src/commands/nif/ConvertNif.cpp`, a NIF
*version* converter unrelated to FBX. It:

- rebuilds legacy `NiMaterialProperty` / `NiTexturingProperty` / `NiAlphaProperty`
  into a `BSEffectShaderProperty`;
- migrates `NiMaterialColorController` → `BSEffectShaderPropertyColorController` and
  `NiAlphaController` → `BSEffectShaderPropertyFloatController`, carrying flags,
  frequency, phase, start/stop and interpolator across;
- empties the per-particle arrays as quoted in §2.1;
- sets `Aspect Ratio` 1, `Texture Clamp Mode` 3, `Lighting Influence` 255 and node
  flags 524302.

The commented-out remainder (L3692–3707) shows an abandoned attempt to synthesise a
`NiTriShape` plus `NiPSysMeshEmitter` from the particle data — i.e. to give the system
geometry. Given §2.1 there was nothing to give it.

---

## 6. What FBX can hold

FBX has **no particle system object**. There is no `FbxParticle*` class, no procedural
emitter, and nothing that means what `NiPSysCylinderEmitter` means. Whatever is done,
no DCC tool will open the result and show a working particle system, because the format
has nowhere for one to live. That is the ceiling, and it is worth stating plainly before
comparing options.

What FBX does offer that is relevant:

| Mechanism | What it is | Fit |
| --- | --- | --- |
| Custom properties (`Properties70`, `U` flag) | Arbitrary typed name/value pairs on any object. Blender surfaces them as custom properties, Maya as extra attributes. | Carries the declarative description exactly. Opaque. |
| Object-to-object connections | The scene graph's own edges, between any two objects. | Carries the node links of §3.1 natively. |
| `Null` node hierarchies | Empties with names, transforms and parentage. | Makes a structure visible and editable in the outliner. |
| `FbxCache` + `FbxVertexCacheDeformer` | A per-frame point stream in a sidecar file, which is how Maya transports simulated nParticles. | The only FBX mechanism built for particles — but it carries a *simulation*, not a system. |
| Custom `NodeAttribute` types | A typed attribute on a node. | Non-standard types are dropped by most importers. |

### 6.1 The point cache is not the answer here

`FbxCache` is genuinely designed for this and would give a DCC tool something it can
play back. It is still the wrong tool:

- A NIF stores no simulation to bake (§2.1), so producing a cache means *running* the
  particle system — an emitter, eleven modifiers and a frame loop, i.e. reimplementing
  Gamebryo's particle engine.
- A cache is one-way. Baked points cannot be turned back into an emitter, so the
  reverse direction would still need the declarative description alongside.

It is worth naming as a possible *preview* addition, not as the carrier.

### 6.2 The realistic options

| | Carries the system | Carries the node links | Visible in a DCC tool | Reversible |
| --- | --- | --- | --- | --- |
| **A.** Nothing — ck-cmd | no | no | n/a | no |
| **B.** Flat custom properties on the system's node | yes | no | poorly: one long list | exactly |
| **C.** B, plus connections for the node links | yes | yes | poorly | exactly |
| **D.** A `Null` per modifier, each with its own properties and connections | yes | yes | well: the stack is a subtree | exactly |

---

## 7. Where se-cmd stands, and what is worth doing next

se-cmd implements **B** (`Fbx/FbxParticleWriter.cs`, `Nif/NifParticleWriter.cs`): the
system block, its data block and its modifier stack in order, as prefixed string
properties on the node that already stands for the system. The node keeps its name,
transform and animation; no geometry is invented for it. Everything in §2.2 and §3
survives a round trip except the links.

The gap is §3.1. Three of the fixture's links are dropped, two of them to named nodes
that exist in the exported scene as FBX Models:

```
NiPSysCylinderEmitter.Emitter Object -> NiNode "PCloud06-Emitter"
NiPSysGravityModifier.Gravity Object -> NiNode "Gravity01"
NiPSysAgeDeathModifier.Spawn Modifier -> NiPSysSpawnModifier "NiPSysSpawnModifier:1"
```

**C is the improvement worth making**, and it is small: an object-to-object connection
from the system's node to the target, labelled by the field it stands for. FBX carries
it natively, it survives renaming and reparenting in a DCC tool in a way a stored name
would not, and it removes the one class of silent loss left. The intra-stack link
(`Spawn Modifier`) needs no connection at all — the modifier's own `Name` identifies it,
exactly as a controller's `Modifier Name` does (§4).

**D is better ergonomics for the same information.** A `Null` per modifier would put the
stack in the outliner where a rigger could see and reorder it, and would shorten the
property names from `npsm_7_frame_count` to `frame_count`. It is a larger change and
buys nothing in fidelity over C, so it is worth doing only if editing particle stacks in
a DCC tool becomes a goal rather than transporting them.
