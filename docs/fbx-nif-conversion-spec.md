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
| `_cylinder` | A `bhkCylinderShape`. This port only; ck-cmd converts none |
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
| `bhkCylinderShape` | `_cylinder` | Tessellated cylinder between the two points. **Not in ck-cmd**, which has no case for one at all, so a body whose shape is a cylinder leaves with no geometry and the collision object is lost with it — see §10 |
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

`Target` and `Next Controller` are not carried, since both are rebuilt from the chain.

**References are followed, two levels.** The flat codec moves fields and drops links,
which is right for almost everything — a link is a block index and means nothing once
exported. One case needs more: a `BSProceduralLightningController` holds nine
interpolators under names of its own (`Interpolator 2: Mutation`), and when no sequence
drives them nothing else in the file would bring them back. So a link that points at an
interpolator is followed to the interpolator's own fields, and then to its data block's —
where the keys are, and the codec sizes an array from the count field it read a moment
before, so they travel whole. This is the shape §5.2.2 already uses for node → bound →
volume.

##### Nothing about this is particular to particle systems

A `BSLagBoneController` makes a bone trail behind the one above it by a fixed amount.
That is a property of the skeleton rather than of a timeline, it holds no interpolator
either, and it was lost on every skeleton that had one. So the carrier belongs to **any
node**, and the particle system is one caller of it.

Two things are excluded rather than carried, and both are exclusions the animation route
would otherwise duplicate:

- **A controller holding an interpolator this layer can carry**, in either slot — the
  second matters, since an emitter's on/off track lives there (§5A.6). That is animation
  and goes the other way. *Holding* one is not the test: a `NiTransformController` whose
  interpolator is a `NiPathInterpolator` or a `NiLookAtInterpolator` drives nothing a
  curve on an FBX property can express, and such a controller used to fall between the
  two routes and be carried by neither. A **blend** interpolator counts as the animation
  layer's own — it holds no keys and is the slot a manager mixes into — which is how a
  sequenced pair is recognised in an LE file, where a sequence names its controllers by
  type string rather than by reference.
- **A controller a sequence names.** Holding no field called `Interpolator` is not enough:
  a `BSProceduralLightningController` holds nine interpolators, none of them called that,
  and every one is driven from a sequence. The animation route rebuilds it from the
  controlled blocks, and carrying it as structure too gave every lightning node two. The
  animation reader already computes that set — a controller a `NiControlledBlock` names is
  one half of a pair — so both routes ask the same question.

The sequence machinery only looks like a structural controller. A `NiControllerManager`
holds no interpolator of its own, but it *is* the animation layer, rebuilt from the
sequences; carrying it here put a manager back into files whose animation had been turned
off.

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

#### 5.2.5 What a node can be, and what it is called

**Any `NiAVObject`, not only a `NiNode`.** A `NiCamera` is a node in the scene graph and
not a `NiNode` in the schema — it inherits `NiAVObject` directly and has no `Children` of
its own. Reading the carried class against `NiNode` rejected it, so every camera came
back as a plain node with its frustum, viewport and LOD adjust gone.

One class is refused explicitly: geometry is built on the mesh path, from a mesh, and a
node naming a shape class would arrive there with no vertices to be one from. The one
exception is a node marked as an empty shape, below.

**A block with no name is still a block.** The game's cameras have none. FBX has no
anonymous object, so the export falls back to the class name — and three things have to
agree on that, or the node is lost in a different way each time:

| Who | Why |
| --- | --- |
| The export, naming the FBX object | Something has to be there |
| The animation reader, keying tracks | A track names a node; a nameless one has no name to be named by |
| The model lookup an FBX track binds through | Or the controller has nothing to hang on |

The name itself travels separately, as `nif_name`, holding the empty string. It is
written **only** when the name is empty, and read back as *present or absent* rather than
by its value — every property getter answers absent and empty alike with its fallback,
which is the one distinction this needs.

**A shape with no vertices travels as a node.** nif.xml says why: a
`BSProceduralLightningController` is "paired with dummy TriShapes", empty shapes the
engine generates lightning into at runtime, and the game's staff bolts, rune projectiles
and shock explosions are built from them. Exporting nothing lost the shape, its shader
and its alpha property — half the blocks in `explosionshock01.nif`.

FBX has no mesh with no vertices worth writing; a DCC tool given one shows an object that
cannot be selected. So it is a plain node carrying everything a shape carries except the
mesh, marked `nif_empty_shape`. The mark is explicit rather than inferred from "a geometry
class with no mesh attached", because that is also what an author typing a class name onto
an ordinary node produces, and those are not the same thing. On the way back a
`NiTriBasedGeom` still gets its data block, empty — the class keeps its vertices there and
a null `Data` is not a shape the engine will load.

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

#### 5.2.4 Which geometry class

The two geometry families differ in where the vertices live, not merely in name. A
`BSTriShape` packs them inline; everything under `NiTriBasedGeom` keeps them in a data
block beside it. `BSLODTriShape` is in that **second** family despite its name.

SE is `BSTriShape` country, and the exceptions are informative. Of the 21,587 vanilla SE
meshes that hold geometry:

| | Files |
| --- | --- |
| with `BSTriShape` | 17,900 |
| with `BSDynamicTriShape` | 3,687 |
| with `BSLODTriShape` | 34 |
| with `NiTriShape` | **130** |
| …of those, also holding `BSTriShape` | **130 — every one** |

No SE file is wholly `NiTriShape`. It appears only *beside* `BSTriShape`, in plants,
landscape and `_byoh` meshes, and the pattern inside a file gives it away:
`floramushroom06.nif` holds `FloraMushroom06:5` as a `BSTriShape` and
`FloraMushroom06_1:5` — the alternate variant — as a `NiTriShape`. These are shapes the
SSE optimiser did not convert, not a class SE prefers for anything.

##### `BSLODTriShape`, and how one is authored

A `BSLODTriShape` does not hold three meshes. It holds **one triangle list, partitioned**:
the first `LOD0 Size` triangles are the nearest level, the next `LOD1 Size` the one
after, and the engine draws a prefix of the list according to distance. The counts are
the whole of the mechanism.

So authoring one is not a matter of picking the class. It means ordering a shape's
triangles by level and saying how many belong to each — and vanilla shows the working in
its own names: `florapotatoplant01.nif` holds a shape called
`L1_FloraPotatoPlant02:1 - L2_FloraPotatoPlant02:1` whose 60 triangles are 10 for LOD1
and 50 for LOD2. Two LOD groups, authored separately and merged in order into one shape.
All 34 vanilla uses are plants.

FBX has nowhere to say any of that, so the counts travel as `lod_size_0..2` on the
geometry. Carrying the class without them gives a shape whose every level is zero
triangles long: present, correct in every other respect, and invisible. A shape with no
counts to carry keeps zeros, which is what a mesh with no LOD groups should have.

##### Marking the triangles, not just counting them

Three counts reproduce a shape that was already a LOD shape. They give an author nothing
to *edit*: the levels are invisible in a DCC tool, and there is no way to move a face
between them or to build one from a mesh that never was a LOD shape. That is the same
gap the skin partitions had, and it is closed the same way — by putting the thing an
artist has to touch somewhere the artist can touch it.

The levels ride as **a material per polygon**, named `LOD0`, `LOD1`, `LOD2`. §5.3.4
covers how the import tells these apart from the two real kinds of material. It is the
one per-face channel every DCC tool exposes and lets an artist reassign, and it is the
mechanism ck-cmd already uses to carry collision materials (§4.8) — there
`eByPolygon`/`eIndexToDirect` on a `bhkPackedNiTriStripsShape`, here the identical layer
element on the shape itself. ck-cmd has no LOD support of any kind; this is a place FBX
can say more than ck-cmd asked it to.

Export connects the three materials to the mesh's node **after** the shape's own
material, so the shape's material keeps the slot it had, and writes one index per
triangle. Import resolves them **by name rather than by index**: an artist who adds or
removes a material slot would otherwise shift every triangle a level, silently.

Two things follow from the counts being *runs* over one list:

- **Import reorders the triangles.** A marking is a level per face in whatever order the
  faces happen to be in; counts only mean anything if the triangles are grouped by level
  and the groups are in order. `FbxLodSizes.GroupByLevel` does the grouping, before the
  geometry is written.
- **An n-gon fans into several triangles**, so a polygon index is not a triangle index.
  `MeshGeometry.TrianglePolygons` records where each triangle came from, and the marking
  is read back through it.

A face left on the shape's own material belongs to no level. It keeps its place, at the
end, rather than being dropped: deleting geometry an artist can see is the worse of the
two failures.

This is the written-twice pair the rest of the port uses (§5C.1), with the halves the
other way round from usual. `lod_size_0..2` is exact and reproduces an untouched file;
a marking that disagrees with it is an artist having said something, so the **marking
wins**. A mesh with no `LOD*` material is not marked at all and keeps whatever the
counts said — which is what stops every ordinary shape, whose material element is
`AllSame` at index 0, from being read as one long level zero.

`LevelPerTriangle` skips an empty level rather than entering it, in both directions: a
0/10/50 shape starts at level one, and a shape whose counts run out before its triangles
do leaves the stragglers in the last level that has any, rather than in a level it has
none of.

So the class is **carried**, because reproducing a file means reproducing it, and 130
vanilla meshes would otherwise be changed. But it is only carried: a scene with nothing
to carry gets the class its edition wants, so geometry authored in a DCC tool becomes
`BSTriShape` for SE and never inherits the anomaly. A `BSTriShape` carried into an LE
build is refused, since the class does not exist there.

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

An effect shader was the first of these and is much the commonest, but it is not the only
one. A `BSWaterShaderProperty` shares no more with a lighting shader than an effect shader
does, and fell through the same gap ck-cmd's does — the export returned no material at
all, so the shape came back with no shader. So the rule is **the one class the common
material form covers**, not a list of the ones it does not: anything that is not a
`BSLightingShaderProperty` rides flat, under its own name, and comes back as the class
that was written. The class is checked against the schema, so a name this build does not
know falls back to an effect shader rather than inserting whatever the property said.

A lighting shader records no `shader_block` and is what everything else rebuilds as, which
keeps a scene authored in a DCC tool working unchanged.

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

#### 5.3.4 Materials, on the way back

An FBX material is the busiest carrier in the format: three unrelated things arrive as
one, and the import tells them apart by where the mesh sits in the scene and what the
material is called. This section is the whole of it.

##### The invariant that shapes everything else

**A NIF shape has exactly one material.** A shader property hangs off the shape, and
geometry that needs two materials is two shapes — `multi_material_cube.nif` is a cube
split into `Cube_Material0`, `Cube_Material1` and `Cube_Material2` for precisely that
reason. FBX has the opposite convention: one mesh, a list of materials, and a
`LayerElementMaterial` saying which polygon uses which.

So on the way out, every mesh gets a material element that says `AllSame` /
`IndexToDirect` / `Materials = [0]`, and the material is connected **to the mesh's node,
not to the geometry** (§4.3). The import reads it back the same way — `ChildrenOf(holder)`
filtered to `Material` — which is why a material that a DCC tool attaches to the geometry
object instead of the node is not found.

##### The three things a material can be

| Where the mesh is | What the material means | Read by |
| --- | --- | --- |
| Under an ordinary node | The shader: colours, textures, alpha | `BuildMaterial` |
| Under a collision node (`_box`, `_convex`, `_mopp`, …) | The Havok material, named after the `SkyrimHavokMaterial` enum, with the collision layer as a `CollisionLayer` property | `ReadCollisionMaterial` |
| Anywhere, named `LOD0`/`LOD1`/`LOD2` | Not a material at all — a level marker (§5.2.4) | `ReadLodMarking` |

The first two never meet: a collision mesh is recognised by its name suffix long before
either is read. The third overlaps both, and is passed over by name: `BuildMaterial` and
`ReadCollisionMaterial` both skip anything `FbxLodSizes.IsLevelMaterial` recognises.

That skip is not decoration. A shape takes the **first** material on its node, the
export connects the shape's own before the markers, and a mesh marked up in a DCC tool
has whatever order that tool wrote. Without it, a shape's shader comes back named `LOD0`
with none of the right textures, and a collision shape warns that `LOD1` is not a Skyrim
Havok material. A shape whose only materials are markers gets **no shader**, which is the
right answer rather than a mistaken one.

##### A render material becomes a shader property

`BuildMaterial` produces a `BSLightingShaderProperty` unless the material carries
`shader_block`, in which case it is an effect shader and §5.3.2 takes over. The lighting
mapping is §4.3 read backwards:

| NIF field | From | Note |
| --- | --- | --- |
| `Glossiness` | `ShininessExponent` | |
| `Specular Strength` | `SpecularFactor × 999` | NIF stores 0–999, FBX 0–1 |
| `Specular Color` | `SpecularColor` | defaults to white |
| `Emissive Color` | `EmissiveColor` | |
| `Emissive Multiple` | `EmissiveFactor` | defaults to 1 |
| `Alpha` | `1 − TransparencyFactor` | |
| `Environment Map Scale` | `environment_map_scale` | user property |
| `UV Offset`, `UV Scale` | the first texture's `ModelUVTranslation` / `ModelUVScaling` | see below |
| `Texture Set` | the textures connected to the material | see below |

**The UV transform is per texture in FBX and per shader in NIF.** The export writes the
same pair onto every slot; the import takes the first texture that names them. The scale
defaults to **one, not zero** — a zero there does not fail loudly, it multiplies every
texture coordinate in the mesh to nothing.

**Textures are found by the property they are connected to**, not by order:
`DiffuseColor` is slot 0, `NormalMap` slot 1, and `slot<N>` is slot `N−1`. Skyrim always
writes nine slots whether or not they are used. The path comes from `RelativeFilename`,
falling back to `FileName`, and is normalised to a `textures\…\*.dds` form.

**Sharing is by identity, not by content.** A texture set or alpha property that several
shapes shared in the source carries an id property, and shapes naming the same id get
one block back. Rebuilding by equality would be as wrong as never merging: Bethesda's
files hold identical blocks side by side on purpose, and merging those changes the file.

##### A collision material becomes a Havok enum

The name *is* the value: `SKY_HAV_MAT_WOOD` is looked up in nif.xml's own
`SkyrimHavokMaterial` enum rather than in a table copied out of ck-cmd, which is what
keeps the two spellings from drifting. A name that is not in the enum leaves the shape
with its default material and **warns** — the one case where an unrecognised material is
reported rather than passed over, because a collision material that silently became
`STONE` is a footstep sound nobody will trace back here.

The collision layer rides as a `CollisionLayer` string property on the same material and
defaults to `SKYL_STATIC`. Materials are shared per distinct (material, layer) pair, so a
shape tree with one material comes back with one.

Note which field is written: `HavokMaterial` declares **three** fields all called
`Material`, one per game, separated only by their version condition. The name alone finds
Oblivion's. The Skyrim one is selected by its type.

##### What a per-polygon element means

`ReadPolygonMaterials` returns one index per polygon when the element says `ByPolygon`,
and null otherwise — an `AllSame` element is *not* a marking, which is what stops every
ordinary shape in the corpus from reading as one long level zero.

Two things stand between a polygon index and a triangle:

- **An n-gon fans into several triangles**, and a degenerate one is dropped during the
  fan, so the polygon list and the triangle list are different lengths.
  `MeshGeometry.TrianglePolygons` records where each triangle came from and is the only
  correct way back.
- **The indices are into the node's material list**, in connection order. They are
  resolved to names before they are used, never used as levels directly.

##### The limit

**A render mesh with several materials keeps the first.** Splitting it into one shape per
material is what a NIF needs and what the export already produces going the other way,
but the import does not do it: a cube authored in a DCC tool with three materials comes
back as one shape shaded entirely with the first. Authoring multi-material geometry means
splitting the mesh in the DCC tool, which is also what the SSE optimiser expects. Listed
in §7.3.

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

#### 5.7.0 Skeletons: the collision object's class

`bhkBlendCollisionObject` is what makes a file a skeleton. The BSXFlags calculation
defines `isSkeleton` as *having one* (`bsxflags-spec.md` §3.1), and three bits follow
from that, so rebuilding it as a plain `bhkCollisionObject` does not lose a class — it
changes what the engine thinks the file is. On `skeleton_cow.nif` the flags went from
`0xC6` to `0x8A`: no ragdoll, no dynamic bodies, with every bone, constraint and shape
still in place.

The class travels as `nif_collision_type` on the body's node, with the blend form's
`Heir Gain` and `Vel Gain` beside it — a zero gain is a bone that does not follow.

The body's class travels too, as `nif_body_type`, because the two go together. ck-cmd
pairs a blend collision object with a plain `bhkRigidBody` and an ordinary one with
`bhkRigidBodyT`, which applies its own transform; a skeleton's bodies are placed by their
bones and do not want that.

##### The body transform is a world transform

A NIF rigid body's transform places it in the **world**, even when the body hangs off a
bone several levels down — which is exactly what a skeleton does. Two things follow, and
both were wrong here.

The placement lives at `Rigid Body Info\Translation`, not beside it. Read from the wrong
path it silently yielded nothing, so **every collision body in every mesh exported at the
origin**: all 24 of a skeleton's bodies piled on one point. No fixture caught it, because
the ones with collision sit at the origin anyway, and no NIF round trip caught it either
— nothing came back, and nothing was expected to.

And it is written relative to the parent, so the node's *global* transform is the body's
NIF placement, which is what ck-cmd does for a blend collision object (§4.9) and what a
DCC tool draws. The import reads the global transform back, as ck-cmd does for a rig
(L4884). `FbxGlobalTransform` walks the chain, since there is no SDK here to ask.

A node has exactly one parent: everything else that joins nodes — a constraint's
attachment point, a mesh, a deformer — hangs the other way round, as a child.

##### A skeleton authored in a DCC tool

A rig built in Blender or Max and brought in for the first time has no classes to carry,
and the fallback decides. Getting it wrong is quiet: the file has every bone, every
constraint and every shape, and the engine does not treat it as a ragdoll.

ck-cmd handles this with an `export_rig` flag (L4868–4886) which switches the collision
object to the blend form with both gains at 1, the body to a plain `bhkRigidBody`, and
the transform from local to global. It is a flag the caller has to know to set.

This port takes the flag — `FbxToNifOptions.SkeletonRig` — but leaves it null by
default and works the answer out instead: **a scene holding a ragdoll constraint is a
skeleton**, because nothing else has one. Constraints already travel (§4.10), so the
evidence is there to read, and a rig exported from this tool and stripped of every
carried property still comes back with its 24 blend objects and its flags at `0xC6`.

A blend object that arrives without gains is given 1 for both, as ck-cmd does: a zero
gain is a bone that does not follow.

#### 5.7.0A Two shapes that used to lose the whole body

A leaf shape that tessellates to nothing takes its body and its collision object with
it: there is no node for the import to find, and `no collision shape found beneath it`
is all that is said. Two cases did that.

**A cylinder is not a capsule.** ck-cmd's `recursive_convert` has no `bhkCylinderShape`
case, so this port had none either, and the shape fell through to "not a shape this
converts yet". It converts them now — and the distinction that matters is where the ends
go. A capsule's two points are the *centres* of its hemispherical caps, so they sit a
radius inside the shape; a cylinder's are on the flat end discs themselves. Fitting one
as the other shortens every cylinder by two radii, which is exactly the kind of error
nothing reports.

Havok stores both points as four-component vectors, and the fourth component is not
padding: it holds the radius again, and Havok reads it.

**A flat hull is not broken input.** `byohwrdoorload01` draws its load door as four
coplanar points — a `bhkConvexVerticesShape` with no volume. The incremental hull starts
from a tetrahedron, and a flat point set has none, so it yielded an empty mesh. It is
tessellated as the polygon it is instead: the points ordered around their common plane
and fanned, wound **both ways**, since the import refits from those triangles and a
one-sided fan would give the fit a surface rather than a solid.

Fewer than three points is genuinely nothing and stays nothing.

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

### 5A.6 What a track can hold besides keys

Three things that are animation and have no keys. Each was dropped by a filter asking
"does this track have keys", and each took the blocks that carried it down with it.

**A constant scalar.** A `NiFloatInterpolator` or `NiBoolInterpolator` with no data block
holds one value for the whole sequence — an effect's "loop" sequence hides a mesh
outright, `Value` 0 and nothing else — and the "begin" sequence beside it keys the same
property. Two sequences saying different things is exactly what animation is. It travels
as a typed property on the **stack**, since a stack is the only per-take place FBX has;
the model's resting value is one per model where this is one per take.

**A constant transform.** A `NiTransformInterpolator` with no data block still carries a
`Transform`, and that is the pose the node takes for the whole sequence. It travels the
same way, as eight numbers on the stack (`constxf_<node>`), written as the quaternion the
file holds rather than as a matrix so a file nobody edited comes back with the numbers it
went out with.

**An interpolator that holds nothing at all.** Not keys, not a pose — a file can store
one with no data block and its `Value` left at the sentinel, and the controlled block
naming it is in the file too. The game's lightning effects are full of them: a "loop"
sequence that drives nothing, spelled out rather than left out. That is a *third* state,
distinct from a constant, which says one thing, and from an absent property, which is not
there at all. It travels as `noval_<node>|<property>` on the stack, whose value is the
interpolator class — a track that holds nothing is entirely described by what kind of
nothing it is.

**The sentinel that means neither.** nif.xml calls the field "Pose value if lacking
NiFloatData" and gives it a default that means *none*: `#INV_FLT#` for a float, `2` for a
bool — a bool being 0 or 1 and never 2, and a transform component being `float.MinValue`.
An interpolator with neither data nor a pose holds nothing, and reading the sentinel as a
constant turns it into an animation that sets every float it drives to 3.4e38. So the
sentinel has to be recognised the moment constants start being kept; the two changes are
one change.

#### A controller can drive two things

`NiPSysEmitterCtlr` holds two interpolators, and nif.xml names them: `['BirthRate',
'EmitterActive']`, "for `Interpolator` and `Visibility Interpolator` respectively". Those
are the same spellings a `NiControlledBlock` puts in its `Interpolator ID`, so a
controller read through a sequence and one read attached name the same track.

Reading only the first lost every emitter's on/off track — and because a birth rate is
usually one constant number, the track then looked empty and the whole controller went
with it. Both halves are read, and on the way back **one controller is built per class
and id**, with each track in the slot its `Interpolator ID` names. Where nothing named a
slot — a scene authored in a DCC tool — a boolean track on a controller that has a
visibility slot is what that slot is for.

Properties share a controller only when the file said which controller they belong to. A
shader carries several `BSEffectShaderPropertyFloatController`s, one fading and another
scrolling, and nothing in a track tells them apart; grouping those by class alone
rebuilds one where there were nine.

#### Which controller is which

nif.xml states per class what `NiInterpController::GetCtlrID()` returns, and it is not
decoration:

| Class | Id field |
| --- | --- |
| `NiPSysModifierCtlr` and below | `Modifier Name` |
| `NiFloatExtraDataController` | `Extra Data Name` |

A particle system carries several modifier controllers of the same class, one per
modifier. With no id to tell them apart the import keys them all to one slot and rebuilds
one controller where there were four, which halved the bool interpolators of every effect
mesh with more than one emitter.

### 5A.7 Known limits

| Limit | Consequence |
| --- | --- |
| A track binds by node name | Duplicate names cannot be told apart, in either format. A block with **no** name is bound by its class name instead, and the name itself travels as `nif_name` (§5.2.5) |
| One layer per stack | Layered animation is not represented |

---

## 5B. Block order

A NIF does not store its blocks in any order. A Havok block has to come **before**
whatever references it — the reverse of every other block — and a constraint after the
bodies it joins. Every mesh the game ships obeys this, and a file built by walking a
scene and appending blocks as it goes does not: before this was fixed, every rebuilt
file with collision was wrong, 24 places on a skeleton.

The rule is NifSkope's `spSanitizeBlockOrder` (`src/spells/sanitize.cpp`), which is the
only written-down statement of it there is. Walk from the roots; for each block emit
first the referenced blocks that belong before it, then the block, then the rest. A
constraint's *entities* come first of all, since they are pointers and the reference walk
never reaches them.

**One correction to that rule.** NifSkope tests `bhkRefObject && !bhkConstraint`, and a
`bhkBallSocketConstraintChain` inherits `bhkSerializable` rather than `bhkConstraint` —
so the rule as written puts a chain *before* the body referencing it, and
`TestNifFile_DeepGraph_SE.nif` has it after. The principle underneath is that a thing
which joins bodies comes after them, and a chain joins bodies whatever it inherits from.
It is recognised by carrying a `bhkConstraintChainCInfo`, rather than by naming every
class that might be one.

Reordering renumbers, so every link in the file is remapped in one pass, the footer's
roots included, and the new order must be a permutation of the old — a dropped block
would leave links pointing at whatever moved into its place. It runs before the header
is written, since the header records the block types in order.

The check is worth having in both directions: the fixture test asserts the *shipped*
files satisfy it, which is what caught the constraint chain. A rule only tested against
its own output tests nothing.

### 5B.1 The rules that are not rules

NifSkope's sanitise page holds three spells. Only the first is an invariant, and the
other two were checked against the shipped files before being believed.

| Spell | Shipped files | Applied here |
| --- | --- | --- |
| `spSanitizeBlockOrder` | 24 of 24 satisfy it | Yes — §5B |
| `spReorderLinks` (sort children, shapes last) | **2 of 24 violate it** | **No** |
| `spSanitizeLinkArrays` (drop null links) | 3 of 24 have null entries | Incidentally — none are produced |

`spReorderLinks` sorts a node's children so that shapes come last for `bsver >= 83` and
first below it. It is cleanup NifSkope offers, not a rule the format has — and applying
it would be actively wrong for a `BSOrderedNode`, a class whose entire purpose is to
draw its children in a fixed order. `TestNifFile_OrderedNode_SE.nif` is one of the two
that "violate" it, which is the giveaway. A test asserts its children come back in the
order they went in, so that nobody later mistakes the spell for a requirement.

### 5B.2 What ck-cmd does about order

Nothing. `RebuildVisitor` (L2631) gathers blocks into a `set<NiObject*>` and copies them
out in the set's order, which is **pointer address order** — arbitrary, and different
between runs of the same conversion. Whatever ordering the format wants, ck-cmd's output
satisfies it only by chance. Recorded in §9.

---

## 5C. Everything Bethesda's classes carry, and where it goes

FBX has a node, a mesh, a material, a skin and an animation curve. A Skyrim NIF has a
hundred classes that differ in what the engine *does* with them, and almost none of that
has an FBX equivalent. What follows is the whole of what is carried across, in one
place, because the alternative is finding it a class at a time.

Everything here is a user-defined property (`U` or `A+U` flags) unless the row says
otherwise. That matters: standard properties are the ones a DCC tool understands and
edits, and user-defined ones survive a round trip through it without being interpreted.

### 5C.1 The rule these follow

A property carries something **only when the scene cannot say it any other way**, and
never when the thing can be derived from what is already there. So the mesh carries no
copy of its own vertices, the tangent space is regenerated rather than carried (§5.3.1),
and `BSXFlags` is recalculated rather than carried (`bsxflags-spec.md`).

Where a value is both meaningful to an artist *and* needed exactly, it is written twice:
once in FBX's own vocabulary so the scene looks right, and once as a property so the
rebuild is exact. The collision material (§4.8), the effect shader (§5.3.2) and the
multi-bound volume (§5.2.2) all do this. **The property is always the authoritative
half**; the visible half is never read back.

### 5C.2 Nodes

| Property | On | Carries |
| --- | --- | --- |
| `nif_name` | node | A name FBX cannot carry as the object's own, which in practice means an empty one. Read as present-or-absent, not by value (§5.2.5) |
| `nif_empty_shape` | node | This node is a shape with no vertices — a dummy TriShape a controller generates geometry into (§5.2.5) |
| `particle_controllers`, `npc_<i>_*` | node | Controllers that animate nothing: a particle system's update switch, a skeleton's lag bone. Excludes anything a sequence names (§4.9A) |
| `const_<node>\|<property>` | animation stack | A track holding one value for the whole take, typed so a boolean constant and a float one stay different (§5A.6) |
| `constxf_<node>` | animation stack | A transform held for the whole take, as the quaternion the file holds (§5A.6) |
| `nif_block_type` | node, geometry | The block class — `BSOrderedNode`, `BSLeafAnimNode`, `BSValueNode`, `BSMultiBoundNode`, `BSLODTriShape`, `BSDynamicTriShape`. Refused unless the schema knows it and it inherits the expected base |
| `extra_data`, `xd_<i>_type`, `xd_<i>_name`, `xd_<i>_*` | node | Every `NiExtraData` block, field by field. `BSXFlags` is excluded: it is recalculated (§5.4.1) |
| `multi_bound_type`, `mb_*`, `multi_bound_culling` | node | A `BSMultiBoundNode`'s culling volume, three blocks deep, plus a `<name>_multibound` mesh drawn for the artist (§5.2.2) |
| `lod_size_0..2` | geometry | A `BSLODTriShape`'s per-level triangle counts. Without them the shape draws nothing at any distance (§5.2.4) |
| `LOD0/1/2` materials | geometry | The same levels as a material per polygon, which is the half an artist can edit. Resolved by name; a marking that disagrees with the counts wins (§5.2.4) |
| `dynamic_vertex_w` | geometry | The fourth component of a `BSDynamicTriShape`'s vertex buffer, one per vertex. The other three are the mesh (§5.3.3) |

### 5C.3 Collision

| Property | On | Carries |
| --- | --- | --- |
| `nif_collision_type` | `_rb` node | `bhkCollisionObject` or `bhkBlendCollisionObject`. The blend form is what makes a file a skeleton (§5.7.0) |
| `nif_body_type` | `_rb` node | `bhkRigidBody` or `bhkRigidBodyT`; the latter applies its own transform |
| `nif_collision_flags` | `_rb` node | `bhkCOFlags` — `SET_LOCAL`, `SYNC_ON_UPDATE`, how the body tracks its node |
| `nif_blend_heir_gain`, `nif_blend_vel_gain` | `_rb` node | A blend object's gains. Zero is a bone that does not follow |
| `nif_rb_mass`, `nif_rb_layer` | `_rb` node | The body's mass and collision layer. The layer decides the motion profile; a static's mass is dropped (§5.7.2) |
| *the FBX material's **name*** | collision mesh | The Havok material, as its `SkyrimHavokMaterial` enum name, with the layer on a `CollisionLayer` property. ck-cmd's scheme, kept (§4.8) |
| *the node's **name suffix*** | shape node | Which primitive: `_box`, `_sphere`, `_capsule`, `_convex`, `_mesh`, and the containers `_list`, `_convex_list`, `_transform`, `_mopp`. Size is refitted from the geometry, not carried |

### 5C.4 Shaders and materials

| Property | On | Carries |
| --- | --- | --- |
| `shader_block`, `es_*` | material | A `BSEffectShaderProperty`, field by field. Only an effect shader records the class; a lighting shader is what everything else rebuilds as (§5.3.2) |
| `shader_type` | material | The lighting shader's `Shader Type`, by enum name |
| `nif_texture_set`, `nif_alpha_property` | material | Which *source blocks* these came from, so blocks shared there are shared again rather than copied per shape (§5.2.1) |
| `source_blend_mode`, `destination_blend_mode`, `alpha_test_mode`, and the flags beside them | material | A `NiAlphaProperty`, spread across properties FBX has no slot for |
| `environment_map_scale` | material | What its name says |

### 5C.5 Skins

| Property | On | Carries |
| --- | --- | --- |
| `body_slots`, `body_slot_<i>`, `body_slot_<i>_flags` | skin deformer | Which body part each partition is, **by enum name**. This is what a dismember instance has and a plain one does not (§5.2.3) |
| `nif_skin_instance` | skin deformer | The instance class. Carried beside the slots rather than derived from them, because an empty slot list means two different things |

### 5C.6 Particles

A particle system has no FBX equivalent at all — no emitter, no modifier stack — and
ck-cmd's FBX path carries none of it. See `nif-particle-spec.md`; the properties are
`particle_system`, `particle_data`, `nps_*`, `npsd_*` on the system's node,
`particle_modifier` and `particle_modifier_name` on each modifier node,
`particle_collider` on each collider, `<name>_ref` for a link naming another node, and
`particle_controllers` with `npc_<i>_*` for the controllers that animate nothing
(§4.9A).

### 5C.7 Animation

Animation is the one thing FBX genuinely models, so most of it travels as curves. What
does not fit is:

| Property | On | Carries |
| --- | --- | --- |
| *the animated property's **name*** | node | `ControllerType\|ControllerId\|InterpolatorId\|PropertyType`, joined by bars with trailing empties dropped (§4.7.4) |
| `interp_<node>\|<property>` | animation stack | The interpolator's exact class — a `NiBoolTimelineInterpolator` is not a `NiBoolInterpolator` |
| `const_<node>\|<property>` | animation stack | A track holding one value for the whole take, typed so a boolean constant is not mistaken for a float one (§4.7.4) |
| `flip_controllers`, `flip_<i>_type`, `flip_<i>_sources`, `flip_<i>_source_<n>` | node | A flipbook controller and the textures it cycles |
| `constraint_type`, `constraint_wrapper`, `hkc_*` | attachment point | A Havok constraint; see `hkx-constraint-spec.md` |

### 5C.8 What is deliberately not carried

`Target` and `Next Controller` on any controller, `Name` where it is written separately,
and every link that is the upward half of a two-way pair. All of them are rebuilt from
the structure, and a carried one would point into a block list that no longer has that
block.

---

## 5D. Authoring a NIF from scratch, in a DCC tool

Every section before this one describes a round trip: a NIF goes out, an FBX comes back,
and the carriers exist so that nothing is lost in between. This section reads the same
machinery the other way round. It is the list of what an author can **type into a DCC
tool** to get a specific NIF block or field out, with no NIF to have started from.

Two mechanisms carry everything: a **node's name**, and **custom properties** (FBX calls
them user-defined properties; Blender calls them custom properties, 3ds Max calls them
user-defined properties in the object properties dialog). Both are plain text and both
survive every exporter worth using. Where a value is a number it is still written as
text; the codec parses it in the invariant culture, so `1.5` and never `1,5`.

Nothing here is required. A scene with none of it still converts — that is the point of
the defaults — and each property below only changes what it names.

### 5D.1 What kind of block a node becomes

| Property | On | Value | Effect |
| --- | --- | --- | --- |
| `nif_block_type` | node | Any `NiAVObject` class | The node becomes that class instead of `NiNode`: `BSFadeNode`, `BSOrderedNode`, `BSLeafAnimNode`, `BSValueNode`, `BSMultiBoundNode`, `NiBillboardNode`, `NiCamera`. Refused if the schema does not know it or it is not a `NiAVObject` (§5.2.5) |
| `nif_block_type` | mesh geometry | A geometry class | `BSTriShape`, `BSDynamicTriShape`, `BSLODTriShape`, `NiTriShape`. Refused for LE builds when the class does not exist there (§5.2.4) |
| `dynamic_vertex_w` | mesh geometry | A number | The fourth component of a `BSDynamicTriShape`'s vertices, which the static vertex buffer has nowhere to hold |
| `nif_own_<field>` | node, geometry | Text | Any field the named class adds to its base. `nif_own_alpha_sort_bound` on a `BSOrderedNode`, `nif_own_value` on a `BSValueNode`. Field names are lowercased with spaces as underscores |
| `nif_name` | node | Usually empty | The block's `Name`, when it cannot be the FBX object's own — in practice only for a block that has **no** name, which the game's cameras have. Present-but-empty is the whole signal (§5.2.5) |
| `nif_empty_shape` | node | Any non-empty value | This node is a shape with no vertices — a dummy TriShape a `BSProceduralLightningController` generates geometry into. It carries a shader and an alpha property like any shape and has no mesh (§5.2.5) |

Without `nif_block_type` a node becomes a `NiNode` and a mesh becomes the geometry class
the target edition uses — `BSTriShape` for SE, `NiTriShape` for LE. That is the right
answer for almost everything, which is why the property is not required.

### 5D.2 Collision

Collision is authored as **child nodes whose names carry a suffix**, not as properties.
The shape is read from the mesh under the node: a box node is fitted to a box, a sphere
node to a sphere, a convex node to a hull. Nest the suffixes to nest the shapes.

| Name suffix | Becomes |
| --- | --- |
| `_rb` | `bhkCollisionObject` + `bhkRigidBody` — the body everything below hangs from |
| `_sp` | `bhkSPCollisionObject` — a simple shape phantom |
| `_box`, `_sphere`, `_capsule`, `_cylinder`, `_convex`, `_mesh` | The leaf shape, fitted to the mesh under it |
| `_transform`, `_list`, `_convex_list`, `_mopp` | A container; recurse into its children |
| `_geometry` | The mesh attribute of a collision node, not a shape of its own |
| `_con_`, `_attach_point` | A constraint and its attach point |
| `_multibound` | A mesh drawn for a culling volume; skipped on import (§5.2.2) |

| Property | On | Effect |
| --- | --- | --- |
| `nif_collision_type` | collision node | The collision-object class, when it is not the default for the suffix |
| `nif_body_type` | collision node | `bhkRigidBody` or `bhkRigidBodyT` — whether the body carries its own transform |
| `nif_collision_flags` | collision node | The collision object's flags word |
| `nif_blend_heir_gain`, `nif_blend_vel_gain` | collision node | A `bhkBlendCollisionObject`'s two gains, which is what makes a file a skeleton |
| `nif_rb_mass` | collision node | The body's mass. **Ignored for a static body**, which is always massless (§5.7.2) |
| `nif_rb_layer` | collision node | The collision filter's layer, default `SKYL_STATIC` |
| `hkc_<field>` | constraint node | Any field of the constraint, flat |
| `constraint_type`, `constraint_wrapper` | constraint node | Which constraint class, and its wrapper |

**The Havok material is an FBX material**, named after nif.xml's `SkyrimHavokMaterial`
enum — `SKY_HAV_MAT_WOOD`, `SKY_HAV_MAT_STONE` — with a `CollisionLayer` string property
on the same material. A name the enum does not know leaves the default and warns; it is
the one unrecognised material that is reported rather than passed over (§5.3.4).

**The inertia tensor is calculated, never authored.** So is the shape's radius, and the
mass of a static. Typing them would be typing something the import overwrites (§5.7.2).

### 5D.3 Skinning and body parts

| Property | On | Effect |
| --- | --- | --- |
| `nif_skin_instance` | mesh geometry | `NiSkinInstance` or `BSDismemberSkinInstance`. Absent, the class follows whether body slots were given (§5.2.3) |
| `body_slots` | mesh geometry | How many dismember partitions follow |
| `body_slot_<i>` | mesh geometry | The body part of partition *i*, from nif.xml's `BSDismemberBodyPartType` |
| `body_slot_<i>_flags` | mesh geometry | That partition's editor flags |

Bones are ordinary FBX skin clusters and need no properties: the deformer names the
bones, and the partitions are computed from the weights. Body slots are the one thing a
skin cluster cannot say, which is why they are here — a character's skin needs them and
a door hinge does not.

### 5D.4 Level of detail

| Mechanism | Effect |
| --- | --- |
| `nif_block_type` = `BSLODTriShape` | The shape becomes a LOD shape |
| Materials named `LOD0`, `LOD1`, `LOD2`, assigned **per face** | Which level each triangle belongs to. Reassigning a face moves it between levels; the import groups the triangles and derives the counts (§5.2.4) |
| `lod_size_0`, `lod_size_1`, `lod_size_2` | The counts outright, for a shape that came from a NIF. A per-face marking that disagrees wins |

Faces left on the shape's own material belong to no level and keep their place at the
end. This is the only per-face channel in FBX, so it is the only way to author this.

### 5D.5 Shaders, textures and alpha

A shader is an FBX material connected to the mesh's **node**, not to the geometry. A
`BSLightingShaderProperty` is built from the material's own Phong values (§5.3.4), so
authoring one is authoring an ordinary material. The properties below are for what a
material cannot say.

| Property | On | Effect |
| --- | --- | --- |
| `shader_block` | material | The shader class, when it is not a lighting shader: `BSEffectShaderProperty`, `BSWaterShaderProperty`, `BSSkyShaderProperty`. Checked against the schema |
| `es_<field>` | material | Any field of that shader, flat |
| `environment_map_scale` | material | The lighting shader's environment map scale |
| `nif_texture_set`, `nif_alpha_property` | material | Identity marks that let several shapes **share** one texture set or alpha property. Two materials naming the same id get one block; leave them out and each gets its own (§5.2.1) |
| `color_blending_enable`, `source_blend_mode`, `destination_blend_mode`, `alpha_test_enable`, `alpha_test_mode`, `alpha_test_threshold`, `no_sorter_flag` | material | A `NiAlphaProperty`, decomposed. Blend and test modes are GL names — `SRC_ALPHA`, `ONE_MINUS_SRC_ALPHA`, `GREATER` — rather than numbers (§4.4). The block is only built when the flags amount to something |
| `flip_controllers`, `flip_<i>_*` | node | `NiFlipController`s — a texture flipbook. They hang off a shader property in the file, but the node is what an importer can put them back on |

Textures are ordinary FBX textures connected to the material by property: `DiffuseColor`
is slot 0, `NormalMap` slot 1, and `slot<N>` reaches the rest. Paths are normalised to
`textures\…\*.dds`.

### 5D.6 Extra data, bounds and multi-bounds

| Property | On | Effect |
| --- | --- | --- |
| `extra_data` | node | How many extra data blocks follow |
| `xd_<i>_type` | node | The class of block *i* — `NiStringExtraData`, `BSBehaviorGraphExtraData`, `NiIntegerExtraData`, `BSBound` |
| `xd_<i>_<field>` | node | That block's fields, flat |
| `multi_bound_type` | node | A `BSMultiBoundNode`'s volume class: `BSMultiBoundOBB` or `BSMultiBoundSphere` |
| `mb_<field>` | node | The volume's own fields |
| `multi_bound_culling` | node | The culling mode |

`BSXFlags` is **never** authored. It is derived from the block graph on every import, and
a hand-written one would be discarded — see `bsxflags-spec.md`.

### 5D.7 Particles

A particle system is a node, not a mesh: its vertices are a runtime buffer the file only
sizes. Modifiers are child nodes.

| Property | On | Effect |
| --- | --- | --- |
| `particle_system` | node | The system class; makes this node a `NiParticleSystem` |
| `nps_<field>` | node | The system's own fields |
| `particle_data`, `npsd_<field>` | node | The `NiPSysData` class and its fields |
| `particle_modifier`, `particle_modifier_name` | child node | One modifier's class and name. Sibling order is stack order |
| `particle_collider` | child node | One collider of a chain |
| `<field>_ref` | node | A modifier field that points at another node, carried by that node's **name** rather than by a block index — `emitter_object_ref`, `gravity_object_ref` |
| `particle_controllers`, `npc_<i>_type`, `npc_<i>_<field>` | node | Controllers that animate nothing (§4.9A) |

### 5D.8 Controllers that animate nothing

The last row above is not particular to particle systems, and is the general answer to
"how do I attach a controller that has no keys":

| Property | On | Effect |
| --- | --- | --- |
| `particle_controllers` | any node | How many controllers follow |
| `npc_<i>_type` | any node | The controller class — `NiPSysUpdateCtlr`, `BSLagBoneController` |
| `npc_<i>_<field>` | any node | Its fields. `Target` and `Next Controller` are rebuilt from the chain and are not written |

A controller that *does* have keys is animation and is authored as animation, below.

### 5D.9 Animation

Animation is authored as FBX animation: a stack per sequence, a layer in it, curves on
the nodes. A node's translation, rotation and scale need nothing extra. What needs
naming is **which NIF controller a non-transform track drives**, and that rides in the
animated property's *name*:

```
<controller class>|<controller id>|<interpolator id>|<property class>
```

with trailing empty parts dropped, so a controller with no ids at all is just its class
name. `Visibility` is the one shorthand: it stands for a `NiVisController` with no ids.

| Part | What it is | Example |
| --- | --- | --- |
| controller class | The NIF block to build | `NiPSysEmitterCtlr` |
| controller id | Which of several of that class, from nif.xml's `GetCtlrID()` — a modifier's name, an extra datum's name | `NiPSysCylinderEmitter:0` |
| interpolator id | Which of the controller's slots, from nif.xml's own field name with the spaces gone | `BirthRate`, `EmitterActive`, `Mutation` |
| property class | The property the controller hangs on | `BSEffectShaderProperty` |

So `NiPSysEmitterCtlr|NiPSysCylinderEmitter:0|BirthRate` on a particle system's node,
keyed, is an emitter whose birth rate changes over time; the same name ending
`|EmitterActive`, as a boolean, is the emitter switching on and off. The two go in
different slots of **one** controller (§5A.6).

Three things go on the **animation stack** rather than on a node, because they are one
per take and a node property is one per node:

| Property | On | Effect |
| --- | --- | --- |
| `const_<node>\|<property name>` | stack | A track holding one value for the whole take. Typed `bool`, `Number` or `ColorRGB` — a boolean constant and a float one are the same number and different animations |
| `constxf_<node>` | stack | A transform held for the whole take: eight numbers, `tx ty tz qw qx qy qz scale` |
| `noval_<node>\|<property name>` | stack | A track whose interpolator holds nothing — no keys, no pose. The value is the interpolator class |
| `interp_<node>\|<property name>` | stack | The interpolator class the track should rebuild as, when it is not the default for the value kind — `NiBoolTimelineInterpolator` rather than `NiBoolInterpolator` |

### 5D.10 What is calculated, and cannot be authored

Typing any of these is typing something the import overwrites. They are listed so that a
scene that lacks them is not thought to be missing anything.

| What | Where it comes from |
| --- | --- |
| `BSXFlags` | The block graph (`bsxflags-spec.md`) |
| Tangents and bitangents | The UVs and triangles, NifSkope's algorithm (§5.3.1) |
| Bounding spheres | The vertices |
| A rigid body's inertia tensor | The shape and the mass (§5.7.2) |
| A static body's mass | Always zero, whatever `nif_rb_mass` says |
| A collision shape's radius | The fitted shape |
| Skin partitions | The bone weights |
| `Vertex Desc`, and every offset in it | Which attributes the mesh has (§5.3.0) |
| Block order | The reference rules (§5B) |
| A convex hull's plane equations | The hull's own faces (§5.7.1) |

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
| The second and later materials of a multi-material render mesh | A NIF shape has one material, and the import keeps the first rather than splitting the mesh into one shape per material. Authoring means splitting it in the DCC tool | §5.3.4 |

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
| L2631 | `RebuildVisitor` collects blocks into a `set<NiObject*>` and writes them in the set's order, which is pointer-address order — so block order is arbitrary and varies between runs. Havok blocks have an ordering requirement (§5B) that this cannot meet except by chance |
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
| Cylinders and flat hulls | Converted (§5.7.0A). ck-cmd has no `bhkCylinderShape` case, and a hull with no volume has no tetrahedron to seed from, so in both cases the shape tessellated to nothing and the body and collision object above it were lost with it. |
| Effect shaders | Carried in both directions (§4.3.1, §5.3.2). The reference drops them: its export casts to `BSLightingShaderProperty` and takes the null, its import only builds lighting shaders. |
| Tangent space | Generated from NifSkope's `spTangentSpace` (§5.3.1) rather than obtained from the FBX SDK, which also removes the need for ck-cmd's tangent/binormal swap. |
| Inertia tensors | Computed directly (§5.7.2) rather than obtained from Havok, and held to the numbers ck-cmd's generated files carry. |
| Node kinds | The NIF block type of every node, and of the root, travels in a `nif_block_type` property. FBX has one kind of node; NIF has a dozen that differ in what the engine does with them. The root matters most: `BSXFlags` asks twice whether it is exactly `NiNode` (see `bsxflags-spec.md` §3.2, §3.4), so flattening it changes what the file claims about itself. |
| `bhkCOFlags` | Carried in a `nif_collision_flags` property rather than derived from the layer. ck-cmd derives them because an FBX authored in a DCC tool has none to carry; carrying wins where the data exists, and the derivation remains the fallback. |
| `BSXFlags` | Recalculated on import rather than carried, as ck-cmd does. See `bsxflags-spec.md`. |
