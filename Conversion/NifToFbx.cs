using MeshIO.Formats.Fbx;
using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>Knobs for the NIF to FBX direction.</summary>
    public sealed class NifToFbxOptions
    {
        /// <summary>Prefix prepended to texture paths written into materials.</summary>
        public string TexturePath { get; set; } = string.Empty;

        /// <summary>Emit the tessellated geometry of Havok collision shapes.</summary>
        public bool ExportCollision { get; set; } = true;

        /// <summary>Emit the model's transform animation as FBX animation stacks.</summary>
        public bool ExportAnimation { get; set; } = true;
    }

    /// <summary>
    /// Converts a loaded NIF into an FBX scene.
    /// </summary>
    /// <remarks>
    /// Follows `docs/fbx-nif-conversion-spec.md` §4, which is FBXWrangler's
    /// behaviour. The conventions that matter most, because getting them wrong is
    /// silent rather than loud:
    ///
    /// <list type="bullet">
    /// <item>No axis conversion. The FBX declares Max axes (Z-up, right-handed), so
    /// coordinates cross unchanged.</item>
    /// <item>A shape's own transform is baked into its vertices, not left on the
    /// node.</item>
    /// <item>V is flipped on UVs.</item>
    /// <item>A mesh never attaches to the scene root directly; a
    /// <c>&lt;name&gt;_support</c> node is interposed.</item>
    /// </list>
    /// </remarks>
    public sealed class NifToFbx(NifModel model, NifToFbxOptions? options = null)
    {
        private readonly NifModel _model = model;
        private readonly NifToFbxOptions _options = options ?? new NifToFbxOptions();
        private readonly Dictionary<NifItem, FbxObject> _built = [];

        /// <summary>Diagnostics gathered during conversion.</summary>
        public List<string> Warnings { get; } = [];

        /// <summary>Converts the model into a fresh FBX document.</summary>
        public FbxDocument Convert()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            foreach (NifItem root in FindRootBlocks())
                ConvertNode(scene, root, parent: null);

            // Subtrees nothing parents, which the walk above cannot reach. They are
            // in the file because something points at them, so they are converted
            // before anything that binds by name goes looking.
            ConvertDetachedSubtrees(scene);

            // After the tree, for the same reason as the two below: a skin cluster
            // names a bone node, and a shape whose bones sit later in the tree found
            // none of them and left a deformer with no clusters at all.
            ConvertPendingSkins(scene);

            // After the tree, because a track binds to a model by name and every
            // model has to exist before anything can be bound to it.
            // Both need the whole tree: a constraint joins two bodies and a track
            // binds to a model by name, so nothing can be bound until every node
            // that could be its target exists.
            if (_options.ExportCollision)
                ConvertConstraints(scene);

            if (_options.ExportAnimation)
                ConvertAnimation(scene);

            scene.Flush();
            return document;
        }

        /// <summary>The rigid bodies converted so far, and the nodes standing for them.</summary>
        private readonly Dictionary<NifItem, (FbxObject Node, string Name)> _bodies = [];

        /// <summary>
        /// Emits the constraints between converted rigid bodies.
        /// </summary>
        /// <remarks>
        /// A constraint is listed by the bodies it joins, and a chain by every body
        /// along it, so the same block is reached more than once and is written the
        /// first time only.
        /// </remarks>
        private void ConvertConstraints(FbxScene scene)
        {
            var written = new HashSet<NifItem>();

            foreach (NifItem body in _bodies.Keys.ToList())
            {
                foreach (NifItem constraint in _model.GetRefArray(body, "Constraints"))
                {
                    if (!written.Add(constraint))
                        continue;

                    if (FbxConstraintWriter.AddConstraint(scene, _model, constraint, _bodies) is null)
                        Warnings.Add($"{constraint.Name}: neither body was converted, the constraint is dropped");
                }
            }
        }

        /// <summary>Writes the model's sequences as FBX animation stacks.</summary>
        private void ConvertAnimation(FbxScene scene)
        {
            foreach (AnimSequence sequence in _model.ReadAnimations())
            {
                foreach (string missing in FbxAnimWriter.AddSequence(scene, sequence, _modelsByName))
                    Warnings.Add($"{sequence.Name}: no node named \"{missing}\", its animation is dropped");
            }
        }

        /// <summary>
        /// The converted models by their NIF name, for binding animation to.
        /// </summary>
        /// <remarks>
        /// Keyed on the unsanitised name, because that is what a controlled block
        /// names its target with.
        /// </remarks>
        private readonly Dictionary<string, FbxObject> _modelsByName = new(StringComparer.Ordinal);

        /// <summary>Records a converted block under its NIF name, first one wins.</summary>
        /// <remarks>
        /// Duplicate names are legal in a NIF and a controlled block cannot tell them
        /// apart either, so binding to the first is as much as the format allows.
        /// </remarks>
        private void Remember(NifItem block, FbxObject node)
        {
            // The same name an animation track binds by: a block's own, or its class
            // when it has none. The game's cameras are unnamed and their frustum
            // controllers had nothing to bind to.
            string name = NifAnimAccess.TrackName(_model, block);

            if (name.Length > 0)
                _modelsByName.TryAdd(name, node);
        }

        /// <summary>
        /// The blocks nothing else points at, which are the scene roots.
        /// </summary>
        /// <remarks>
        /// The footer names them explicitly, but it is not always present or
        /// correct, so fall back to the first block — which is the root in every
        /// file Bethesda ships.
        /// </remarks>
        private List<NifItem> FindRootBlocks()
        {
            var roots = new List<NifItem>();

            NifItem? footerRoots = _model.FindItem(_model.Footer, "Roots");

            if (footerRoots is not null)
            {
                foreach (NifItem link in footerRoots.Children)
                {
                    if (_model.GetBlock(link) is { } block)
                        roots.Add(block);
                }
            }

            if (roots.Count == 0 && _model.Blocks.Count > 0)
                roots.Add(_model.Blocks[0]);

            return roots;
        }

        private FbxObject? ConvertNode(FbxScene scene, NifItem block, FbxObject? parent)
        {
            if (_built.TryGetValue(block, out FbxObject? existing))
                return existing;

            // Geometry is an attribute of a node in FBX, not a node itself.
            //
            // Two unrelated block families carry it. NiTriBasedGeom keeps its data
            // in a separate NiTriShapeData; BSTriShape, which Skyrim SE uses, packs
            // everything inline and inherits NiAVObject directly rather than
            // NiTriBasedGeom, so it needs testing for separately.
            if (_model.BlockInherits(block, "NiTriBasedGeom") || _model.BlockInherits(block, "BSTriShape"))
            {
                ConvertGeometry(scene, block, parent);
                return null;
            }

            if (!_model.BlockInherits(block, "NiAVObject"))
                return null;

            // The name no other block shares, which is what an animation track binds
            // by. Where it is not the block's own, `nif_name` carries the real one.
            string name = NameEncoding.Sanitize(NifAnimAccess.TrackName(_model, block));


            FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", _model.GetTransform(block));
            _built[block] = node;
            Remember(block, node);

            // The class, and whatever fields it adds to a plain NiNode. A particle
            // system is left alone: it has a carrier of its own that owns every field
            // it has, and two carriers writing one field is how one of them loses.
            if (FbxParticleWriter.IsParticleSystem(_model, block))
                FbxNodeType.Write(node, block);
            else
                FbxNodeType.WriteWithFields(node, _model, block, "NiNode", MultiBoundFields);

            // A block with no name at all -- the game's cameras have none -- is
            // exported under its class name, since FBX has no anonymous object. This
            // is what says the name was empty rather than that.
            FbxNodeType.WriteName(node, _model, block);

            // Everything hanging off the node that FBX has no place for: behaviour
            // graph paths, string data, bounds. BSXFlags is left out, since the import
            // recalculates it.
            FbxExtraDataWriter.AddExtraData(node, _model, block);

            // The volume a multi-bound node culls against, which the engine uses in
            // place of one worked out from the geometry.
            FbxMultiBound.Write(node, _model, block);
            AddMultiBoundMesh(scene, node, block, name);

            // A particle system has no geometry to export -- its vertices are a
            // runtime buffer the file only sizes -- so it stays an empty node with
            // the system carried alongside it.
            // A controller that holds no interpolator drives nothing the animation
            // layer can see, and nothing else in the file would bring it back. A
            // particle system's update switch is the familiar one; a skeleton's
            // BSLagBoneController is the same case on an ordinary node.
            if (!FbxParticleWriter.IsParticleSystem(_model, block))
                FbxNodeControllers.Write(node, _model, block, SequencedControllers);

            if (FbxParticleWriter.IsParticleSystem(_model, block))
            {
                FbxParticleWriter.AddParticleSystem(scene, node, _model, block);

                // A particle system is a shape: it has a shader and an alpha property
                // like any other, and they are what the effect actually looks like.
                // It has no geometry to hang them off, so they attach to the node.
                AddMaterialTo(scene, block, node, name, geometry: null);
            }

            if (parent is null)
                scene.ConnectToRoot(node);
            else
                scene.Connect(node, parent);

            if (_options.ExportCollision)
                ConvertCollision(scene, block, node);

            foreach (NifItem child in _model.GetChildren(block))
                ConvertNode(scene, child, node);

            return node;
        }

        /// <summary>
        /// Emits a node's Havok collision as tessellated geometry.
        /// </summary>
        /// <remarks>
        /// The rigid body becomes a node suffixed <c>_rb</c>, which is the marker
        /// the import side keys off (spec §3.1), and its shape becomes a mesh under
        /// it. A body's transform is a *world* transform even when parented, so it
        /// is written as-is rather than composed with anything.
        /// </remarks>
        private void ConvertCollision(FbxScene scene, NifItem block, FbxObject parent)
        {
            NifItem? collision = _model.GetRef(block, "Collision Object");

            if (collision is null)
                return;

            NifItem? body = _model.GetRef(collision, "Body");

            if (body is null)
                return;

            string name = NameEncoding.Sanitize(_model.GetName(block));

            if (name.Length == 0)
                name = block.Name;

            bool isPhantom = _model.BlockInherits(body, "bhkSimpleShapePhantom");
            string suffix = isPhantom ? "_sp" : "_rb";

            NifTransform transform = NifTransform.Identity;

            // The body's placement lives inside its Rigid Body Info, not beside it.
            // Read from the wrong path this silently yielded nothing, and every
            // collision body in every mesh exported at the origin -- which no fixture
            // caught, because the ones with collision all sit at the origin anyway.
            if (!isPhantom && _model.FindItem(body, @"Rigid Body Info\Translation") is { } translation)
            {
                // Havok works in metres; the rest of the file is in Skyrim units.
                NifVector4 t = translation.Value.Get<NifVector4>();
                var scaled = new NifVector3(
                    t.X * ShapeTessellator.BhkScaleFactor,
                    t.Y * ShapeTessellator.BhkScaleFactor,
                    t.Z * ShapeTessellator.BhkScaleFactor);

                NifQuat rotation =
                    _model.FindItem(body, @"Rigid Body Info\Rotation")?.Value.Get<NifQuat>() ?? NifQuat.Identity;
                transform = new NifTransform(scaled, NifTransform.RotationFromQuaternion(rotation), 1f);
            }

            // The body's transform is a world transform, and the node it hangs from
            // may be a bone several levels down. Written relative to that node, so the
            // body's *global* placement is its NIF one -- which is what a DCC tool
            // draws and what the import reads back.
            FbxObject bodyNode = FbxMeshWriter.AddModel(
                scene, name + suffix, "Null", FbxGlobalTransform.Under(scene, parent, transform));

            scene.Connect(bodyNode, parent);

            FbxCollisionObject.Write(bodyNode, _model, collision, body);
            FbxRigidBodyInfo.Write(bodyNode, _model, body);

            // Constraints join two bodies and are emitted once the walk has seen
            // both, so the bodies are remembered as they are converted.
            _bodies[body] = (bodyNode, name + suffix);

            NifItem? shape = _model.GetRef(body, "Shape");

            if (shape is null)
            {
                Warnings.Add($"{name}: collision body has no shape");
                return;
            }

            RememberOwner(shape, body);
            ConvertShape(scene, shape, bodyNode, name + suffix);
        }

        /// <summary>
        /// Walks a shape tree, emitting a mesh for each leaf and a node for each
        /// container, with the suffixes the import side recognises.
        /// </summary>
        private void ConvertShape(FbxScene scene, NifItem shape, FbxObject parent, string parentName, int depth = 0)
        {
            if (depth > 16)
            {
                Warnings.Add($"{parentName}: collision shape nests too deeply, stopping");
                return;
            }

            // Containers: emit a node and recurse.
            string? containerSuffix = shape.Name switch
            {
                "bhkTransformShape" or "bhkConvexTransformShape" => "_transform",
                "bhkListShape" => "_list",
                "bhkConvexListShape" => "_convex_list",
                "bhkMoppBvTreeShape" => "_mopp",
                _ => null
            };

            if (containerSuffix is not null)
            {
                string name = parentName + containerSuffix;
                FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", NifTransform.Identity);
                scene.Connect(node, parent);

                // The suffix says what kind of container this is, but not exactly
                // which class: a transform shape and a convex transform shape share
                // one, as they do in ck-cmd. The class itself travels alongside.
                FbxNodeType.Write(node, shape);

                // A MOPP tree just wraps the shape it indexes; the tree itself is
                // regenerated on import and carries nothing to convert.
                foreach (NifItem child in ChildShapesOf(shape))
                    ConvertShape(scene, child, node, name, depth + 1);

                return;
            }

            MeshGeometry? mesh = TessellateShape(shape);

            if (mesh is null)
            {
                Warnings.Add($"{parentName}: {shape.Name} is not a shape this converts yet");
                return;
            }

            if (mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{parentName}: {shape.Name} tessellated to nothing");
                return;
            }

            ShapeTessellator.Scale(mesh, ShapeTessellator.BhkScaleFactor);

            string shapeName = parentName + ShapeSuffix(shape.Name);
            FbxObject holder = FbxMeshWriter.AddModel(scene, shapeName, "Mesh", NifTransform.Identity);
            scene.Connect(holder, parent);

            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, shapeName + "_geometry", mesh);
            scene.Connect(geometry, holder);

            if (shape.Name == "bhkNiTriStripsShape")
                FbxStripsParts.Write(holder, _stripsParts);

            AddCollisionMaterial(scene, shape, holder, geometry);
        }

        /// <summary>
        /// Attaches the shape's Havok material to its mesh, as ck-cmd does.
        /// </summary>
        /// <remarks>
        /// Nothing in the tessellated triangles records whether the shape is wood or
        /// stone, and the engine reads that for footstep sound and impact response. It
        /// travels as an FBX material named after the enum, which a DCC tool can show
        /// and edit. Materials are shared between shapes that agree, so a file with one
        /// material comes back with one.
        /// </remarks>
        private void AddCollisionMaterial(
            FbxScene scene, NifItem shape, FbxObject holder, FbxObject geometry)
        {
            string material = FbxCollisionMaterial.NameOf(_model, shape);

            if (material.Length == 0)
                return;

            string layer = FbxCollisionMaterial.LayerOf(_model, _shapeOwners.GetValueOrDefault(shape));
            string key = $"{material}/{layer}";

            if (!_collisionMaterials.TryGetValue(key, out FbxObject? fbxMaterial))
            {
                fbxMaterial = scene.AddObject("Material", material, string.Empty);
                fbxMaterial.Node.Nodes.Add(new FbxNode("Version", 102));
                fbxMaterial.Node.Nodes.Add(new FbxNode("ShadingModel", "Phong"));
                fbxMaterial.Node.Nodes.Add(new FbxNode("MultiLayer", 0));

                fbxMaterial.Properties.Set(
                    FbxCollisionMaterial.LayerProperty, "KString", "", FbxProperties.UserFlags, layer);

                _collisionMaterials[key] = fbxMaterial;
            }

            scene.Connect(fbxMaterial, holder);
            FbxMeshWriter.AddSingleMaterialElement(geometry);
        }

        /// <summary>
        /// Records which body a shape belongs to, following the tree down.
        /// </summary>
        /// <remarks>
        /// The collision layer lives on the body's filter, not on the shape, so a leaf
        /// several containers below still has to find the body above it.
        /// </remarks>
        private void RememberOwner(NifItem shape, NifItem body, int depth = 0)
        {
            if (depth > 16 || !_shapeOwners.TryAdd(shape, body))
                return;

            foreach (NifItem child in ChildShapesOf(shape))
                RememberOwner(child, body, depth + 1);
        }

        /// <summary>Collision materials emitted so far, keyed by material and layer.</summary>
        private readonly Dictionary<string, FbxObject> _collisionMaterials = new(StringComparer.Ordinal);

        /// <summary>The body each shape hangs from, for the layer its filter names.</summary>
        private readonly Dictionary<NifItem, NifItem> _shapeOwners = [];

        /// <summary>
        /// Draws a multi-bound node's culling volume as a mesh beside it.
        /// </summary>
        /// <remarks>
        /// The exact numbers travel as properties; this is so the volume can be seen
        /// and resized in a DCC tool, which six numbers on a node cannot be. It is the
        /// same split the collision material and the effect shader use, and the import
        /// knows the suffix and skips it rather than turning it into geometry.
        /// </remarks>
        private void AddMultiBoundMesh(FbxScene scene, FbxObject node, NifItem block, string name)
        {
            if (!_model.BlockInherits(block, "BSMultiBoundNode")
                || _model.GetRef(block, "Multi Bound") is not { } bound
                || _model.GetRef(bound, "Data") is not { } data)
            {
                return;
            }

            MeshGeometry? mesh = data.Name switch
            {
                // Size is the full length of each side, where the tessellator takes
                // half-extents.
                "BSMultiBoundOBB" => ShapeTessellator.Box(
                    Half(_model.FindItem(data, "Size")?.Value.Get<NifVector3>() ?? default)),

                "BSMultiBoundSphere" => ShapeTessellator.Sphere(
                    _model.FindItem(data, "Radius")?.Value.ToFloat() ?? 0f),

                _ => null
            };

            if (mesh is null || mesh.Triangles.Count == 0)
                return;

            NifVector3 centre = _model.FindItem(data, "Center")?.Value.Get<NifVector3>() ?? default;

            NifMatrix33 rotation = _model.FindItem(data, "Rotation") is { } r
                ? r.Value.Get<NifMatrix33>()
                : NifMatrix33.Identity;

            string meshName = name + FbxMultiBound.MeshSuffix;

            FbxObject holder = FbxMeshWriter.AddModel(
                scene, meshName, "Mesh", new NifTransform(centre, rotation, 1f));

            scene.Connect(holder, node);
            scene.Connect(FbxMeshWriter.AddGeometry(scene, meshName + "_geometry", mesh), holder);
        }

        private static NifVector3 Half(NifVector3 size) =>
            new(size.X * 0.5f, size.Y * 0.5f, size.Z * 0.5f);

        /// <summary>
        /// Emits a block's shader and alpha properties as an FBX material on a node.
        /// </summary>
        /// <param name="geometry">
        /// The mesh the material applies to, or null for a block that has none — a
        /// particle system's shader describes a runtime buffer rather than triangles.
        /// </param>
        private void AddMaterialTo(
            FbxScene scene, NifItem shape, FbxObject holder, string name, FbxObject? geometry)
        {
            if (ReadMaterial(shape, name) is not { } material)
                return;

            FbxObject fbxMaterial = FbxMaterialWriter.AddMaterial(scene, material, _options.TexturePath);

            // An effect shader shares almost no fields with a lighting one, so its own
            // ride across flat on the same material rather than being forced through
            // the common form.
            if (_model.GetRef(shape, "Shader Property") is { } shader
                && FbxEffectShader.Is(_model, shader))
            {
                FbxEffectShader.Write(fbxMaterial, _model, shader);
            }

            // A material belongs to the node carrying the mesh, not the mesh, and the
            // geometry's material element points at index 0.
            scene.Connect(fbxMaterial, holder);

            if (geometry is not null)
                FbxMeshWriter.AddSingleMaterialElement(geometry);
        }

        /// <summary>
        /// The controllers a sequence names, which the sequence rebuilds.
        /// </summary>
        /// <remarks>
        /// Worked out once. Every node asks, and walking every sequence per node is
        /// the same answer computed as many times as the file has nodes.
        /// </remarks>
        private HashSet<NifItem>? _sequencedControllers;

        private HashSet<NifItem> SequencedControllers =>
            _sequencedControllers ??= NifAnimAccess.SequencedControllers(_model);

        /// <summary>The LOD level materials, shared by every shape that has levels.</summary>
        private readonly Dictionary<string, FbxObject> _lodMaterials = new(StringComparer.Ordinal);

        /// <summary>
        /// Marks which triangles belong to which level of detail.
        /// </summary>
        /// <remarks>
        /// A <c>BSLODTriShape</c>'s levels are three counts into one triangle list, and
        /// counts alone give an artist nothing to edit: they can be carried across
        /// (§5.2.4) but not authored. FBX has no LOD group, so the levels ride as a
        /// material per polygon — the one per-face channel every DCC tool shows and
        /// lets an artist reassign, and the same mechanism ck-cmd uses for collision
        /// materials (§4.8).
        ///
        /// The materials are named <c>LOD0</c>, <c>LOD1</c>, <c>LOD2</c> and are
        /// resolved by that name rather than by their index, so an artist adding or
        /// removing a material slot does not silently shift every triangle a level.
        /// The shape's own material stays where it was, connected first.
        ///
        /// This is the second, editable half of the pair the rest of the port uses:
        /// the counts are exact and win a round trip, and a marking that disagrees with
        /// them is an artist having said something, so it wins instead (§5C.1).
        /// </remarks>
        private void AddLodMaterials(
            FbxScene scene, NifItem shape, FbxObject holder, FbxObject geometry, MeshGeometry mesh)
        {
            if (!_model.BlockInherits(shape, "BSLODTriShape") || mesh.Triangles.Count == 0)
                return;

            // Whatever the shape's own material took, the levels follow.
            int first = scene.ChildrenOf(holder.Id).Count(o => o.Class == "Material");

            for (int level = 0; level < FbxLodSizes.Levels; level++)
            {
                string name = FbxLodSizes.LevelMaterial(level);

                if (!_lodMaterials.TryGetValue(name, out FbxObject? material))
                {
                    material = scene.AddObject("Material", name, string.Empty);
                    material.Node.Nodes.Add(new FbxNode("Version", 102));
                    material.Node.Nodes.Add(new FbxNode("ShadingModel", "Phong"));
                    material.Node.Nodes.Add(new FbxNode("MultiLayer", 0));

                    _lodMaterials[name] = material;
                }

                scene.Connect(material, holder);
            }

            List<int> levels = FbxLodSizes.LevelPerTriangle(_model, shape, mesh.Triangles.Count);

            FbxMeshWriter.AddPerPolygonMaterialElement(geometry, [.. levels.Select(l => first + l)]);
        }

        /// <summary>Fields the multi-bound carrier owns (§5.2.2).</summary>
        private static readonly HashSet<string> MultiBoundFields =
            new(StringComparer.Ordinal) { "Multi Bound", "Culling Mode" };

        /// <summary>Fields the LOD carrier owns (§5.2.4).</summary>
        private static readonly HashSet<string> LodFields =
            new(StringComparer.Ordinal) { "LOD0 Size", "LOD1 Size", "LOD2 Size", "Vertices", "Dynamic Data Size" };

        private IEnumerable<NifItem> ChildShapesOf(NifItem shape)
        {
            if (_model.GetRef(shape, "Shape") is { } single)
                yield return single;

            foreach (NifItem child in _model.GetRefArray(shape, "Sub Shapes"))
                yield return child;
        }

        private static string ShapeSuffix(string blockName) => blockName switch
        {
            "bhkSphereShape" => "_sphere",
            "bhkBoxShape" => "_box",
            "bhkCapsuleShape" => "_capsule",
            "bhkCylinderShape" => "_cylinder",
            "bhkConvexVerticesShape" => "_convex",
            "bhkPlaneShape" => "_plane",
            "bhkNiTriStripsShape" => "_strips",
            "bhkCompressedMeshShape" => "_mesh",
            _ => "_shape"
        };

        /// <summary>Tessellates a leaf shape, or null when it is not one we handle.</summary>
        private MeshGeometry? TessellateShape(NifItem shape) => shape.Name switch
        {
            "bhkSphereShape" => ShapeTessellator.Sphere(
                _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            "bhkBoxShape" => ShapeTessellator.Box(
                _model.FindItem(shape, "Dimensions")?.Value.Get<NifVector3>() ?? new NifVector3()),

            "bhkCapsuleShape" => ShapeTessellator.Capsule(
                _model.FindItem(shape, "First Point")?.Value.Get<NifVector3>() ?? new NifVector3(),
                _model.FindItem(shape, "Second Point")?.Value.Get<NifVector3>() ?? new NifVector3(),
                _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            // A cylinder's ends are flat discs through its two points, where a
            // capsule's are hemispheres a radius beyond them. Reading one as the other
            // makes a collision two radii too long.
            "bhkCylinderShape" => ShapeTessellator.Cylinder(
                Vector3Of(_model.FindItem(shape, "Vertex A")),
                Vector3Of(_model.FindItem(shape, "Vertex B")),
                _model.FindItem(shape, "Cylinder Radius")?.Value.ToFloat() ?? 0f),

            "bhkConvexVerticesShape" => ShapeTessellator.ConvexHull(ReadConvexVertices(shape)),

            // An infinite plane bounded by a box, which is what the game puts under
            // water and under a few things that must not fall.
            "bhkPlaneShape" => ShapeTessellator.Plane(
                _model.FindItem(shape, "Plane Normal")?.Value.Get<NifVector3>() ?? new NifVector3(),
                _model.FindItem(shape, "Plane Constant")?.Value.ToFloat() ?? 0f,
                Vector3Of(_model.FindItem(shape, "AABB Center")),
                Vector3Of(_model.FindItem(shape, "AABB Half Extents"))),

            // The LE-era mesh collision, still in a handful of SE files. Its geometry
            // is real triangles rather than the chunked form a compressed mesh uses,
            // so it needs no Havok to read -- only the strips unwound.
            "bhkNiTriStripsShape" => ReadTriStrips(shape),

            "bhkCompressedMeshShape" => ReadCompressedMesh(shape),

            _ => null
        };

        /// <summary>
        /// Decodes a <c>bhkCompressedMeshShape</c> back into triangles (spec §4.8.1).
        /// </summary>
        /// <remarks>
        /// The mesh is stored in two parts. "Big" vertices and triangles sit in the
        /// data block directly, at full precision. Everything else is chunked:
        /// vertices are 16-bit offsets from a per-chunk origin, scaled by 1/1000 and
        /// placed by a shared transform, and the triangles are held partly as strips
        /// and partly as a plain index list.
        /// </remarks>
        private MeshGeometry? ReadCompressedMesh(NifItem shape)
        {
            NifItem? data = _model.GetRef(shape, "Data");

            if (data is null)
                return null;

            var mesh = new MeshGeometry();

            // Big geometry is stored ready to use.
            if (_model.FindItem(data, "Big Verts") is { } bigVerts)
            {
                foreach (NifItem item in bigVerts.Children)
                {
                    NifVector4 v = item.Value.Get<NifVector4>();
                    mesh.Vertices.Add(new NifVector3(v.X, v.Y, v.Z));
                }
            }

            if (_model.FindItem(data, "Big Tris") is { } bigTris)
            {
                foreach (NifItem item in bigTris.Children)
                {
                    NifTriangle t = _model.FindItem(item, "Triangle")?.Value.Get<NifTriangle>() ?? default;

                    if (t.V1 < mesh.Vertices.Count && t.V2 < mesh.Vertices.Count && t.V3 < mesh.Vertices.Count)
                        mesh.Triangles.Add(t);
                }
            }

            var transforms = new List<NifTransform>();

            if (_model.FindItem(data, "Chunk Transforms") is { } chunkTransforms)
            {
                foreach (NifItem item in chunkTransforms.Children)
                {
                    NifVector4 t = _model.FindItem(item, "Translation")?.Value.Get<NifVector4>() ?? default;
                    NifQuat r = _model.FindItem(item, "Rotation")?.Value.Get<NifQuat>() ?? NifQuat.Identity;

                    transforms.Add(new NifTransform(
                        new NifVector3(t.X, t.Y, t.Z), NifTransform.RotationFromQuaternion(r), 1f));
                }
            }

            if (_model.FindItem(data, "Chunks") is not { } chunks)
                return mesh;

            foreach (NifItem chunk in chunks.Children)
            {
                NifVector4 origin = _model.FindItem(chunk, "Translation")?.Value.Get<NifVector4>() ?? default;
                int transformIndex = (int)_model.GetUInt(chunk, "Transform Index");

                NifTransform placement = transformIndex >= 0 && transformIndex < transforms.Count
                    ? transforms[transformIndex]
                    : NifTransform.Identity;

                var offsets = ReadUShorts(chunk, "Vertices");
                var indices = ReadUShorts(chunk, "Indices");
                var strips = ReadUShorts(chunk, "Strips");

                int firstVertex = mesh.Vertices.Count;

                // Vertices are millimetre offsets from the chunk's own origin.
                for (int i = 0; i + 2 < offsets.Count; i += 3)
                {
                    var local = new NifVector3(
                        origin.X + offsets[i] / 1000f,
                        origin.Y + offsets[i + 1] / 1000f,
                        origin.Z + offsets[i + 2] / 1000f);

                    mesh.Vertices.Add(placement.Apply(local));
                }

                int at = 0;

                // Strips first, alternating winding as a triangle strip does.
                foreach (ushort length in strips)
                {
                    for (int f = 0; f + 2 < length; f++)
                    {
                        if (at + f + 2 >= indices.Count)
                            break;

                        int a = firstVertex + indices[at + f];
                        int b = firstVertex + indices[at + f + 1];
                        int c = firstVertex + indices[at + f + 2];

                        mesh.Triangles.Add((f & 1) == 1
                            ? new NifTriangle((ushort)c, (ushort)b, (ushort)a)
                            : new NifTriangle((ushort)a, (ushort)b, (ushort)c));
                    }

                    at += length;
                }

                // Whatever follows the strips is a plain triangle list.
                for (int f = at; f + 2 < indices.Count; f += 3)
                {
                    mesh.Triangles.Add(new NifTriangle(
                        (ushort)(firstVertex + indices[f]),
                        (ushort)(firstVertex + indices[f + 1]),
                        (ushort)(firstVertex + indices[f + 2])));
                }
            }

            mesh.RecalculateNormals();
            return mesh;
        }

        private List<ushort> ReadUShorts(NifItem parent, string field)
        {
            var values = new List<ushort>();

            if (_model.FindItem(parent, field) is not { } array)
                return values;

            foreach (NifItem item in array.Children)
                values.Add((ushort)item.Value.ToUInt());

            return values;
        }

        /// <summary>
        /// A convex shape's vertices, which are stored as Vector4 with the fourth
        /// component unused.
        /// </summary>
        /// <summary>The XYZ of a four-component field, which is how Havok stores a point.</summary>
        private static NifVector3 Vector3Of(NifItem? item)
        {
            if (item is null)
                return new NifVector3();

            NifVector4 v = item.Value.Get<NifVector4>();

            return new NifVector3(v.X, v.Y, v.Z);
        }

        /// <summary>
        /// Decodes a <c>bhkNiTriStripsShape</c>, whose geometry is triangle strips.
        /// </summary>
        /// <remarks>
        /// A strip alternates its winding: the first triangle is (0,1,2), the second
        /// (1,3,2), and so on. Emitting them all the same way turns every other face
        /// inside out, which for collision means a surface the engine lets things
        /// through from one side.
        ///
        /// Degenerate triangles -- a repeated index -- are how a strip stitches to the
        /// next one and are dropped rather than emitted.
        /// </remarks>
        private MeshGeometry? ReadTriStrips(NifItem shape)
        {
            var mesh = new MeshGeometry();

            _stripsParts.Clear();

            foreach (NifItem data in _model.GetRefArray(shape, "Strips Data"))
            {
                int first = mesh.Vertices.Count;
                int firstTriangle = mesh.Triangles.Count;

                if (_model.FindItem(data, "Vertices") is { } vertices)
                {
                    foreach (NifItem vertex in vertices.Children)
                        mesh.Vertices.Add(vertex.Value.Get<NifVector3>());
                }

                if (_model.FindItem(data, "Points") is not { } strips)
                    continue;

                foreach (NifItem strip in strips.Children)
                {
                    for (int i = 0; i + 2 < strip.Children.Count; i++)
                    {
                        int a = first + (int)strip.Children[i].Value.ToUInt();
                        int b = first + (int)strip.Children[i + 1].Value.ToUInt();
                        int c = first + (int)strip.Children[i + 2].Value.ToUInt();

                        if (a == b || b == c || a == c)
                            continue;

                        mesh.Triangles.Add(i % 2 == 0
                            ? new NifTriangle((ushort)a, (ushort)b, (ushort)c)
                            : new NifTriangle((ushort)a, (ushort)c, (ushort)b));
                    }
                }

                _stripsParts.Add((
                    mesh.Vertices.Count - first,
                    mesh.Triangles.Count - firstTriangle));
            }

            if (mesh.Vertices.Count == 0)
                return null;

            mesh.RecalculateNormals();

            return mesh;
        }

        /// <summary>
        /// How the strips shape just decoded divided its geometry.
        /// </summary>
        /// <remarks>
        /// Filled by <see cref="ReadTriStrips"/> and read immediately after, by the
        /// caller that has the node to write it onto. One shape can hold several
        /// `NiTriStripsData` blocks and FBX has one mesh per node, so the seams are
        /// the one thing merging them loses.
        /// </remarks>
        private readonly List<(int Vertices, int Triangles)> _stripsParts = [];

        private List<NifVector3> ReadConvexVertices(NifItem shape)
        {
            var points = new List<NifVector3>();

            if (_model.FindItem(shape, "Vertices") is not { } vertices)
                return points;

            foreach (NifItem item in vertices.Children)
            {
                NifVector4 v = item.Value.Get<NifVector4>();
                points.Add(new NifVector3(v.X, v.Y, v.Z));
            }

            return points;
        }

        /// <summary>
        /// Exports a shape with no vertices as a node standing for it.
        /// </summary>
        /// <remarks>
        /// Everything a shape carries except the mesh: the class, its own fields, the
        /// shader and alpha property, the extra data. FBX has no mesh worth writing
        /// for a shape with nothing in it, and a DCC tool given one shows an object
        /// that cannot be selected, so it is a plain node with a mark saying what it
        /// was (§5.2.4).
        /// </remarks>
        private void ConvertEmptyShape(FbxScene scene, NifItem shape, FbxObject? parent)
        {
            string name = NameEncoding.Sanitize(NifAnimAccess.TrackName(_model, shape));

            FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", _model.GetTransform(shape));

            if (parent is null)
                scene.ConnectToRoot(node);
            else
                scene.Connect(node, parent);

            node.Properties.SetUserString(FbxNodeType.EmptyShapeProperty, "1");

            FbxNodeType.WriteWithFields(
                node, _model, shape,
                _model.BlockInherits(shape, "BSTriShape") ? "BSTriShape" : "NiTriBasedGeom",
                LodFields);

            FbxNodeType.WriteName(node, _model, shape);
            FbxDynamicShape.Write(node, _model, shape);
            FbxLodSizes.Write(node, _model, shape);
            FbxExtraDataWriter.AddExtraData(node, _model, shape);

            // The shader and alpha property are what the effect looks like, and the
            // shape having no vertices of its own does not make them any less its.
            AddMaterialTo(scene, shape, node, name, geometry: null);

            _built[shape] = node;
            Remember(shape, node);
        }

        private void ConvertGeometry(FbxScene scene, NifItem shape, FbxObject? parent)
        {
            MeshGeometry? mesh;

            if (_model.BlockInherits(shape, "BSTriShape"))
            {
                mesh = ReadBsTriShapeGeometry(shape);
            }
            else
            {
                NifItem? data = _model.GetRef(shape, "Data");

                mesh = data is null ? null : ReadGeometry(shape, data);
            }

            // A shape with nothing to draw is still a block, and the game builds real
            // effects out of them -- a lightning controller generates its geometry into
            // one at runtime. It travels as a node marked as what it is, rather than
            // not travelling.
            if (mesh is null || mesh.IsEmpty)
            {
                ConvertEmptyShape(scene, shape, parent);
                return;
            }

            if (!mesh.IsWellFormed(out string? problem))
            {
                Warnings.Add($"{_model.GetName(shape)}: {problem}");
                return;
            }

            string name = NameEncoding.Sanitize(NifAnimAccess.TrackName(_model, shape));

            // FBX allows one mesh attribute per node, and refuses meshes parented
            // straight to the scene root, so a holder node is interposed in both
            // cases. The _support suffix is what FBXWrangler uses and what the
            // import side looks for.
            FbxObject holder = FbxMeshWriter.AddModel(scene, $"{name}_support", "Mesh", NifTransform.Identity);

            if (parent is null)
                scene.ConnectToRoot(holder);
            else
                scene.Connect(holder, parent);

            // The game ships a few effect meshes whose vertices are NaN in the file
            // itself -- explosionilusiondark01's lightRays among them, where the
            // node's rotation matrix is NaN in all nine entries too. Writing that into
            // an FBX passes the problem to whatever opens it, and a DCC tool given a
            // NaN vertex does not report a bad mesh, it misbehaves.
            if (mesh.Vertices.Any(v => float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z)))
                Warnings.Add($"{name}: the source's vertices are not numbers, the mesh is exported as it is");

            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, name, mesh);

            // Which geometry class this was. BSDynamicTriShape and BSTriShape hold the
            // same vertices and are not the same thing to the engine.
            // The class, and the fields it adds to the geometry it derives from --
            // the LOD counts have their own carrier, being an array of three.
            FbxNodeType.WriteWithFields(
                geometry, _model, shape,
                _model.BlockInherits(shape, "BSTriShape") ? "BSTriShape" : "NiTriBasedGeom",
                LodFields);
            FbxDynamicShape.Write(geometry, _model, shape);
            FbxLodSizes.Write(geometry, _model, shape);

            // The geometry is named uniquely too, so two shapes sharing a name can be
            // told apart; this says which name each really had.
            FbxNodeType.WriteName(geometry, _model, shape);

            // A shape carries extra data as a node does, and it was going nowhere: a
            // glow plant's two NiBooleanExtraData hang on its NiTriShape, not on the
            // node above it. It rides on the geometry because that is what stands for
            // the shape block -- the holder stands for the node.
            FbxExtraDataWriter.AddExtraData(geometry, _model, shape);

            scene.Connect(geometry, holder);

            ConvertSkin(scene, shape, geometry);

            AddMaterialTo(scene, shape, holder, name, geometry);

            AddLodMaterials(scene, shape, holder, geometry, mesh);

            // A flipbook controller hangs off a property rather than off the shape,
            // but the node is what an importer has to put it back on.
            FbxFlipWriter.AddFlipControllers(holder, _model, shape);

            _built[shape] = holder;

            // The holder is the node with the transform, so it is what an animation
            // track has to drive; the geometry under it never moves on its own.
            Remember(shape, holder);
        }

        /// <summary>
        /// Attaches a skin to a converted mesh, if the shape has one.
        /// </summary>
        /// <remarks>
        /// Bones are FBX Models, so they must already exist. They do: a bone is a
        /// NiNode somewhere in the hierarchy, and the walk converts the whole tree
        /// before any geometry beneath it. A bone the walk never reached is reported
        /// rather than silently dropping that bone's influence.
        /// </remarks>
        /// <summary>
        /// Converts node subtrees that are referenced but never parented.
        /// </summary>
        /// <remarks>
        /// The walk starts at the footer's roots and follows children, which is the
        /// scene. A NIF can also hold nodes that nothing parents, kept alive only by
        /// something pointing at them: `fxdragoncrashfurrow01` has three, each named
        /// by a `BSPSysHavokUpdateModifier` as the debris its particles throw, each a
        /// node with a collision object and a shaded mesh beneath it. Six nodes, three
        /// shapes and three collision bodies were simply never visited.
        ///
        /// They become scene roots, marked so the import knows not to parent them
        /// (§5.2.5). Only nodes something actually points at: an unreferenced block is
        /// a file's problem to have, not this one's to invent a place for.
        /// </remarks>
        private void ConvertDetachedSubtrees(FbxScene scene)
        {
            foreach (NifItem block in Referenced())
            {
                if (_built.ContainsKey(block))
                    continue;

                if (ConvertNode(scene, block, parent: null) is { } node)
                    node.Properties.SetUserString(FbxNodeType.DetachedProperty, "1");
            }
        }

        /// <summary>
        /// Every <c>NiAVObject</c> some reference in the file points at.
        /// </summary>
        /// <remarks>
        /// References only. A pointer is the upward half of a two-way link — a
        /// collision object's <c>Target</c> names the node it hangs on — and a node
        /// kept alive only by one of those is not referenced by anything.
        /// </remarks>
        private IEnumerable<NifItem> Referenced()
        {
            var seen = new HashSet<NifItem>();

            foreach (NifItem block in _model.Blocks)
            {
                foreach (NifItem link in LinksUnder(block))
                {
                    if (link.Value.Type != NifValueType.Link
                        || _model.GetBlock(link) is not { } target
                        || !_model.BlockInherits(target, "NiAVObject"))
                    {
                        continue;
                    }

                    if (seen.Add(target))
                        yield return target;
                }
            }
        }

        private static IEnumerable<NifItem> LinksUnder(NifItem item)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Value.Type is NifValueType.Link or NifValueType.UpLink)
                    yield return child;
                else
                    foreach (NifItem nested in LinksUnder(child))
                        yield return nested;
            }
        }

        /// <summary>Shapes whose skin is waiting for the rest of the tree.</summary>
        private readonly List<(NifItem Shape, FbxObject Geometry)> _pendingSkins = [];

        /// <summary>Remembers a shape's skin, to be written once every node exists.</summary>
        /// <remarks>
        /// A cluster names a bone node, and the walk reaches a shape before it reaches
        /// everything else. `wrdrawbridge01`'s chains hang from four bones that come
        /// after it, so all four were missing and the shape left with a skin deformer
        /// holding no clusters -- which is not a skin, and came back as no skin at all.
        /// </remarks>
        private void ConvertSkin(FbxScene scene, NifItem shape, FbxObject geometry)
        {
            if (_model.ReadSkin(shape) is not null)
                _pendingSkins.Add((shape, geometry));
        }

        /// <summary>Writes every deferred skin, now that the whole tree exists.</summary>
        private void ConvertPendingSkins(FbxScene scene)
        {
            foreach ((NifItem shape, FbxObject geometry) in _pendingSkins)
                WriteSkin(scene, shape, geometry);

            _pendingSkins.Clear();
        }

        private void WriteSkin(FbxScene scene, NifItem shape, FbxObject geometry)
        {
            SkinData? skin = _model.ReadSkin(shape);

            if (skin is null)
                return;

            var bones = new Dictionary<string, FbxObject>(StringComparer.Ordinal);

            foreach ((NifItem block, FbxObject node) in _built)
            {
                if (node.Class != "Model")
                    continue;

                string name = _model.GetName(block);

                if (name.Length > 0)
                    bones[name] = node;
            }

            foreach (string problem in FbxSkinIO.AddSkin(scene, geometry, skin, bones, NifTransform.Identity))
                Warnings.Add($"{_model.GetName(shape)}: {problem}");
        }

        /// <summary>
        /// The visible half of an effect shader, as an ordinary FBX material.
        /// </summary>
        /// <remarks>
        /// The exact values travel as properties (see <see cref="FbxEffectShader"/>)
        /// and are what the import reads back. This is the other half: enough of the
        /// shader expressed in FBX's own terms that the surface looks like itself in a
        /// DCC tool — its texture on the diffuse channel, its base colour tinting it,
        /// its own UV transform.
        ///
        /// Without it the material is a white Phong with nothing connected, and an
        /// artist opening the file sees a blank surface beside correctly textured
        /// lighting-shader ones. The properties would still reimport perfectly, which
        /// is exactly what makes that failure easy to miss.
        /// </remarks>
        private MaterialData ReadEffectMaterial(NifItem shape, NifItem shader, string name)
        {
            NifColor4 baseColor = _model.FindItem(shader, "Base Color") is { } colour
                ? colour.Value.Get<NifColor4>()
                : new NifColor4(1f, 1f, 1f, 1f);

            var material = new MaterialData
            {
                Name = name,
                DiffuseColor = new NifColor3(baseColor.R, baseColor.G, baseColor.B),

                // The base colour's alpha is the effect's overall opacity.
                Alpha = baseColor.A,

                EmissiveColor = new NifColor3(baseColor.R, baseColor.G, baseColor.B),
                EmissiveMultiple = FloatOf(shader, "Base Color Scale", 1f),
                UvOffset = Vector2Of(shader, "UV Offset", new NifVector2(0f, 0f)),
                UvScale = Vector2Of(shader, "UV Scale", new NifVector2(1f, 1f)),
                TextureClampMode = _model.GetUInt(shader, "Texture Clamp Mode")
            };

            // Same as a lighting shader's: the alpha property is the shape's, not the
            // shader's, and it drives the transparency connection on the texture.
            if (_model.GetRef(shape, "Alpha Property") is { } alpha)
            {
                material.AlphaId = _model.IndexOf(alpha);

                material.AlphaProperty = AlphaSettings.FromFlags(
                    (ushort)_model.GetUInt(alpha, "Flags"),
                    (byte)_model.GetUInt(alpha, "Threshold"));
            }

            // An effect shader names its textures directly rather than through a set.
            // Only the first has a standard FBX channel; the greyscale map follows the
            // convention the texture set uses for its later slots.
            material.Textures.Add(_model.GetString(shader, "Source Texture"));
            material.Textures.Add(string.Empty);
            material.Textures.Add(_model.GetString(shader, "Greyscale Texture"));

            return material;
        }

        /// <summary>
        /// Reads a shape's shader and alpha properties into the neutral material
        /// form, or null when it has no shader property.
        /// </summary>
        private MaterialData? ReadMaterial(NifItem shape, string name)
        {
            NifItem? shader = _model.GetRef(shape, "Shader Property");

            if (shader is null)
                return null;

            // An effect shader has none of the fields read below -- no specular model,
            // no texture set -- so it gets a bare material to hang its own fields on
            // rather than being read as a lighting shader that happens to be empty.
            // ck-cmd returns null here instead, which loses the shape's material
            // entirely: see `docs/fbx-nif-conversion-spec.md` §4.3.
            if (FbxEffectShader.Is(_model, shader))
                return ReadEffectMaterial(shape, shader, name);

            var material = new MaterialData
            {
                Name = name,
                EmissiveColor = Color3Of(shader, "Emissive Color"),
                EmissiveMultiple = FloatOf(shader, "Emissive Multiple", 1f),
                SpecularColor = Color3Of(shader, "Specular Color"),
                SpecularStrength = FloatOf(shader, "Specular Strength"),
                Glossiness = FloatOf(shader, "Glossiness"),
                Alpha = FloatOf(shader, "Alpha", 1f),
                EnvironmentMapScale = FloatOf(shader, "Environment Map Scale"),
                UvOffset = Vector2Of(shader, "UV Offset", new NifVector2(0f, 0f)),
                UvScale = Vector2Of(shader, "UV Scale", new NifVector2(1f, 1f)),
                TextureClampMode = _model.GetUInt(shader, "Texture Clamp Mode")
            };

            // The shader path is stored on the NiObjectNET level, guarded by an
            // onlyT condition, and is written out by name rather than as a number.
            if (_model.FindItem(shader, "Shader Type") is { } shaderType
                && _model.Database.TryGetEnumOptionName(
                    shaderType.Type, shaderType.Value.ToUInt(), out string typeName))
            {
                material.ShaderType = typeName;
            }

            if (_model.GetRef(shader, "Texture Set") is { } textureSet
                && _model.FindItem(textureSet, "Textures") is { } textures)
            {
                material.TextureSetId = _model.IndexOf(textureSet);

                foreach (NifItem texture in textures.Children)
                    material.Textures.Add(texture.Value.AsString());
            }

            if (_model.GetRef(shape, "Alpha Property") is { } alphaProperty)
            {
                material.AlphaId = _model.IndexOf(alphaProperty);

                material.AlphaProperty = AlphaSettings.FromFlags(
                    (ushort)_model.GetUInt(alphaProperty, "Flags"),
                    (byte)_model.GetUInt(alphaProperty, "Threshold"));
            }

            return material;
        }

        private float FloatOf(NifItem block, string field, float fallback = 0f) =>
            _model.FindItem(block, field) is { } item ? item.Value.ToFloat() : fallback;

        private NifColor3 Color3Of(NifItem block, string field) =>
            _model.FindItem(block, field)?.Value.Get<NifColor3>() ?? new NifColor3();

        private NifVector2 Vector2Of(NifItem block, string field, NifVector2 fallback) =>
            _model.FindItem(block, field) is { } item ? item.Value.Get<NifVector2>() : fallback;

        /// <summary>
        /// Reads a geometry data block into the neutral mesh form, baking the
        /// shape's own transform into the vertices and flipping V.
        /// </summary>
        private MeshGeometry ReadGeometry(NifItem shape, NifItem data)
        {
            var mesh = new MeshGeometry();

            NifTransform transform = _model.GetTransform(shape);

            foreach (NifVector3 v in _model.GetVertices(data))
                mesh.Vertices.Add(transform.Apply(v));

            foreach (NifVector3 n in _model.GetNormals(data))
                mesh.Normals.Add(transform.ApplyDirection(n));

            // NIF's V axis points the other way from FBX's.
            foreach (NifVector2 uv in _model.GetUvSet(data))
                mesh.Uvs.Add(new NifVector2(uv.X, 1f - uv.Y));

            mesh.Colors.AddRange(_model.GetVertexColors(data));
            mesh.Triangles.AddRange(_model.GetGeometryTriangles(data));

            return mesh;
        }

        /// <summary>
        /// Reads a <c>BSTriShape</c>, which packs its vertex data inline rather than
        /// in a separate data block.
        /// </summary>
        /// <remarks>
        /// Each vertex is a struct whose fields are present or absent according to
        /// the flags in <c>Vertex Desc</c>, and whose positions may be full floats
        /// or halves depending on the same flags. The reader already resolves all of
        /// that — the array is a "fixed compound", so the layout is decided once
        /// from the first element — which leaves only reading the values out.
        ///
        /// The bitangent is the awkward one: it is split across three separate
        /// fields, X alongside the position and Y and Z alongside the normal and
        /// tangent, because it is packed into the spare lanes of those vectors.
        /// </remarks>
        /// <summary>
        /// A dynamic shape's own vertex buffer, when it has one worth reading.
        /// </summary>
        /// <remarks>
        /// `BSDynamicTriShape` carries a second array of positions that the engine
        /// rewrites as the mesh moves. In the files seen it is not a copy of the
        /// static ones — those are zero, and this is where the shape actually is.
        ///
        /// Null when the shape has no such buffer, or when it does not line up with
        /// the vertex data, since a mismatched buffer says less than the static
        /// positions do.
        /// </remarks>
        private List<NifVector3>? DynamicPositions(NifItem shape, int count)
        {
            if (!_model.BlockInherits(shape, "BSDynamicTriShape")
                || _model.FindItem(shape, "Vertices") is not { } buffer
                || buffer.Children.Count != count)
            {
                return null;
            }

            var positions = new List<NifVector3>(count);

            foreach (NifItem vertex in buffer.Children)
            {
                NifVector4 v = vertex.Value.Get<NifVector4>();
                positions.Add(new NifVector3(v.X, v.Y, v.Z));
            }

            return positions;
        }

        private MeshGeometry? ReadBsTriShapeGeometry(NifItem shape)
        {
            NifItem? vertexData = _model.FindItem(shape, "Vertex Data");

            // Each entry is a partition and the vertex map that translates its
            // triangle indices; a shape holding its own geometry has one entry with
            // no map.
            var triangleSources = new List<(NifItem Source, List<ushort>? VertexMap)>();

            // A skinned Skyrim SE shape keeps nothing in itself: the vertex data and
            // the triangles both live in the skin partition, and the shape's own
            // counts are zero. Follow the skin when that is the case.
            if (vertexData is null || vertexData.Children.Count == 0)
            {
                NifItem? partition = FindSkinPartition(shape);

                if (partition is null)
                    return null;

                // The vertex array is shared by every partition; only the triangles
                // and the maps into it are per partition.
                vertexData = _model.FindItem(partition, "Vertex Data");

                if (_model.FindItem(partition, "Partitions") is { } partitions)
                {
                    foreach (NifItem entry in partitions.Children)
                    {
                        List<ushort>? map = null;

                        if (_model.FindItem(entry, "Vertex Map") is { } mapItem)
                        {
                            map = [];

                            foreach (NifItem vertex in mapItem.Children)
                                map.Add((ushort)vertex.Value.ToUInt());
                        }

                        triangleSources.Add((entry, map));
                    }
                }
            }
            else
            {
                triangleSources.Add((shape, null));
            }

            if (vertexData is null || vertexData.Children.Count == 0)
                return null;

            var mesh = new MeshGeometry();
            NifTransform transform = _model.GetTransform(shape);

            // Which attributes are present is fixed for the whole array.
            NifItem first = vertexData.Children[0];

            bool hasNormals = _model.FindItem(first, "Normal") is not null;
            bool hasTangents = _model.FindItem(first, "Tangent") is not null;
            bool hasUvs = _model.FindItem(first, "UV") is not null;
            bool hasColors = _model.FindItem(first, "Vertex Colors") is not null;

            // A dynamic shape keeps its positions in the buffer the engine writes
            // into every frame, and the static entries beside them are zero. Reading
            // those instead collapses the whole mesh onto the origin -- 136 vertices
            // all in one place -- with the right counts throughout, so nothing about
            // the file looks wrong.
            List<NifVector3>? dynamic = DynamicPositions(shape, vertexData.Children.Count);

            for (int i = 0; i < vertexData.Children.Count; i++)
            {
                NifItem vertex = vertexData.Children[i];

                NifVector3 position = dynamic is not null
                    ? dynamic[i]
                    : _model.FindItem(vertex, "Vertex")?.Value.Get<NifVector3>() ?? new NifVector3();

                mesh.Vertices.Add(transform.Apply(position));

                if (hasNormals)
                {
                    NifVector3 normal = _model.FindItem(vertex, "Normal")?.Value.Get<NifVector3>() ?? new NifVector3();
                    mesh.Normals.Add(transform.ApplyDirection(normal));
                }

                if (hasTangents)
                {
                    NifVector3 tangent = _model.FindItem(vertex, "Tangent")?.Value.Get<NifVector3>() ?? new NifVector3();
                    mesh.Tangents.Add(transform.ApplyDirection(tangent));

                    // Reassembled from the three lanes it was packed into.
                    var bitangent = new NifVector3(
                        _model.FindItem(vertex, "Bitangent X")?.Value.ToFloat() ?? 0f,
                        ByteToSNorm(_model.FindItem(vertex, "Bitangent Y")),
                        ByteToSNorm(_model.FindItem(vertex, "Bitangent Z")));

                    mesh.Bitangents.Add(transform.ApplyDirection(bitangent));
                }

                if (hasUvs)
                {
                    NifVector2 uv = _model.FindItem(vertex, "UV")?.Value.Get<NifVector2>() ?? new NifVector2();
                    mesh.Uvs.Add(new NifVector2(uv.X, 1f - uv.Y));
                }

                if (hasColors)
                    mesh.Colors.Add(_model.FindItem(vertex, "Vertex Colors")?.Value.Get<NifColor4>()
                                    ?? new NifColor4(1f, 1f, 1f, 1f));
            }

            // Every partition contributes triangles over the shared vertex array, so
            // the mesh is the union of them all. Converting only the first drops
            // whole sections of anything split across several, which real armour
            // routinely is.
            foreach ((NifItem source, List<ushort>? vertexMap) in triangleSources)
            {
                if (_model.FindItem(source, "Triangles") is not { } triangles)
                    continue;

                foreach (NifItem item in triangles.Children)
                {
                    NifTriangle t = item.Value.Get<NifTriangle>();

                    // Partition triangles index the partition's own vertex list.
                    if (vertexMap is not null)
                    {
                        if (t.V1 >= vertexMap.Count || t.V2 >= vertexMap.Count || t.V3 >= vertexMap.Count)
                            continue;

                        t = new NifTriangle(vertexMap[t.V1], vertexMap[t.V2], vertexMap[t.V3]);
                    }

                    if (t.V1 < mesh.Vertices.Count && t.V2 < mesh.Vertices.Count && t.V3 < mesh.Vertices.Count)
                        mesh.Triangles.Add(t);
                }
            }

            return mesh;
        }

        /// <summary>The skin partition a shape's geometry lives in, if it is skinned.</summary>
        private NifItem? FindSkinPartition(NifItem shape)
        {
            NifItem? skin = _model.GetRef(shape, "Skin");

            if (skin is null)
                return null;

            // The partition may hang off the skin instance or off its data.
            if (_model.GetRef(skin, "Skin Partition") is { } fromInstance)
                return fromInstance;

            NifItem? data = _model.GetRef(skin, "Data");

            return data is null ? null : _model.GetRef(data, "Skin Partition");
        }

        /// <summary>
        /// Expands a packed byte back to the -1..1 range, as the vertex formats
        /// store the spare bitangent lanes.
        /// </summary>
        private static float ByteToSNorm(NifItem? item) =>
            item is null ? 0f : (float)(item.Value.ToUInt() / 255.0 * 2.0 - 1.0);
    }

    /// <summary>
    /// Builds the fixed scaffolding every FBX file carries around its object graph.
    /// </summary>
    public static class FbxDocumentTemplate
    {
        /// <summary>
        /// An empty FBX 7.4 document with the header, global settings and empty
        /// object and connection sections.
        /// </summary>
        /// <remarks>
        /// Global settings declare Max axes (Z-up, right-handed) and centimetres,
        /// matching what FBXWrangler sets on the scene. Those two declarations are
        /// what let coordinates pass through unconverted.
        /// </remarks>
        public static FbxDocument CreateEmpty()
        {
            var document = new FbxDocument { Version = FbxVersion.v7400 };

            var header = new FbxNode("FBXHeaderExtension");
            header.Nodes.Add(new FbxNode("FBXHeaderVersion", 1003));
            header.Nodes.Add(new FbxNode("FBXVersion", (int)FbxVersion.v7400));

            // Not decoration: readers reject a header without a timestamp.
            DateTime now = DateTime.Now;
            var stamp = new FbxNode("CreationTimeStamp");
            stamp.Nodes.Add(new FbxNode("Version", 1000));
            stamp.Nodes.Add(new FbxNode("Year", now.Year));
            stamp.Nodes.Add(new FbxNode("Month", now.Month));
            stamp.Nodes.Add(new FbxNode("Day", now.Day));
            stamp.Nodes.Add(new FbxNode("Hour", now.Hour));
            stamp.Nodes.Add(new FbxNode("Minute", now.Minute));
            stamp.Nodes.Add(new FbxNode("Second", now.Second));
            stamp.Nodes.Add(new FbxNode("Millisecond", now.Millisecond));
            header.Nodes.Add(stamp);

            header.Nodes.Add(new FbxNode("Creator", "se-cmd"));
            document.Nodes.Add(header);

            document.Nodes.Add(new FbxNode("Creator", "se-cmd"));

            var settings = new FbxNode("GlobalSettings");
            settings.Nodes.Add(new FbxNode("Version", 1000));

            var properties = new FbxNode("Properties70");
            settings.Nodes.Add(properties);

            var globals = new FbxProperties(properties);

            // Z-up, right-handed: FbxAxisSystem::Max, which is also NIF's convention.
            globals.Set("UpAxis", "int", "Integer", "", 2);
            globals.Set("UpAxisSign", "int", "Integer", "", 1);
            globals.Set("FrontAxis", "int", "Integer", "", 1);
            globals.Set("FrontAxisSign", "int", "Integer", "", -1);
            globals.Set("CoordAxis", "int", "Integer", "", 0);
            globals.Set("CoordAxisSign", "int", "Integer", "", 1);
            globals.Set("UnitScaleFactor", "double", "Number", "", 1.0);

            document.Nodes.Add(settings);

            var definitions = new FbxNode("Definitions");
            definitions.Nodes.Add(new FbxNode("Version", 100));
            definitions.Nodes.Add(new FbxNode("Count", 0));
            document.Nodes.Add(definitions);

            document.Nodes.Add(new FbxNode("Objects"));
            document.Nodes.Add(new FbxNode("Connections"));

            return document;
        }
    }
}
