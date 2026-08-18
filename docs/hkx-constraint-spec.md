# Constraint round trip, as ck-cmd implements it

Extracted from ck-cmd at `/home/ecanepa/Dev/ck-cmd`:

- `src/core/FBXWrangler.cpp` — the NIF → FBX direction (`FbxConstraintBuilder`,
  L2054–2312).
- `src/core/HKXWrangler.cpp` — the FBX → Havok direction (`build_constraint`,
  L2811–2910; `isConstraintFbxNode`, L146).
- `src/core/FBXWrangler.cpp` again — the Havok → NIF direction (`buildConstraints`,
  L5184; `convert_from_hk`, L4985–5088).

The three directions form a loop, and only one of them is direct:

```
NIF ──FbxConstraintBuilder──▶ FBX ──build_constraint──▶ hkpConstraintInstance
 ▲                                                              │
 └──────────────── convert_from_hk ◀───────────────── buildConstraints
```

FBXWrangler writes constraints out as nodes with string properties; HKXWrangler reads
those nodes back as Havok constraint instances; and `FBXWrangler::buildConstraints`
(L5184) then converts *those* into NIF blocks with `convert_from_hk` (L4985). So NIF
constraints are rebuilt on import, but only ever by going through Havok — which is why
so little survives the trip (§3.6).

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

`setMatTransform` (L176) then reads that matrix back with `GetTrans()` and
`GetRotation().AsQuaternion()`. Both halves are worth following:

- `GetTrans()` in this niflib fork returns `(rows[0][3], rows[1][3], rows[2][3])` — the
  last **column** — which is where the pivot was put. It survives. (The neighbouring
  `GetTranslation()` reads the last row and would return zero here.)
- `GetRotation()` returns the top-left 3×3 exactly as stored, so the axes are still its
  columns. `AsQuaternion` then reads it as niflib matrices are meant to be read, with
  the axes as **rows**, and so yields the quaternion of the frame's **transpose**.

> **The node's rotation is the inverse of the joint frame.** A rigger looking at an
> attachment point in a DCC tool sees an orientation whose X axis is
> `(twistB.x, planeB.x, motorB.x)` rather than `twistB`. This is not compensated on
> export; it is compensated on import, by the `.Inverse()` in §3.2, and the two cancel.

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
corpus are of exactly those kinds. `bhkBreakableConstraint` and
`bhkBallSocketConstraintChain` are not handled at all: `visitConstraint`'s type ladder
ends in `throw new runtime_error("Unimplemented constraint type!")` (L2299).

Ragdoll returns its node while Hinge and LimitedHinge `return NULL`. The value is
assigned to `constraint_position` and never read, so nothing depends on it.

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

### 3.4 Back to a NIF

`FBXWrangler::buildConstraints` (L5184) walks `constraints_table` and, for each entry,
converts the Havok instance into a NIF block and appends it to **entity A**:

```cpp
bhkRigidBodyRef entity_a = conversion_Map[get<0>(entry)];   // the second-half name
bhkRigidBodyRef entity_b = conversion_Map[get<1>(entry)];   // the node's parent
auto& to_add = entity_a->GetConstraints();
to_add.push_back(convert_from_hk(get<2>(entry), entity_a, entity_b));
entity_a->SetConstraints(to_add);
```

So the body that *owns* a constraint in the rebuilt NIF is entity A, which is the body
named by the attachment point's second half — the same body that owned it before
export. The loop closes.

`convert_from_hk` returns `NULL` for anything that is not a ragdoll, hinge or limited
hinge, and the result is pushed onto the array **without a null check**, so an
unconvertible constraint becomes a null reference in `entity_a`'s `Constraints`.

### 3.5 NIF and Havok field equivalence

`convert_from_hk` (L4985–5088) is the only place the two vocabularies are laid side by
side. Every NIF axis is a **column** of the corresponding Havok constraint frame, and
every pivot is column 3 scaled by `bhkScaleFactorInverse` = `0.01428`:

| NIF field | Havok source |
| --- | --- |
| `twistA` / `axleA` | `m_atoms.m_transforms.m_transformA.getColumn(0)` |
| `planeA` / `perp2AxleInA1` | `m_transformA.getColumn(1)` |
| `motorA` / `perp2AxleInA2` | `m_transformA.getColumn(2)` |
| `pivotA` | `m_transformA.getColumn(3)` × `0.01428` |
| `twistB` / `axleB` | `m_transformB.getColumn(0)` |
| `planeB` / `perp2AxleInB1` | `m_transformB.getColumn(1)` |
| `motorB` / `perp2AxleInB2` | `m_transformB.getColumn(2)` |
| `pivotB` | `m_transformB.getColumn(3)` × `0.01428` |
| `coneMaxAngle` | `m_atoms.m_coneLimit.m_maxAngle` |
| `planeMinAngle` / `planeMaxAngle` | `m_atoms.m_planesLimit.m_minAngle` / `m_maxAngle` |
| `twistMinAngle` / `twistMaxAngle` | `m_atoms.m_twistLimit.m_minAngle` / `m_maxAngle` |
| `minAngle` / `maxAngle` (limited hinge) | `m_atoms.m_angLimit.m_minAngle` / `m_maxAngle` |
| `maxFriction` | `m_atoms.m_angFriction.m_maxFrictionTorque` |

The NIF→FBX direction scales the pivot the other way, by `bhkScaleFactor`, read from
the NIF header (`nif.GetBhkScaleFactor()`, L1483) rather than hard-coded. se-cmd uses
the constant `69.99125`, which is `1 / 0.01428` to five figures.

`hkpHingeConstraintData` has no limits, so a plain hinge carries frames only.

### 3.6 What actually survives a full loop

Composing §1.3, §3.3 and §3.4:

| NIF block in | FBX node | Havok data | NIF block out |
| --- | --- | --- | --- |
| `bhkRagdollConstraint` | yes | `hkpRagdollConstraintData` | `bhkRagdollConstraint` |
| `bhkLimitedHingeConstraint` | yes | `hkpLimitedHingeConstraintData` | `bhkLimitedHingeConstraint` |
| `bhkHingeConstraint` | yes | **`hkpLimitedHingeConstraintData`** | **`bhkLimitedHingeConstraint`** |
| `bhkMalleableConstraint` | as wrapped type | as wrapped type | wrapper **lost** |
| `bhkPrismaticConstraint` | **no node** | — | — |
| `bhkBallAndSocketConstraint` | **no node** | — | — |
| `bhkStiffSpringConstraint` | **no node** | — | — |
| `bhkBreakableConstraint` | **throws** | — | — |
| `bhkBallSocketConstraintChain` | **throws** | — | — |

A hinge becomes a limited hinge because `build_constraint` only distinguishes
`"Ragdoll"`; the `minAngle`/`maxAngle`/`maxFriction` it then reads were never written
for a hinge, so the limits come from the Havok defaults. Four of the nine types cannot
make the trip at all, and two of those four are what the test corpus contains.

### 3.7 Ragdoll assembly

`build_constraint` is called per body from the ragdoll path. When
`constraints.size() == rigidBodies.size() - 1` (L2608) the set is treated as a tree and
handed to `hkaRagdollUtils::reorderAndAlignForRagdoll` / `constructSkeletonForRagdoll`;
otherwise `"Wrong number of constraints in the model."` is logged (L2800).

---

## 4. What se-cmd does instead

se-cmd goes FBX → NIF directly, with no Havok in the middle, so §3.4's loop through
`hkpConstraintData` — and everything it costs in §3.6 — does not apply. Where the two
diverge, the reasons are recorded here.

### 4.1 Discovery and naming

As §2 and §3.1: a node whose name contains `_con_`, with `_attach_point` trimmed, the
first half naming the parent and the second half the owning body, which becomes
`Entity A` and receives the constraint in its `Constraints` array. That matches §3.4
exactly; the naming is the one part of ck-cmd's design that survives its own round trip
unchanged.

se-cmd's exporter was corrected to write the halves in this order, so its output is
readable by HKXWrangler and vice versa.

### 4.2 Block type

`constraint_type` selects the block directly rather than collapsing everything
non-Ragdoll to a limited hinge:

| `constraint_type` | Block |
| --- | --- |
| `Ragdoll` | `bhkRagdollConstraint` |
| `Hinge` | `bhkHingeConstraint` |
| `LimitedHinge` | `bhkLimitedHingeConstraint` |
| `BallAndSocket` | `bhkBallAndSocketConstraint` |
| `StiffSpring` | `bhkStiffSpringConstraint` |
| `Prismatic` | `bhkPrismaticConstraint` |
| `Malleable` | `bhkMalleableConstraint` |
| `BallSocketConstraintChain` | `bhkBallSocketConstraintChain` |

An unrecognised type is reported and dropped rather than guessed at. Collapsing to a
limited hinge would turn the corpus's stiff spring into a hinge that was never
authored, and §3.6 shows the same collapse silently demoting every plain hinge as well.

A `constraint_wrapper` property carries the wrapping block — `bhkBreakableConstraint`
or `bhkMalleableConstraint` — because `constraint_type` names the descriptor *inside*
the wrapper, which is what HKXWrangler expects to read. Without it the wrapper is lost,
which is what happens to malleable constraints in §3.6.

### 4.3 Descriptor values

se-cmd's exporter writes the **whole** descriptor as `hkc_`-prefixed string properties,
field by field off the nif.xml definition (`Fbx/FbxConstraintWriter.cs`), because the
six names in §1.3 do not describe a stiff spring, a ball and socket, or a chain. Import
prefers those when present and falls back to §3.3's names when not, so a scene from
FBXWrangler still imports as a ragdoll or a limited hinge with its limits intact.

### 4.4 Frames

The node's rotation follows ck-cmd's convention from §1.2 — the **transpose** of the
joint frame — so that a scene from either tool reads correctly in the other. se-cmd
transposes on the way out and again on the way in, exactly where HKXWrangler's
`.Inverse()` sits.

This is the one place se-cmd reproduces something it would not have chosen. The
alternative is an attachment point whose orientation is the joint frame as a rigger
would expect to see it, at the cost of every such node being inverted when ck-cmd reads
it. Interoperating won, and the `hkc_` properties mean se-cmd's own round trip does not
depend on the node's rotation at all.

The pivot is the node's translation divided back by `bhkScaleFactor`, as in §3.5.

The A frame is **not** recomputed from the hierarchy. se-cmd records `Pivot A` and the
A-side axes in the `hkc_` properties, so §3.2's derivation — and the bug in it — is
unnecessary for a scene se-cmd wrote. For a scene from ck-cmd the A frame is left at
zero rather than derived, since the derivation as written produces `(x, x, x)`.
