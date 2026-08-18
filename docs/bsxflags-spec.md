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
