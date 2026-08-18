# Constraint round trip, as ck-cmd implements it

Extracted from ck-cmd at `/home/ecanepa/Dev/ck-cmd`:

- `src/core/FBXWrangler.cpp` — the NIF → FBX direction (`FbxConstraintBuilder`,
  L2054–2312).
- `src/core/HKXWrangler.cpp` — the FBX → Havok direction (`build_constraint`,
  L2811–2910; `isConstraintFbxNode`, L146).

The two are a matched pair: FBXWrangler writes constraints out as nodes with string
properties, and HKXWrangler reads those same nodes back. Neither writes NIF constraint
blocks on the way in — HKXWrangler's target is a Havok `hkpConstraintInstance` for a
`.hkx` ragdoll — so §4 below is what se-cmd has to supply for itself.

---

## 1. How a constraint is represented in FBX

An empty node, parented under a rigid-body node, carrying string properties.

### 1.1 Name and parentage

`FbxConstraintBuilder::visitConstraint` (L2255) is constructed per rigid body as
`FbxConstraintBuilder(rb_node, bodies, obj, constraint, scale)` (L2370), so:

- `holder` — the node of the body **owning** the constraint, i.e. the body whose
  `Constraints` array lists it.
- For each of the constraint's entities that is *not* the owner (L2262), the other
  body's node is looked up and passed as `parent`.

Each `visit` overload then does (L2088, L2170, L2219):

```cpp
FbxNode* constraint_node = FbxNode::Create(scene,
    (string(parent->GetName()) + "_con_" + string(child->GetName()) + "_attach_point").c_str());
parent->AddChild(setMatTransform(matB, constraint_node));
```

with `parent` = the **other** body's node and `child` = the **owning** body's node.

> **The name reads other-first.** The node is
> `<otherBody>_con_<owningBody>_attach_point`, and it is a child of `<otherBody>`. The
> first half therefore repeats the parent's name; the second half is the only new
> information in it.

In NIF terms the owner is `Entity A` — in every corpus file the constraint appears in
its own `Entity A`'s `Constraints` array — so the name is
`<EntityB>_con_<EntityA>_attach_point`, parented under Entity B's node.

### 1.2 Placement

The node's local transform is the constraint's **B frame**, built from the descriptor's
B-side axes as matrix **columns**, with the pivot as the translation column and scaled
by `bhkScaleFactor` (L2074–2086):

```cpp
Matrix44 matB(
    twistB.x, planeB.x, motorB.x, (pivotB * bhkScaleFactor).x,
    twistB.y, planeB.y, motorB.y, (pivotB * bhkScaleFactor).y,
    twistB.z, planeB.z, motorB.z, (pivotB * bhkScaleFactor).z,
    0,        0,        0,        1);
```

The A frame is built identically from the A-side axes and then **discarded** — the
commented-out `FbxConstraintParent` block (L2090–2104) was what used to carry it.
HKXWrangler recomputes it from the scene hierarchy instead (§3.2).

### 1.3 Properties

All written as `FbxStringDT`, i.e. FBX type `KString`, via `set_property`. Numbers are
stored as their decimal text.

| Constraint | `constraint_type` | Other properties |
| --- | --- | --- |
| Ragdoll (L2106–2113) | `"Ragdoll"` | `coneMaxAngle`, `planeMinAngle`, `planeMaxAngle`, `twistMinAngle`, `twistMaxAngle`, `maxFriction` |
| Hinge (L2185) | `"Hinge"` | — |
| LimitedHinge (L2242) | `"LimitedHinge"` | `maxAngle`, `minAngle`, `maxFriction` |
| Malleable (L2119) | delegates to the wrapped type | as that type |
| Prismatic (L2116), BallAndSocket (L2246), StiffSpring (L2250) | — | — |

The last three `visit` overloads `return parent;` without creating a node at all, so
those constraints are **silently dropped on export**. Both constraints in se-cmd's test
corpus are of exactly those kinds.

### 1.4 An animation stack is forced into existence

`visitConstraint` (L2270–2277) creates a `"Take 001"` stack with a `"Default"` layer if
the scene has none, commented `//Constraints need an animation stack?`. Nothing else in
the constraint path uses it.

---

## 2. How a constraint node is recognised

`isConstraintFbxNode` (L146):

```cpp
return node_name.find("_con_") != string::npos;
```

Substring, anywhere in the name — not a suffix test, and `_attach_point` is not
checked. `build_body` (L2919–2935) collects such children while walking a body's
children looking for its shape.

---

## 3. How it is read back

`build_constraint(FbxNode* body)` (L2811), where `body` is the constraint node.

### 3.1 Resolving the two entities

```cpp
name = name.substr(0, name.length() - sizeof("_attach_point") + 1);
int pos = name.find("_con_");
if (pos == string::npos) return NULL;
entity_a_name = name.substr(0, pos);
entity_b_name = name.substr(pos + 5, name.length());
entity_a_fbx = body->GetScene()->FindNodeByName(entity_b_name.c_str());
if (entity_a_fbx == NULL) return NULL;
entity_a = bodies[entity_a_fbx];
entity_b = bodies[body->GetParent()];
```

Note the crossover, which is deliberate and matches §1.1:

- **Havok entity A** = the node named by the name's **second** half = the owning body.
  The local variables `entity_a_name`/`entity_b_name` are named the other way round
  from what they end up being used for; `entity_a_name` is never read.
- **Havok entity B** = the constraint node's FBX **parent** = the other body.

A constraint whose second-half name matches no node in the scene is skipped.

### 3.2 The two frames

```cpp
hkTransform transform_b = getTransform(body, false, true);
```

`getTransform(node, absolute=false, inverse=true)` (L490) takes the node's **local**
transform and **inverts the rotation quaternion**, keeping the translation as-is. This
undoes the column-major packing of §1.2.

The A frame is recomputed from the hierarchy (L2836–2842):

```cpp
trans_parent = body->GetParent()->EvaluateGlobalTransform(0);   // entity B
trans_child  = entity_a_fbx->EvaluateGlobalTransform(0);        // entity A
trans_a_calc = body->EvaluateLocalTransform(0) * trans_parent.Inverse() * trans_child;
```

— the B frame carried through entity B's space into entity A's.

> **Bug (L2839–2841).** The copy out of `trans_a_calc` reads `[0][3]` for all three
> translation components:
> ```cpp
> transform_a(0,3) = trans_a_calc[0][3];
> transform_a(1,3) = trans_a_calc[0][3];   // should be [1][3]
> transform_a(2,3) = trans_a_calc[0][3];   // should be [2][3]
> ```
> so entity A's pivot comes out as `(x, x, x)`. Not reproduced by se-cmd.

`trans_parent_to_child` (L2838) is computed and never used.

### 3.3 Type and limits

```cpp
string type = get_property<FbxString>(body, "constraint_type", FbxString(""));
```

Exactly one type is distinguished (L2871):

- `"Ragdoll"` → `hkpRagdollConstraintData`, reading `coneMaxAngle`, `planeMinAngle`,
  `planeMaxAngle`, `twistMinAngle`, `twistMaxAngle`, `maxFriction`.
- **everything else**, including `"Hinge"`, the empty string, and any type FBXWrangler
  never writes → `hkpLimitedHingeConstraintData`, reading `maxAngle`, `minAngle`,
  `maxFriction`.

Every property is read as a string and parsed with `atof`, defaulting to the Havok
constructor's own value when absent. `atof` returns `0.0` on unparseable text rather
than failing.

The result is `new hkpConstraintInstance(entity_a, entity_b, data)`, named after
entity A (L2902–2904), added to the physics system, and recorded in
`constraints_table` as `{entity_a_fbx, body->GetParent(), instance}`.

### 3.4 Ragdoll assembly

`build_constraint` is called per body from the ragdoll path. When
`constraints.size() == rigidBodies.size() - 1` (L2608) the set is treated as a tree and
handed to `hkaRagdollUtils::reorderAndAlignForRagdoll` / `constructSkeletonForRagdoll`;
otherwise `"Wrong number of constraints in the model."` is logged (L2800).

---

## 4. What se-cmd does instead

se-cmd's import target is a NIF, not a `.hkx`, so §3 maps onto NIF blocks rather than
Havok instances. Where the two disagree, the reasons are recorded here.

### 4.1 Discovery and naming

As §2 and §3.1: a node whose name contains `_con_`, with `_attach_point` trimmed, the
first half naming the parent and the second half the owning body. se-cmd's exporter is
corrected to write the halves in this order so that its output is readable by
HKXWrangler and vice versa.

### 4.2 Block type

`constraint_type` selects the block directly rather than collapsing everything
non-Ragdoll to a limited hinge:

| `constraint_type` | Block | Descriptor field |
| --- | --- | --- |
| `Ragdoll` | `bhkRagdollConstraint` | `Constraint` |
| `Hinge` | `bhkHingeConstraint` | `Constraint` |
| `LimitedHinge` | `bhkLimitedHingeConstraint` | `Constraint` |
| `BallAndSocket` | `bhkBallAndSocketConstraint` | `Constraint` |
| `StiffSpring` | `bhkStiffSpringConstraint` | `Constraint` |
| `Prismatic` | `bhkPrismaticConstraint` | `Constraint` |
| `Malleable` | `bhkMalleableConstraint` | `Constraint` |
| `BallSocketConstraintChain` | `bhkBallSocketConstraintChain` | — |
| `BreakableConstraint` | `bhkBreakableConstraint` | `Constraint Data` |

Collapsing to a limited hinge would turn the corpus's stiff spring into a hinge that
was never authored, which is worse than not importing it.

### 4.3 Descriptor values

se-cmd's exporter writes the **whole** descriptor as `hkc_`-prefixed string properties,
field by field off the nif.xml definition (see `Fbx/FbxConstraintWriter.cs`), because
the six names in §1.3 do not describe a stiff spring, a ball and socket, or a chain.
Import prefers those when present and falls back to §3.3's names when not, so a scene
from FBXWrangler still imports as a ragdoll or a limited hinge with its limits intact.

### 4.4 Frames

The B frame is taken from the node's transform as in §3.2, and the pivot divided back
by `bhkScaleFactor`. The A frame is **not** recomputed from the hierarchy: se-cmd
records `Pivot A` and the A-side axes in the `hkc_` properties, so the recomputation —
and the bug in it — is unnecessary when the scene came from se-cmd. For a scene that
did not, §3.2's derivation is used, without the translation bug.
