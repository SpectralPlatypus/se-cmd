# BSXFlags, and how ck-cmd calculates it

Extracted from [ck-cmd](https://github.com/aerisarn/ck-cmd):
`src/core/geometry.cpp` (`calculateSkyrimBSXFlags`, L37–160) and
`include/commands/Geometry.h` (`SingleChunkFlagVerifier`, `MarkerBranchVisitor`).
Line references are to the checkout used for extraction.

---

## 1. What it is

`BSXFlags` is a `NiIntegerExtraData` named `BSX`, hung off the root of nearly every
Skyrim NIF. Its integer tells the engine what kind of file this is: whether it animates,
whether it collides, whether it is a skeleton, whether its collision is one piece or
many. Get it wrong and the mesh still loads and still looks right — the game simply
treats it as something it is not.

It is **derived**, not authored. Every bit is a fact about the block graph, which is why
ck-cmd recalculates it on export rather than carrying it, and why `NifScan` compares the
calculated value against the stored one as a way of finding malformed files.

## 2. The bits

The list below is ck-cmd's own comment block, which is the fullest documentation of
these that exists.

| Bit | Meaning |
| --- | --- |
| 0 | Has Gamebryo animation. Not applicable to NIFs meant to be attached to others |
| 1 | Has Havok — at least one collision or phantom collision |
| 2 | Has Havok ragdoll. Really means "this is a skeleton", even with no ragdoll constraint |
| 3 | Has multiple Havok collisions |
| 4 | Has AttachLight / FlameNode / AddonNode |
| 5 | Has editor markers |
| 6 | Has dynamic Havok rigid bodies. Meaningless without bit 1 |
| 7 | Is a single collision, or a single kinematic chain (see §5) |
| 8 | `bIKTarget` / `needsTransformUpdates` — **never set in vanilla Skyrim or its DLCs** |
| 9 | `bExternalEmit` |
| 10 | `bMagicShaderParticles` — **never set in vanilla** |
| 11 | `bLights` — **never set in vanilla** |
| 12 | `bBreakable` — **never set in vanilla** |
| 13 | `bSearchedBreakable` — runtime only, **never set in vanilla** |

ck-cmd's `bsx_flags_t` is a `std::bitset<12>`, so bits 12 and 13 cannot be produced by
the calculation at all. Bits 8, 10 and 11 are representable and never set.

## 3. The calculation

### 3.1 First pass: a census of the block list

- `num_collisions` — blocks deriving from `bhkCollisionObject`
- `num_phantom_collisions` — blocks deriving from `bhkSPCollisionObject`
- `isSkeleton` — any `bhkBlendCollisionObject`
- `isSkinned` — any `NiSkinInstance`, whose bones are collected
- `hasMultiBound` — any `BSMultiBound`
- `hasCollisionList` — any `bhkListShape` (computed and never used)

`bhkSPCollisionObject` and `bhkCollisionObject` are siblings, both under
`bhkNiCollisionObject`, so a phantom is **not** counted as a collision.
`bhkBlendCollisionObject` *does* derive from `bhkCollisionObject`, so a skeleton's
blend objects count towards `num_collisions`.

### 3.2 External skeleton

If the file is skinned and the root derives from `NiNode`: remove the root's direct
children from the set of bones, and if nothing is left, the file is skinned entirely by
bones it does not contain. `hasExternalSkeleton` is then true — but only when the root
is **exactly** `NiNode`, not a subclass such as `BSFadeNode`.

### 3.3 Second pass: per block

| Bit | Set when |
| --- | --- |
| 0 | a `NiTimeController` or `BSValueNode` exists, **and** not `isSkeleton`, **and** not `hasExternalSkeleton` |
| 2 | `isSkeleton` |
| 4 | a `BSValueNode` exists, or an `NiNode` whose name contains `AddonNode` |
| 6 | a `bhkRigidBody` exists with `isSkeleton`, or with a quality type other than `MO_QUAL_INVALID` (0) and `MO_QUAL_FIXED` (1) |
| 9 | a `BSLightingShaderProperty` or `BSEffectShaderProperty` has shader flag 1 bit 29, `External_Emittance` (`0x20000000`) |

Bit 5's editor-marker test is commented out here and done by the visitor in §6 instead,
because a marker inside a switch branch does not count.

### 3.4 Afterwards

```cpp
hasRootCollision = !isRootBSTree && (
    (isRootBSFade && root's collision object derives from bhkCollisionObject) ||
    (isRootBSLeaf && root's collision object derives from bhkCollisionObject) ||
    hasMultiBound);

if (isSingleChain(root))      flags[7] = true;
if (MarkerBranchVisitor(root).marker) flags[5] = true;

if (num_collisions > 0 || num_phantom_collisions > 0) {
    if (!isSkeleton && num_collisions > 0 && (!hasRootCollision || num_collisions > 1))
        flags[3] = true;
    flags[1] = true;
}
```

The source marks `hasRootCollision` *"wrong. may be complex but only in 6 models, need
further investigation"*, and two earlier attempts at bit 3 are commented out above it.
Treat bit 3 as the least certain of the set.

## 4. What the caller adds

`FBXWrangler.cpp` L5831–5838, after calculating:

- if there are skinned animations, **force bit 0** — the file has animation even though
  its controllers live in a Havok behaviour file rather than in the NIF;
- build the `BSXFlags` block named `BSX` and append it to the root's extra data;
- when exporting a rig, append a `SkeletonID` `NiIntegerExtraData` of `207579012`.

## 5. Bit 7: single collision or single chain

`SingleChunkFlagVerifier` walks the graph from the root counting:

- `n_collisions` — `bhkCollisionObject`-derived
- `n_phantoms` — `bhkSPCollisionObject`-derived
- `n_constraints` — `bhkConstraint`-derived, but only counting **distinct entity pairs**,
  so two constraints joining the same two bodies count once

and then:

```cpp
singlechain = (n_collisions - n_constraints == 1);
if (singlechain)                                  verified = true;
if (n_phantoms > 0 && (singlechain || n_collisions == 0)) verified = true;
if (hasBranches)
    verified = (n_collisions == 0 && n_phantoms == 0)
        ? verified || branchesResult
        : verified && branchesResult;
```

`n_collisions - n_constraints == 1` is the kinematic-chain test: a chain of *n* bodies
joined by *n−1* constraints leaves one.

A `NiSwitchNode` makes `hasBranches` true; each of its children is verified separately
and `branchesResult` is the AND of them, because only one branch is displayed at a time.

> **The two constructors differ.** The recursive one, used for switch children, ends
> with `if (n_phantoms == 0 && n_collisions == 0) verified = true;`. The top-level one
> does not — so a file with no collisions at all does **not** get bit 7.

## 6. Bit 5: editor markers outside branches

`MarkerBranchVisitor` walks the graph looking for an `NiObjectNET` whose name contains
`EditorMarker`, and sets the flag only when it is **not inside a branch**:

- `NiSwitchNode` — only its **first** child is walked outside a branch, the rest inside.
  The comment explains why: the first branch is the active one by default, so that is
  what the editor sees.
- `BSOrderedNode` — all children are inside a branch.

## 7. What se-cmd does

`Nif/NifBsxFlags.cs` implements §3 to §6. It is used in two ways:

- the FBX importer sets `BSX` from it rather than carrying the source value, since every
  bit is a fact about the graph it has just built;
- a test compares the calculated value against the stored one across the vanilla corpus,
  which is how the implementation is held to the real thing rather than to a reading of
  the source.

Deviations, and why:

- **Bits 8 and 10 to 13 are never set**, as in ck-cmd. Nothing in vanilla sets them and
  no rule for them is known.
- **`hasCollisionList` is not computed.** ck-cmd computes it and never uses it.
- **Bit 0's skinned-animation override (§4) is not applied**, because se-cmd does not
  write Havok behaviour files; there are no skinned animations for it to know about.

## 8. Measured against the game

Running the calculation over every mesh Skyrim SE ships and comparing it with the value
the file already stores: **22,007 of 22,047 agree**. That is the useful check on this
implementation, because those files were written by the exporter that defined the rules,
so a disagreement is either a bug here or a malformed file — and telling those apart is
what `NifScan` was for in the first place.

The 40 that disagree fall into four groups, and none of them is a fifth rule waiting to
be found.

| Files | Bit | Which way | What it is |
| --- | --- | --- | --- |
| 17 | 7 | calculated, not stored | One collision, no constraints, so §5's chain test says single. Mostly `meshes/shadertests/*`, plus legacy assets such as `clutter/table02.nif` and `architecture/markarth/markarthtemphouse.nif` |
| 5 | 3 | stored, not calculated | The root carries the collision, so `hasRootCollision` suppresses bit 3 |
| 4 | 1, 2, 6, 7 | stored, not calculated | The file claims collision it does not contain |
| 14 | 0, 5, 6, 9 | mixed | Mostly Creation Club, plus two `marker*.nif` |

The second group is the one to read carefully: those five are exactly the case §3.4's
source comment calls *"wrong. may be complex but only in 6 models, need further
investigation"*, and they turn up at almost the count it names. Reproducing ck-cmd's
uncertainty at ck-cmd's own scale is evidence the port is faithful, so the rule is left
as written rather than bent to fit five files.

The third group cannot be anything but the files. `creationclub/.../arpitwalltall02.nif`
stores `0x82` — Havok, single chain — with no collision block anywhere in it, and
`character assets/hair/hairshorthumanfold.nif` stores ragdoll and dynamic bodies with
the same. No calculation from the graph can produce those, because the graph does not
say them.

The first group is the least settled. Seventeen files with a single collision and no
constraint are a single collision by any reading of §5, and they do not set bit 7. They
are concentrated in test and legacy content, which suggests files that predate the rule
rather than a rule this misreads — but that is inference, not evidence.

Three of the remainder have not been run down: `hairlonghumanm.nif` (two collisions and
two constraints, where the distinct-pair counting of §5 decides bit 7),
`arcandleplate01.nif` and `arcandleplate02.nif` (bit 6, so a quality type read), and
`markerentrance.nif` / `markerexit.nif` (bit 5, on files whose nodes are not named
`EditorMarker`). Any of those could be this implementation rather than the file.
