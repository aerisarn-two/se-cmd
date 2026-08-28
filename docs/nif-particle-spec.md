# Particle systems: what a NIF stores, and what a scene format can hold

Extracted from `nif.xml` 0.9.1.0 (vendored at `External/nifxml/nif.xml`) and from the
one particle system in the test corpus, `TestNifFile_Animated_LE.nif`. §6 assesses
FBX's object model against that; §7 surveys the alternatives, since FBX turns out to be
the weakest of them and is chosen only because it is the pipeline se-cmd speaks.

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

Most point at **named nodes elsewhere in the scene**. An emitter that has lost its
emitter object emits from the origin; a gravity modifier that has lost its gravity
object pulls towards the origin. Neither failure is visible in the file.

`Collider` is the odd one: it does not name a node but starts a chain of blocks. Each
`NiPSysCollider` carries bounce and spawn-on-collide settings, subclass fields — a
plane's width, height and axes, a sphere's radius — a `Next Collider` continuing the
chain, a `Parent` back to the manager, and two links of its own: a `Collider Object`
naming a node, and a `Spawn Modifier` naming a modifier of the same stack.

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
| **D.** A `Null` per modifier, each with its own properties | yes | yes | well: the stack is a subtree | exactly |

---

## 7. Other formats

FBX is not the only option, so it is worth knowing what the alternatives actually
model. Surveyed August 2026; sources at the end of this section.

### 7.1 X3D — the only format that models this natively

X3D's **Particle Systems component** (ISO/IEC 19775-1, clause 40) is a declarative
emitter-plus-forces model, which is the same shape as a NIF's.

`ParticleSystem` is a shape node — it has `appearance` and `geometry` like any other —
and adds `geometryType` (`"POINT"`, `"LINE"`, `"TRIANGLE"`, `"QUAD"`, `"SPRITE"`,
`"GEOMETRY"`), `maxParticles`, `particleLifetime`, `particleSize`,
`lifetimeVariation`, `enabled`, `createParticles`, and two ramps: `color` with
`colorKey`, and `texCoord` with `texCoordKey`. It holds **one** `emitter` and a
**list** of `physics` models. The complete inventory is small:

| Emitters (`X3DParticleEmitterNode`) | Distinguishing fields |
| --- | --- |
| `PointEmitter` | `position`, `direction` |
| `ConeEmitter` | `position`, `direction`, `angle` |
| `ExplosionEmitter` | `position` |
| `PolylineEmitter` | `coord`, `coordIndex`, `direction` |
| `SurfaceEmitter` | `surface` (a geometry node) |
| `VolumeEmitter` | `coord`, `coordIndex`, `direction`, `internal` |

All six share `speed`, `variation`, `mass`, `surfaceArea` and `on`.

| Physics models (`X3DParticlePhysicsModelNode`) | Fields |
| --- | --- |
| `ForcePhysicsModel` | `force` |
| `WindPhysicsModel` | `direction`, `speed`, `gustiness`, `turbulence` |
| `BoundedPhysicsModel` | `geometry` |

The correspondence with a NIF is real, and in places exact:

| NIF | X3D | Fidelity |
| --- | --- | --- |
| `BS Max Vertices` | `maxParticles` | exact |
| emitter `Speed`, `Speed Variation` | emitter `speed`, `variation` | exact |
| emitter `Life Span`, `Life Span Variation` | `particleLifetime`, `lifetimeVariation` | exact |
| `NiPSysBoxEmitter`, `NiPSysCylinderEmitter`, `NiPSysSphereEmitter` | `VolumeEmitter` (or `ConeEmitter` from `Declination`) | shape approximated |
| `NiPSysMeshEmitter` | `SurfaceEmitter` | close |
| `NiPSysGravityModifier` | `ForcePhysicsModel.force` = `Gravity Axis` × `Strength` | partial: `Decay`, `Turbulence`, `Force Type` have no home |
| `NiPSysDragModifier`, `NiPSysAirFieldModifier` | `WindPhysicsModel` | approximate |
| `NiPSysColliderManager` + planar/spherical collider | `BoundedPhysicsModel.geometry` | approximate: bounds, not collision response |
| emitter `Initial Radius` | `particleSize` | exact only while `BSPSysScaleModifier` is absent |
| `World Space` | implied by where the node sits | lost |
| `BSPSysSimpleColorModifier` | `color` + `colorKey` | close |
| `BSPSysSubTexModifier` | `texCoord` + `texCoordKey` | close — both are atlas animation |
| `NiPSysAgeDeathModifier`, `NiPSysPositionModifier`, `NiPSysBoundUpdateModifier` | implicit in the runtime | vanish, harmlessly |
| `NiPSysRotationModifier`, `BSPSysScaleModifier`, `BSPSysLODModifier`, `BSPSysInheritVelocityModifier` | nothing | lost |
| modifier `Order` | fixed by the runtime | lost |

X3D has three physics models against nif.xml's twenty-eight modifiers, so the mapping
is lossy in one direction and unrecoverable in the other: a `VolumeEmitter` cannot say
whether it was a cylinder or a box.

#### Who actually implements it

This is what decides whether X3D is a real option, and it splits cleanly: **runtimes
implement the Particle Systems component; authoring tools do not.**

Among 3D suites, X3D is native in **Blender**, **Modo**, **Rhino**, **ZBrush**,
**Okino PolyTrans**, **Clara.io** and **Cura**, and reaches **3ds Max** and **Maya**
only through third-party plugins (InstantExport, Bacon XjF). But that support is
*geometry interchange*: Blender's exporter documents coverage of the Rendering and
Geometry3D components, image and pixel textures, `TextureTransform`, the Lighting
component and viewpoints — and no particle systems. **No surveyed authoring tool round
trips a `ParticleSystem`.**

Runtimes are the other story. **X_ITE** implements the whole component — all six
emitters and all three physics models. **Castle Game Engine** has its own particle
system and lists following X3D's component as a planned feature rather than a
delivered one.

So X3D is a **presentation** target and specifically a *viewer* target: it is the only
surveyed format where a particle system arrives as a particle system, and the thing
that will show it is a browser runtime, not a DCC suite. That is a narrower claim than
"X3D supports particles", and it is the one that holds.

#### Writing it from C#

There is no C# X3D library. The Web3D Consortium ships official language bindings for
Java (X3DJSAIL) and Python (X3DPSAIL), both generated from the X3D Unified Object
Model; C, C++ and C# bindings are listed as work in progress and are not delivered.
Nothing on NuGet targets X3D, and the general commercial 3D libraries do not list it
among their formats.

This matters less than it sounds. X3D's `.x3d` encoding is **XML**, with a published
schema and DTD, and the subset needed here is one `ParticleSystem` element, one
emitter and a handful of physics models. Writing that needs `System.Xml.Linq` and
nothing else — no dependency, and no binding to wait for. Reading X3D would be a
different proposition, and is not something this project would need to do.

### 7.2 USD — the best transport, by being extensible rather than by knowing about particles

OpenUSD has no particle schema. `UsdGeomPointInstancer` is a per-frame set of instanced
prototypes with born/die `ids` — a baked simulation, in the same category as a point
cache, not an emitter.

What USD has instead is **schemas as a first-class extension mechanism**. A custom
typed (IsA) or applied API schema declares NIF's own model — `NiPSysCylinderEmitter`
with a typed `radius`, `height`, `speed` — into the schema registry, and since USD
21.08 a *codeless* schema needs no compiled C++ at all, only a plugin manifest. Any
USD runtime can then read those prims as typed, named, validated data, and they
compose through layers and variants like anything else.

That is strictly better than FBX custom properties for the same information: typed
rather than stringly, namespaced rather than prefixed, introspectable, and versioned.
Nothing will simulate it — but nothing simulates the FBX properties either.

### 7.3 glTF — extensible, but nothing exists

No ratified `KHR_` extension covers particle systems, and the registry contains no
particle, emitter or VFX extension at all; `ACME_particle_emitter` appears only as a
naming example in tooling docs. A vendor extension is possible and would be a JSON
object on a node — the same idea as a USD schema with weaker validation, or as FBX
custom properties with better conventions.

### 7.4 Alembic — explicitly not this

Alembic is "specifically NOT concerned with storing the complex dependency graph of
procedural tools", and its own documentation states it has no support for particle
systems. Particles go through `OPoints`/`IPoints` as a baked per-frame cloud. Same
category as `FbxCache`, and rejected for the same reason (§6.1): a NIF has no
simulation to bake.

COLLADA has no particle constructs either and is in practice superseded by glTF for
interchange.

### 7.5 Summary

| Format | Models an emitter | Carries the NIF description losslessly | Anything renders it |
| --- | --- | --- | --- |
| **X3D** | yes, natively | no — 3 physics models against 28 modifiers | yes (X_ITE) |
| **USD** | no, but a custom schema declares it | yes, typed and validated | no |
| **glTF** | no; a vendor extension would be needed | yes, as JSON | no |
| **FBX** | no | yes, as string properties | no |
| **Alembic** | no, and says so | no — baked points only | plays back |

Two different jobs, and no format does both. **X3D is the one worth having for showing
a system to someone; USD is the one worth having for moving it without loss.** FBX
does the second job less well than USD and the first not at all — it is the right
target only because it is the pipeline se-cmd already speaks.

**Sources**

- [X3D Particle systems component, ISO/IEC 19775-1:2023](https://www.web3d.org/specifications/X3Dv4/ISO-IEC19775-1v4-IS/Part01/components/particleSystems.html)
- [X_ITE ParticleSystem node](https://create3000.github.io/x_ite/components/particlesystems/particlesystem/)
- [X3D export and import tools](https://www.web3d.org/x3d/export-import)
- [Using Blender with X3D](https://www.web3d.org/blog/anitahavele/using-blender-x3d-comprehensive-guide)
- [Castle Game Engine planned features](https://castle-engine.io/planned_features.php)
- [X3D standards progress — language bindings](https://www.web3d.org/x3d/progress)
- [X3DJSAIL (Java)](https://www.web3d.org/specifications/java/X3DJSAIL.html) and [X3DPSAIL (Python)](https://www.web3d.org/x3d/stylesheets/python/python.html)
- [UsdGeomPointInstancer](https://openusd.org/docs/api/class_usd_geom_point_instancer.html)
- [AOUSD — What are OpenUSD schemas?](https://aousd.org/blog/explainer-series-for-developers-what-are-openusd-schemas/)
- [Generating new schema classes (codeless schemas)](https://openusd.org/release/tut_generating_new_schema.html)
- [glTF extension registry](https://github.com/KhronosGroup/glTF/blob/main/extensions/README.md)
- [Alembic](https://en.wikipedia.org/wiki/Alembic_(computer_graphics)) and [Alembic particle support discussion](https://groups.google.com/g/alembic-discussion/c/tMNOBWtE5hc)

---

## 8. Where se-cmd stands

se-cmd implements **D** (`Fbx/FbxParticleWriter.cs`, `Nif/NifParticleWriter.cs`): the
system block and its data block as prefixed string properties on the node that already
stands for the system, and the modifier stack as **one empty node per modifier** under
it, in order, each carrying its own fields and links. The node keeps its name, transform
and animation; no geometry is invented for it. Everything in §2.2 and §3 survives a
round trip.

Putting the stack in the tree costs nothing in fidelity and buys two things. A rigger
opening an outliner sees eleven named modifiers rather than one node with a hundred
properties on it — the fixture's system node went from 249 properties to 56 — and
**sibling order is stack order**, so moving one moves it in the file. The engine's own
ordering still comes from each modifier's `Order` field (§3), carried like any other,
with array position breaking its ties.

Each modifier's NIF name is carried beside the node's own, because the node name goes
through FBX's naming rules — `NiPSysAgeDeath:2` becomes `NiPSysAgeDeath_dd_2` — while
the NIF name is what a controller binds to (§4).

### 8.1 How the links are carried

By the name of what they point at, under the field's own key plus `_ref`, on the
modifier that holds the link:

```
NiPSysCylinderEmitter    emitter_object_ref = "PCloud06-Emitter"
NiPSysGravityModifier    gravity_object_ref = "Gravity01"
NiPSysAgeDeathModifier   spawn_modifier_ref = "NiPSysSpawnModifier:1"
```

A property rather than the object-to-object connection this section first proposed. A
connection is the more native mechanism and survives renaming, but resolving by name is
what the rest of this project already does — skin bones, animation targets, constraint
entities — and a particle system is not the place to introduce a second convention. It
is also what a NIF itself does one level up: a controller finds its modifier by
`Modifier Name` (§4), not by reference.

Resolution takes two passes, because the links are not all resolvable at once. One
naming a modifier of the same system is wired the moment the stack exists; one naming a
node has to wait for the whole tree, since an emitter object may be a sibling the walk
has not reached. That is the same deferral skins and animation already use.

An array of links is named element by element — `emitter_meshes_0_ref`,
`emitter_meshes_1_ref` — because the order is what the emission walks, and a count that
outlived its contents would rebuild an emitter that births from nowhere.

A collider chain hangs under its manager, as the modifiers hang under the system, so
sibling order is chain order there too.

Five links are deliberately **not** named: the system's `Data`, its `Modifiers` array,
each modifier's `Target` back-pointer, and a collider's `Next Collider` and `Parent`.
All five follow from the structure being rebuilt, and naming them as well would give
two sources for one fact.

A name that resolves to nothing is reported. Silence would mean an emitter emitting
from the origin, or gravity pulling towards it, with nothing in the file to say why.

### 8.2 What the survey changes, and what it does not

§7 establishes that FBX is the weakest of the surveyed formats for this: worse than USD
at transport, and unlike X3D it cannot show a particle system at all. That is worth
recording, and it does **not** change what se-cmd should do next, for two reasons.

The first is that the two jobs are separable, and only one of them is se-cmd's.
*Transporting* a system — getting it out of a NIF, through an editor, and back without
loss — is what a NIF ↔ FBX converter is for, and options B and C do it. *Presenting*
one — showing a modder what the effect looks like — is a different tool, and X3D is
what it would be written against.

The second is that FBX is not a technical choice here. It is the format FBXWrangler
speaks, the format Blender and Max import, and the format the rest of this project
already reads and writes. Moving particles alone to USD would mean a second exporter,
a second importer and a scene split across two files, for information that already
survives in the FBX intact.

So the survey's practical consequences are narrower than its conclusions:

- **C and D are done** (§8). The dropped links were the last real loss; the stack is
  now a subtree rather than a property list.
- **If a USD path is ever added** for other reasons, the particle system should move to
  a codeless custom schema rather than being re-encoded as strings. §7.2's argument is
  that the same information becomes typed and validated for no extra work, and the
  field-by-field walk this project already uses to flatten a block (`NifFieldCodec`) is
  most of what a schema generator would need.
- **If a preview is ever wanted**, X3D is the target, one-way, generated from the same
  neutral description rather than from the NIF. §7.1's table is the mapping; the losses
  in it are acceptable for looking at something and not for storing it. The audience is
  a browser runtime such as X_ITE, not a DCC suite, since no authoring tool implements
  the component — and writing the file needs `System.Xml.Linq` and no dependency, since
  X3D is XML and no C# binding exists to wait for.
- **Do not bake.** Both `FbxCache` (§6.1) and Alembic (§7.4) want a simulation, and a
  NIF contains none. Producing one means reimplementing Gamebryo's particle engine, and
  the result would be one-way even then.


## The run switch comes last

`NiPSysUpdateCtlr` is not a controller like the others: it holds no interpolator and no
keys, and is the switch that makes a particle system run at all. Skyrim puts it at the
**end** of the system's controller chain without exception — of the 516 particle systems
sampled, **515 have it last, none has it anywhere else**, and the one remaining has none
at all. The chains around it vary freely:

```
NiPSysEmitterCtlr -> NiPSysUpdateCtlr                                         156
NiPSysModifierActiveCtlr x3 -> NiPSysEmitterCtlr -> NiPSysUpdateCtlr          106
BSPSysMultiTargetEmitterCtlr -> NiPSysUpdateCtlr                               26
```

se-cmd attached controllers by appending to the chain, and the run switch is attached
before the emitter controller a sequence names, so it became the *head* and the file came
back inverted. The attach step now keeps it at the tail.
