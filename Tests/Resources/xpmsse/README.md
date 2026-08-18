# XPMSSE skeleton

`skeleton_cow.nif` is the cow skeleton from
[XP32 Maximum Skeleton Special Extended](https://github.com/acepleiades/XP32-Maximum-Skeleton-Special-Extended),
taken from `Creature Meshes/meshes/actors/cow/character assets/skeleton.nif` at
commit `8d36383`.

XPMSSE is **MIT**, © 2018 Groovman, with the mod itself credited to Groovtama,
Skulltyrant and XP32. MIT permits redistribution, so the file is included here
under that licence; copyright remains with its authors.

## Why this one

Ragdolls live in skeletons, and nothing else in the corpus has one. Constraint
export was reaching the two types se-cmd's own fixtures happen to contain — a
stiff spring and a ball-and-socket chain, neither of which has an orientation —
so the frame packing was only ever exercised from the import side, against
scenes built by hand.

This file is Skyrim SE, 147 blocks, and contains:

| | |
| --- | --- |
| 24 | `bhkRigidBody` + `bhkBlendCollisionObject` + `bhkCapsuleShape` |
| 11 | `bhkRagdollConstraint`, with orthonormal twist/plane/motor frames on both sides |
| 12 | `bhkLimitedHingeConstraint`, with axle/perpendicular frames and angle limits |
| 47 | `NiNode` bones, named as a skeleton (`Pelvis`, `LFemur`, `LTibia`, …) |

Those are the two constraint types ck-cmd implements and the spec's table
describes (`docs/hkx-constraint-spec.md` §1.3, §3.6), so this is also the first
fixture that can check se-cmd against what ck-cmd would do.

It was chosen for being the smallest skeleton carrying both types: 17 KB against
30–110 KB for the humanoid ones, and unlike the smaller `atronachfrost` skeleton
it has hinges as well as ragdolls.

## Chosen over an extracted asset

Every vanilla `skeleton.nif` is a Bethesda asset, and no NIF library ships one:
nifly's corpus has no constraints beyond the two above, PyFFI's 38 test files
have none at all, and the Blender addon's are Morrowind and Fallout 3 meshes.
XPMSSE is a modder-made replacer its authors released under MIT, which is what
makes it redistributable here.
