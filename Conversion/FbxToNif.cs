using System.Globalization;
using SECmd.Havok;
using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>Knobs for the FBX to NIF direction.</summary>
    public sealed class FbxToNifOptions
    {
        /// <summary>Mirror U on import. Off by default, as in FBXWrangler.</summary>
        public bool InvertU { get; set; }

        /// <summary>Mirror V on import. On by default: NIF's V axis points the other way.</summary>
        public bool InvertV { get; set; } = true;

        /// <summary>Name given to the root block. Defaults to the file stem.</summary>
        public string RootName { get; set; } = "Scene";

        public uint Version { get; set; } = 0x14020007;

        public uint UserVersion { get; set; } = 12;

        /// <summary>
        /// Target Skyrim Legendary Edition rather than Special Edition.
        /// </summary>
        /// <remarks>
        /// nif.xml distinguishes the two only by the Bethesda stream version: both
        /// are file version 20.2.0.7 with user version 12, LE being
        /// <c>V20_2_0_7_SKY</c> at 83 and SE <c>V20_2_0_7_SSE</c> at 100.
        ///
        /// That one number changes which geometry block is legal. BSTriShape is
        /// declared <c>versions="#SSE# #FO4# #F76#"</c> and so does not exist in LE,
        /// while NiTriShape is unrestricted. Writing NiTriShape into an SE file
        /// parses, but is not what the engine expects — converting between the two
        /// is the entire purpose of SSE NIF Optimizer.
        /// </remarks>
        public bool LegendaryEdition { get; set; }

        /// <summary>
        /// The skin instance class to build when the scene does not say which.
        /// </summary>
        /// <remarks>
        /// Nothing about a mesh decides this. Across the 26,940 skinned shapes the game
        /// ships, 15,728 are `BSDismemberSkinInstance` and 11,212 are plain
        /// `NiSkinInstance`; the Bethesda version does not separate them, and neither
        /// does the folder — `meshes/actors/character` alone holds 11,433 of the first
        /// and 9,772 of the second. The difference is what the shape is *for*: a
        /// dismember instance carries body-part slots, which is what lets a cuirass
        /// hide the body under it and a limb come off.
        ///
        /// A scene converted from a NIF carries the answer and this is not consulted.
        /// It decides only for an FBX authored elsewhere, where the dismember form is
        /// the better guess: new Skyrim content is mostly armour and body parts, and a
        /// shape that has slots it does not need is easier to live with than one that
        /// needs slots it has not got.
        /// </remarks>
        public string SkinInstanceType { get; set; } = "BSDismemberSkinInstance";

        /// <summary>
        /// Build the collision of a skeleton rather than of an object.
        /// </summary>
        /// <remarks>
        /// A skeleton's collision objects are <c>bhkBlendCollisionObject</c>s and its
        /// bodies plain <c>bhkRigidBody</c>s, where an object's are
        /// <c>bhkCollisionObject</c> and <c>bhkRigidBodyT</c>. The difference is not
        /// cosmetic: the BSXFlags calculation defines a skeleton as *having* a blend
        /// object, so a rig built without one is not a ragdoll as far as the engine is
        /// concerned, however many bones and constraints it has.
        ///
        /// A scene converted from a NIF carries the classes and this is not consulted.
        /// It decides for a skeleton authored in a DCC tool, which has nothing to
        /// carry — the case ck-cmd covers with its <c>export_rig</c> flag.
        ///
        /// Null means work it out: a scene with ragdoll constraints in it is a
        /// skeleton, since nothing else has them.
        /// </remarks>
        public bool? SkeletonRig { get; set; }

        /// <summary>Rebuild the scene's animation stacks as NIF controller sequences.</summary>
        public bool ImportAnimation { get; set; } = true;

        /// <summary>Rebuild Havok constraints from the scene's attachment points.</summary>
        public bool ImportConstraints { get; set; } = true;

        /// <summary>The Bethesda stream version implied by the target edition.</summary>
        public uint BSVersion => LegendaryEdition ? 83u : 100u;
    }

    /// <summary>
    /// Converts an FBX scene into a NIF.
    /// </summary>
    /// <remarks>
    /// Follows `docs/fbx-nif-conversion-spec.md` §5. The root becomes a
    /// <c>BSFadeNode</c> named after the file rather than after any node in the
    /// scene, meshes become <c>NiTriShape</c> plus <c>NiTriShapeData</c>, and node
    /// names are decoded back through <see cref="NameEncoding"/>.
    ///
    /// Collision nodes (the <c>_rb</c> and <c>_sp</c> suffixes) become collision
    /// objects attached to their parent rather than children of it (spec §5.7).
    /// </remarks>
    public sealed class FbxToNif(FbxScene scene, FbxToNifOptions? options = null)
    {
        private readonly FbxScene _scene = scene;
        private readonly FbxToNifOptions _options = options ?? new FbxToNifOptions();

        private NifModel _model = null!;

        /// <summary>Diagnostics gathered during conversion.</summary>
        public List<string> Warnings { get; } = [];

        /// <summary>Builds a NIF from the scene.</summary>
        public NifModel Convert(NifXmlDatabase database)
        {
            _model = NifModel.CreateNew(database, _options.Version, _options.UserVersion, _options.BSVersion);

            // The root's kind is carried like any other node's, and matters more:
            // BSXFlags asks twice whether the root is exactly NiNode.
            // A referenced-but-unparented subtree is a scene root in FBX because FBX
            // has nowhere else to put it, and it is not one here: it must not decide
            // whether the real root collapses, nor become a child of it (§5.2.5).
            var detached = _scene.RootModels().Where(FbxNodeType.IsDetached).ToList();
            var sceneRoots = _scene.RootModels().Where(o => !FbxNodeType.IsDetached(o)).ToList();

            string rootType = sceneRoots.Count == 1 && !HasGeometry(sceneRoots[0])
                ? FbxNodeType.Read(sceneRoots[0], _model, "BSFadeNode")
                : "BSFadeNode";

            // A rig whose skins bind only to plain nodes under the root is rooted on a
            // plain node itself -- but only when the scene did not say otherwise. A
            // scene out of a NIF carries the root's class, and that is the answer; this
            // decides for one authored elsewhere, which carries nothing.
            //
            // Overriding the carried class instead was tried and is wrong: the
            // condition holds for 977 vanilla files and 402 of them are rooted on
            // `BSFadeNode`, nearly all facegen heads, whose bones are parented to the
            // root exactly like a rig's.
            if (sceneRoots.Count == 1
                && sceneRoots[0].Properties.GetString(FbxNodeType.Property).Length == 0
                && IsPlainSkeletonRoot(sceneRoots[0]))
            {
                rootType = "NiNode";
            }

            NifItem root = _model.InsertBlock(rootType);

            // Named after the file rather than after any node in the scene (§5.2).
            _model.SetString(root, "Name", _options.RootName);

            // What its class usually carries. Overridden below when the scene brought
            // real flags with it; set here because the branch that reads them only runs
            // for a scene whose single root is an empty node.
            _model.FindItem(root, "Flags")?.Value.SetCount(FbxNodeType.DefaultFlagsFor(rootType));

            // The root is built here rather than by the walk, so everything the walk
            // does for a node has to be done for it too.
            if (sceneRoots.Count == 1 && !HasGeometry(sceneRoots[0]))
            {
                FbxNodeType.ReadFields(sceneRoots[0], _model, root, "NiNode");
                FbxNodeType.ReadFlags(sceneRoots[0], _model, root);
                FbxExtraDataWriter.ReadExtraData(sceneRoots[0], _model, root, Warnings);
                FbxMultiBound.Read(sceneRoots[0], _model, root, Warnings);
                FbxNodeControllers.Read(sceneRoots[0], _model, root, Warnings, AimAt);
            }
            _nodesByName[_options.RootName] = root;
            _sceneRoot = root;

            var rootModels = sceneRoots;
            var children = new List<NifItem>();

            // FBXWrangler renames the FBX *implicit* root to the NIF root's name, so
            // a scene it produced has the NIF root as the implicit root and no Model
            // standing for it. We export a real Model instead, which is friendlier
            // in a DCC tool but leaves one to collapse on the way back. A lone root
            // Model carrying no geometry of its own is exactly that node, so it maps
            // onto the NIF root rather than becoming a redundant child of it.
            if (rootModels.Count == 1 && !HasGeometry(rootModels[0]))
            {
                FbxObject sceneNode = rootModels[0];
                _model.SetTransform(root, ReadTransform(sceneNode));

                foreach (FbxObject child in _scene.ChildrenOf(sceneNode.Id).Where(o => o.Class == "Model"))
                    ConvertModel(child, children);
            }
            else
            {
                _model.SetTransform(root, NifTransform.Identity);

                foreach (FbxObject model in rootModels)
                    ConvertModel(model, children);
            }

            AttachChildren(root, children);

            // Built after the tree so the names they will be claimed by are already
            // taken, and kept out of the root's children on purpose.
            foreach (FbxObject model in detached)
                ConvertModel(model, []);

            // Collision sitting directly under the scene root belongs to the root
            // block, and would otherwise be left unattached.
            BuildCollisionFrom(root, 0);

            // Pointers, for the same reason as skins below: what a pointer aims at is a
            // node elsewhere in the scene, and is as likely as not built after the block
            // that aims at it.
            foreach ((NifItem field, string named) in _pendingPointers)
            {
                if (_nodesByName.TryGetValue(named, out NifItem? aimed))
                    field.Value.SetLink(_model.IndexOf(aimed));
                else
                    Warnings.Add($"no node named \"{named}\" for a pointer to aim at");
            }

            _pendingPointers.Clear();

            // Skins are wired up last: a bone is a node elsewhere in the scene, so
            // they can only be resolved once the whole tree exists.
            BuildPendingSkins(root);

            // A particle system's emitter and gravity objects are nodes elsewhere in
            // the scene, which the walk may not have reached when the system was built.
            ResolveParticleLinks();

            // Constraints join two bodies, so they wait until every body exists.
            if (_options.ImportConstraints)
                _model.WriteConstraints(_scene.ReadConstraints(), _bodiesByName, Warnings);

            // Animation last of all, for the same reason: a track names the node it
            // moves, and the manager has to list blocks that already exist.
            if (_options.ImportAnimation)
                _model.WriteAnimations(root, _scene.ReadAnimations(), _nodesByName, Warnings);

            // Last, because it is an answer about the finished graph.
            //
            // Not written at all when the root is a plain `NiNode` -- a rig or a
            // fragment another file places has nothing to announce -- nor when the
            // scene came from a file that had none. See `NifToFbx.NoBsxFlagsProperty`:
            // 1,919 of the game's `BSFadeNode`-rooted files carry no BSXFlags, every
            // facegen head among them, and adding one states something they do not.
            bool carriedNone = sceneRoots.Count == 1
                && sceneRoots[0].Properties.GetString(NifToFbx.NoBsxFlagsProperty).Length > 0;

            if (root.Name != "NiNode" && !carriedNone)
                AddBsxFlags(root);

            _model.SetRoots([root]);

            // Block order is not free: a Havok block has to come before whatever
            // references it, which is the reverse of every other block, and a
            // constraint after the bodies it joins. Every mesh the game ships obeys
            // this and a file built by walking a scene does not, so the blocks are put
            // in order before the header is written -- the header records their types
            // in order, so this has to happen first.
            _model.ReorderBlocks(NifBlockOrder.Sorted(_model));
            _model.UpdateHeader();

            return _model;
        }

        /// <summary>
        /// Hangs a calculated <c>BSXFlags</c> off the root.
        /// </summary>
        /// <remarks>
        /// Every bit is a fact about the block graph -- whether it animates, collides,
        /// is a skeleton, is one collision or many -- so the value is worked out from
        /// what was just built rather than carried across from the source file, which
        /// would describe a graph this is not. See `docs/bsxflags-spec.md`.
        ///
        /// The root has to be linked before the calculation runs, because the walk
        /// behind bits 5 and 7 starts from the footer, and the block itself has to be
        /// attached afterwards so it does not appear in the graph it describes.
        /// </remarks>
        private void AddBsxFlags(NifItem root)
        {
            _model.SetRoots([root]);

            uint flags = _model.Calculate();

            NifItem bsx = _model.InsertBlock("BSXFlags");

            _model.SetString(bsx, "Name", NifBsxFlags.BlockName);
            _model.FindItem(bsx, "Integer Data")?.Value.SetCount(flags);

            // Onto the node that carries it, which is the root in all but one shape of
            // file: a master particle system wraps the node that behaves like the root,
            // and every one of the game's 84 keeps its BSXFlags on the child.
            FbxExtraDataWriter.Append(_model, NifToFbx.BsxOwnerOf(_model, root) ?? root, [bsx]);
        }

        /// <summary>
        /// Whether every bone the scene's skins use is a plain node under the root.
        /// </summary>
        /// <remarks>
        /// A file whose skin partitions name nothing but `NiNode`s, each parented to
        /// the root and to nothing else, is a rig and not a piece of scenery: there is
        /// no fading, no ordering, no LOD in it, and its root is a plain `NiNode`.
        ///
        /// Decided from the scene rather than carried, because the answer is a fact
        /// about the graph. A skin whose bones sit deeper -- a hand under a forearm
        /// under an upper arm -- is not this, and neither is one that binds to
        /// something other than a plain node.
        /// </remarks>
        private bool IsPlainSkeletonRoot(FbxObject root)
        {
            var bones = new List<FbxObject>();

            foreach (FbxObject skin in _scene.OfClass("Deformer", "Skin"))
            {
                foreach (FbxObject cluster in _scene.ChildrenOf(skin.Id))
                {
                    if (cluster is not { Class: "Deformer", SubClass: "Cluster" })
                        continue;

                    bones.AddRange(_scene.ChildrenOf(cluster.Id).Where(o => o.Class == "Model"));
                }
            }

            if (bones.Count == 0)
                return false;

            foreach (FbxObject bone in bones.DistinctBy(o => o.Id))
            {
                // A plain node, not a billboard or a marker of some kind.
                if (FbxNodeType.Read(bone, _model, "NiNode") != "NiNode")
                    return false;

                // Parented to the root and to nothing else. A cluster is a parent of
                // the bone in FBX's connection graph, so only Models count here.
                var parents = _scene.ParentsOf(bone.Id).Where(o => o.Class == "Model").ToList();

                if (parents.Count != 1 || parents[0].Id != root.Id)
                    return false;
            }

            return true;
        }

        /// <summary>Appends one block to another's extra data list.</summary>
        private void AddExtraData(NifItem block, NifItem extra)
        {
            var existing = _model.GetRefArray(block, "Extra Data List").ToList();

            NifItem? array = _model.SetArraySize(
                block, "Num Extra Data List", "Extra Data List", existing.Count + 1);

            if (array is null)
                return;

            for (int i = 0; i < existing.Count; i++)
                array.Children[i].Value.SetLink(_model.IndexOf(existing[i]));

            array.Children[existing.Count].Value.SetLink(_model.IndexOf(extra));
        }

        /// <summary>
        /// Turns one FBX Model into a NIF block, recursing into its children.
        /// </summary>
        private void ConvertModel(FbxObject model, List<NifItem> into)
        {
            string name = NameEncoding.Unsanitize(model.Name);

            // Collision bodies are leaves keyed off their name suffix, not ordinary
            // nodes, and are attached to their parent rather than listed as a child.
            if (name.EndsWith("_rb", StringComparison.Ordinal) || name.EndsWith("_sp", StringComparison.Ordinal))
            {
                _pendingCollision.Add(model);
                return;
            }

            // The mesh drawn for a multi-bound volume is a picture of it, not geometry.
            // The volume itself is rebuilt from the node's properties.
            if (FbxMultiBound.IsVolumeMesh(name))
                return;

            NifTransform transform = ReadTransform(model);

            // A mesh holder interposed on export carries no information of its own,
            // so unwrap it rather than emitting a redundant NiNode.
            bool isHolder = name.EndsWith("_support", StringComparison.Ordinal);

            var geometries = _scene.ChildrenOf(model.Id)
                .Where(o => o.Class == "Geometry")
                .ToList();

            var childModels = _scene.ChildrenOf(model.Id)
                .Where(o => o.Class == "Model")
                .ToList();

            if (isHolder && geometries.Count > 0 && childModels.Count == 0)
            {
                foreach (FbxObject geometry in geometries)
                {
                    if (BuildShape(geometry, model, transform) is { } shape)
                        into.Add(shape);
                }

                return;
            }

            // An attachment point is a marker, not a node: it says where a joint is
            // and is rebuilt as part of the body that owns it.
            if (FbxConstraintReader.IsAttachmentPoint(model))
                return;

            // A modifier node is part of the particle system above it, which built it
            // already. Left to the walk it would become an empty NiNode instead.
            if (FbxParticleWriter.IsModifierNode(model))
                return;

            // A node standing for a shape with no vertices is that shape, not a node
            // that happens to sit where one was.
            if (FbxNodeType.IsEmptyShape(model))
            {
                into.Add(BuildEmptyShape(model, name, transform));
                return;
            }

            // A node carrying a particle system becomes the system rather than a
            // NiNode: it is the same node, and emitting both would leave the system
            // parented under a copy of itself.
            // A node is rebuilt as whatever kind of node it was. FBX has one kind
            // and NIF has a dozen, and they differ in what the engine does with them
            // rather than in where they sit.
            // Any NiAVObject, not only a NiNode. A NiCamera is a node in the scene
            // graph and not a NiNode in the schema -- it inherits NiAVObject directly,
            // has no Children of its own, and was coming back as a plain NiNode with
            // its frustum, viewport and LOD adjust gone.
            string blockType = FbxNodeType.Read(model, _model, "NiNode", "NiAVObject");

            // Geometry is built on the mesh path, from a mesh. A node claiming to be a
            // shape would arrive here with no vertices to be one from.
            if (_model.Database.Inherits(blockType, "NiTriBasedGeom")
                || _model.Database.Inherits(blockType, "BSTriShape"))
            {
                blockType = "NiNode";
            }

            NifItem node = NifParticleWriter.HasParticleSystem(model)
                ? _model.WriteParticleSystem(_scene, model, name, Warnings, _pendingParticleLinks)
                  ?? _model.InsertBlock(blockType)
                : _model.InsertBlock(blockType);

            _model.SetString(node, "Name", FbxNodeType.ReadName(model, name));
            FbxNodeType.ReadFlags(model, _model, node);
            _model.SetTransform(node, transform);

            // Keyed by the FBX name rather than the NIF one: that is what an animation
            // track names, and a file's unnamed nodes would otherwise share one key.
            _nodesByName[name] = node;

            // A particle system is a shape and carries a shader and an alpha property
            // like any other; it just has no geometry for them to hang off.
            if (NifParticleWriter.HasParticleSystem(model))
                BuildMaterial(node, model);

            // Whatever the class adds to a plain NiNode: an ordered node's sort
            // bound, a value node's value. Carrying the class without them leaves a
            // block that is the right kind and says nothing. A particle system is left
            // to its own carrier, which owns every field it has.
            if (!NifParticleWriter.HasParticleSystem(model))
                FbxNodeType.ReadFields(model, _model, node, "NiNode");


            FbxExtraDataWriter.ReadExtraData(model, _model, node, Warnings);
            FbxMultiBound.Read(model, _model, node, Warnings);

            // Controllers that animate nothing. A particle system rebuilds its own
            // through its carrier, which owns the whole system.
            if (!NifParticleWriter.HasParticleSystem(model))
                FbxNodeControllers.Read(model, _model, node, Warnings, AimAt);

            // Collision found under this node attaches to it rather than becoming a
            // child, so collect it before recursing into the real children.
            int collisionMark = _pendingCollision.Count;

            var nodeChildren = new List<NifItem>();

            foreach (FbxObject geometry in geometries)
            {
                if (BuildShape(geometry, model, NifTransform.Identity) is { } shape)
                    nodeChildren.Add(shape);
            }

            foreach (FbxObject child in childModels)
                ConvertModel(child, nodeChildren);

            AttachChildren(node, nodeChildren);
            BuildCollisionFrom(node, collisionMark);
            into.Add(node);
        }

        /// <summary>Collision bodies seen since <paramref name="mark"/>, awaiting a node to attach to.</summary>
        private readonly List<FbxObject> _pendingCollision = [];

        /// <summary>The block every compressed mesh shape points back at.</summary>
        /// <remarks>
        /// Set as soon as the root exists, since the walk that builds the shapes runs
        /// well before the roots are recorded in the footer.
        /// </remarks>
        private NifItem? _sceneRoot;

        /// <summary>The rigid bodies built so far, by the node name they came from.</summary>
        private readonly Dictionary<string, NifItem> _bodiesByName = new(StringComparer.Ordinal);

        /// <summary>Particle links naming a node, waiting for that node to exist.</summary>
        private readonly List<NifParticleWriter.PendingParticleLink> _pendingParticleLinks = [];

        /// <summary>
        /// Points a particle system's links at the nodes they name.
        /// </summary>
        /// <remarks>
        /// An emitter that has lost its emitter object emits from the origin and a
        /// gravity modifier that has lost its gravity object pulls towards it, and
        /// neither shows up as anything but the effect being wrong.
        /// </remarks>
        private void ResolveParticleLinks()
        {
            foreach (NifParticleWriter.PendingParticleLink pending in _pendingParticleLinks)
            {
                if (_nodesByName.TryGetValue(pending.TargetName, out NifItem? target))
                    pending.Link.Value.SetLink(_model.IndexOf(target));
                else
                    Warnings.Add(
                        $"{pending.Context}: no node named \"{pending.TargetName}\", "
                        + "the particle system's reference to it is dropped");
            }

            _pendingParticleLinks.Clear();
        }

        /// <summary>
        /// Builds the collision object for a node from any bodies found beneath it.
        /// </summary>
        /// <remarks>
        /// A NIF node holds exactly one collision object, so when several bodies sit
        /// under one node only the first becomes it and the rest are reported. That
        /// is rare enough in practice to be worth saying rather than silently
        /// merging or dropping.
        /// </remarks>
        private void BuildCollisionFrom(NifItem node, int mark)
        {
            if (_pendingCollision.Count <= mark)
                return;

            var bodies = _pendingCollision.GetRange(mark, _pendingCollision.Count - mark);
            _pendingCollision.RemoveRange(mark, _pendingCollision.Count - mark);

            for (int i = 1; i < bodies.Count; i++)
                Warnings.Add($"{NameEncoding.Unsanitize(bodies[i].Name)}: only one collision body per node, ignored");

            if (BuildRigidBody(bodies[0]) is { } collision)
            {
                _model.SetRef(node, "Collision Object", collision);
                _model.SetRef(collision, "Target", node);
            }
        }

        /// <summary>
        /// Builds a collision object and its body from an <c>_rb</c> or <c>_sp</c> node.
        /// </summary>
        private NifItem? BuildRigidBody(FbxObject bodyNode)
        {
            string name = NameEncoding.Unsanitize(bodyNode.Name);
            bool isPhantom = name.EndsWith("_sp", StringComparison.Ordinal);

            NifItem? shape = BuildShapeFrom(bodyNode, name);

            if (shape is null)
            {
                Warnings.Add($"{name}: no collision shape found beneath it");
                return null;
            }

            if (isPhantom)
            {
                NifItem phantomCollision = _model.InsertBlock("bhkSPCollisionObject");
                NifItem phantom = _model.InsertBlock("bhkSimpleShapePhantom");

                FbxCollisionObject.Read(bodyNode, _model, phantomCollision);

                _model.SetRef(phantom, "Shape", shape);
                _model.SetRef(phantomCollision, "Body", phantom);

                // A phantom is a `bhkWorldObject` and carries a collision filter of its
                // own, exactly as a rigid body does: the layer is what decides which
                // objects it notices. The export writes it -- `FbxRigidBodyInfo.Write`
                // is not guarded against phantoms -- and this branch returned before
                // anything read it back, so every rebuilt phantom sat on nif.xml's
                // `SKYL_STATIC` whatever the file said.
                FbxCollisionMaterial.ApplyLayer(_model, phantom, FbxRigidBodyInfo.LayerOf(bodyNode));

                FbxCollisionMaterial.ApplyFilterFlags(
                    _model, phantom, FbxRigidBodyInfo.FilterFlagsOf(bodyNode));

                // Which broad-phase list Havok keeps it in. A phantom is the one thing
                // that is not what nif.xml assumes: the field defaults to
                // BROAD_PHASE_ENTITY, which is right for all 2,406 vanilla rigid bodies
                // and wrong for all 48 phantoms. So only this one is written, and the
                // bodies keep the schema's answer rather than being told it again.
                SetEnum(phantom, @"World Object Info\Broad Phase Type",
                    "BroadPhaseType", "BROAD_PHASE_PHANTOM");

                return phantomCollision;
            }

            // A blend collision object is what makes a file a skeleton, so which class
            // this is decides what the engine thinks the whole file is. Carried when
            // the scene came from a NIF; otherwise it follows from whether this is a
            // rig at all.
            NifItem collision = _model.InsertBlock(
                FbxCollisionObject.TypeOf(
                    bodyNode, _model,
                    IsSkeletonRig() ? "bhkBlendCollisionObject" : "bhkCollisionObject"));

            // How the body and its node keep in step -- local transform, follow on
            // animation -- is not visible in the shape and cannot be derived from it.
            FbxCollisionObject.Read(bodyNode, _model, collision);

            // A blend object left at zero gain is a bone that does not follow, so a
            // rig built from a scene that carried no gains gets the ones ck-cmd uses.
            if (_model.BlockInherits(collision, "bhkBlendCollisionObject"))
            {
            }

            FbxCollisionObject.ReadGains(bodyNode, _model, collision);

            // bhkRigidBodyT applies its own transform; the plain body ignores it,
            // which is what a skeleton's bodies want since their bones place them.
            NifItem body = _model.InsertBlock(
                FbxCollisionObject.BodyTypeOf(
                    bodyNode, _model, IsSkeletonRig() ? "bhkRigidBody" : "bhkRigidBodyT"));

            // A constraint names the bodies it joins by the node they came from.
            _bodiesByName[name] = body;

            _model.SetRef(body, "Shape", shape);

            // The layer the body was on. It travelled out of the file, across the scene
            // and back in, and was then used only to decide whether this is a static --
            // never written to the body it came from. So every rebuilt body kept the
            // field's default, and one that had been on a named layer came back on
            // whatever that default is.
            //
            // It is the input to the motion profile as well, so losing it takes the
            // motion system, the deactivation and the quality with it. Applied before
            // WriteStaticMotion, which reads the layer to decide the profile.
            FbxCollisionMaterial.ApplyLayer(_model, body, FbxRigidBodyInfo.LayerOf(bodyNode));
            FbxCollisionMaterial.ApplyFilterFlags(_model, body, FbxRigidBodyInfo.FilterFlagsOf(bodyNode));

            WriteBodyTransform(body, bodyNode);
            WriteMotionProfile(body, bodyNode);
            WriteMassProperties(body, shape, bodyNode);
            WriteSimulationScalars(body, bodyNode);

            _model.SetRef(collision, "Body", body);

            return collision;
        }

        /// <summary>
        /// Writes a body's placement, converting Skyrim units back to Havok metres.
        /// </summary>
        private void WriteBodyTransform(NifItem body, FbxObject bodyNode)
        {
            // The body's placement is a world transform and the node carrying it may
            // hang off a bone, so it is the node's global transform that is written.
            NifTransform transform = FbxGlobalTransform.Of(_scene, bodyNode);
            NifVector3 t = transform.Translation;

            _model.FindItem(body, @"Rigid Body Info\Translation")?.Value.Set(new NifVector4(
                t.X * ShapeTessellator.BhkScaleFactorInverse,
                t.Y * ShapeTessellator.BhkScaleFactorInverse,
                t.Z * ShapeTessellator.BhkScaleFactorInverse,
                0f));

            _model.FindItem(body, @"Rigid Body Info\Rotation")?.Value.Set(transform.ToQuaternion());
        }

        /// <summary>
        /// Gives a body its mass and the inertia tensor that follows from it.
        /// </summary>
        /// <remarks>
        /// The two are not alike. The mass is authored and is carried across; the
        /// tensor is a consequence of that mass and the shape, and is computed, because
        /// ck-cmd's is computed too -- it asks Havok, and this arrives at the same
        /// numbers the files ck-cmd generated hold.
        ///
        /// A static keeps neither. Its layer is the whole of the decision, and a static
        /// carrying a mass is treated as movable, which is how scenery ends up falling
        /// through the world -- so the carried value is dropped rather than trusted.
        /// </remarks>
        private void WriteMassProperties(NifItem body, NifItem shape, FbxObject bodyNode)
        {
            if (FbxRigidBodyInfo.IsStatic(FbxRigidBodyInfo.LayerOf(bodyNode)))
                return;

            if (FbxRigidBodyInfo.MassOf(bodyNode) is not { } mass || mass <= 0f)
                return;

            SetFloat(body, @"Rigid Body Info\Mass", mass);

            if (InertiaOf(shape, mass) is not { } tensor)
                return;

            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m11", tensor.M11);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m12", tensor.M12);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m13", tensor.M13);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m21", tensor.M21);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m22", tensor.M22);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m23", tensor.M23);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m31", tensor.M31);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m32", tensor.M32);
            SetFloat(body, @"Rigid Body Info\Inertia Tensor\m33", tensor.M33);
        }

        /// <summary>
        /// Restores the simulation scalars the body was authored with.
        /// </summary>
        /// <remarks>
        /// Friction, restitution, damping, penetration depth and the velocity ceilings
        /// are settings, not consequences of the geometry: nothing about a rebuilt hull
        /// says how slippery it is. They are carried across the scene verbatim and put
        /// back, which is the same treatment the mass gets and for the same reason.
        ///
        /// A body that arrives without them -- one authored in a DCC tool rather than
        /// converted from a NIF -- gets Bethesda's commonest value for each, which is
        /// not always nif.xml's default: the damping pair is quantised onto a 1/1024
        /// grid in every vanilla file, and the penetration depth a static wants is not
        /// the one a mover wants. Written explicitly rather than left to the block's
        /// own initialisation, so the value in the file is one this code chose.
        /// </remarks>
        private void WriteSimulationScalars(NifItem body, FbxObject bodyNode)
        {
            bool isStatic = FbxRigidBodyInfo.IsStatic(FbxRigidBodyInfo.LayerOf(bodyNode));

            foreach (FbxRigidBodyInfo.Scalar scalar in FbxRigidBodyInfo.Scalars)
            {
                SetFloat(
                    body,
                    $@"Rigid Body Info\{scalar.Field}",
                    FbxRigidBodyInfo.ScalarOf(bodyNode, scalar, isStatic));
            }
        }

        /// <summary>The tensor for a rebuilt shape, or null for one with no formula.</summary>
        private NifMatrix33? InertiaOf(NifItem shape, float mass) => shape.Name switch
        {
            "bhkBoxShape" => HavokInertia.Box(
                mass, _model.FindItem(shape, "Dimensions")?.Value.Get<NifVector3>() ?? default),

            "bhkSphereShape" => HavokInertia.Sphere(
                mass, _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            "bhkCapsuleShape" => HavokInertia.Capsule(
                mass,
                _model.FindItem(shape, "First Point")?.Value.Get<NifVector3>() ?? default,
                _model.FindItem(shape, "Second Point")?.Value.Get<NifVector3>() ?? default,
                _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f),

            "bhkConvexVerticesShape" => HavokInertia.Convex(mass, HullOf(shape)),

            _ => null
        };

        private MeshGeometry HullOf(NifItem shape)
        {
            var points = new List<NifVector3>();

            if (_model.FindItem(shape, "Vertices") is { } vertices)
            {
                foreach (NifItem vertex in vertices.Children)
                {
                    NifVector4 v = vertex.Value.Get<NifVector4>();
                    points.Add(new NifVector3(v.X, v.Y, v.Z));
                }
            }

            return ShapeTessellator.ConvexHull(points);
        }

        /// <summary>
        /// Applies how Havok is to simulate the body (spec §5.7).
        /// </summary>
        /// <remarks>
        /// This was `WriteStaticMotion`, and it wrote the static profile onto every
        /// body it was handed -- the call site's comment claimed it read the layer to
        /// decide, and it took no layer at all. So ck-cmd's three-way split collapsed
        /// to its third arm: a biped's bodies came back BOX_STABILIZED/INVALID/OFF
        /// where the file had BOX_INERTIA/FIXED/LOW, and with them went bit 6 of
        /// BSXFlags, which asks whether any body is dynamic.
        ///
        /// The profile is carried when the scene has one, because no layer predicts it
        /// better than 88% -- see <see cref="FbxRigidBodyInfo.DefaultProfile"/> for the
        /// distribution and for what a body carrying nothing gets instead.
        ///
        /// Only a static is stripped of its mass and inertia. Leaving a mass on one
        /// makes Havok treat it as movable, which is how a piece of scenery ends up
        /// falling through the world; taking it off anything else is what left every
        /// rebuilt ragdoll weightless.
        /// </remarks>
        private void WriteMotionProfile(NifItem body, FbxObject bodyNode)
        {
            string layer = FbxRigidBodyInfo.LayerOf(bodyNode);
            FbxRigidBodyInfo.MotionProfile profile = FbxRigidBodyInfo.ProfileOf(bodyNode, layer);

            SetEnum(body, @"Rigid Body Info\Motion System", "Motion System", profile.MotionSystem);
            SetEnum(body, @"Rigid Body Info\Quality Type", "Motion Quality", profile.QualityType);

            SetEnum(
                body, @"Rigid Body Info\Solver Deactivation",
                "Solver Deactivation", profile.SolverDeactivation);

            if (!FbxRigidBodyInfo.IsStatic(layer))
                return;

            SetFloat(body, @"Rigid Body Info\Mass", 0f);

            // Havok wants the tensor cleared, not merely small.
            for (int row = 1; row <= 3; row++)
            {
                for (int column = 1; column <= 4; column++)
                    SetFloat(body, $@"Rigid Body Info\Inertia Tensor\m{row}{column}", 0f);
            }
        }

        /// <summary>
        /// Sets an enum field by option name, so the intent survives even though the
        /// numeric values differ between enums.
        /// </summary>
        private void SetEnum(NifItem block, string path, string enumType, string optionName)
        {
            NifItem? item = _model.FindItem(block, path);

            if (item is null)
                return;

            if (_model.Database.TryGetEnumOptionValue(item.Type, optionName, out uint value)
                || _model.Database.TryGetEnumOptionValue(enumType, optionName, out value))
            {
                item.Value.SetCount(value);
            }
        }

        /// <summary>
        /// Finds the shape beneath a body node and fits a Havok primitive to it,
        /// choosing which by the node's name suffix.
        /// </summary>
        /// <param name="only">
        /// Build from this child alone, so a container can take its children one at a
        /// time rather than stopping at the first that yields a shape.
        /// </param>
        private NifItem? BuildShapeFrom(
            FbxObject parent, string parentName, int depth = 0, FbxObject? only = null)
        {
            if (depth > 16)
            {
                Warnings.Add($"{parentName}: collision nodes nest too deeply, stopping");
                return null;
            }

            IEnumerable<FbxObject> candidates = only is not null
                ? [only]
                : _scene.ChildrenOf(parent.Id).Where(o => o.Class == "Model");

            foreach (FbxObject child in candidates)
            {
                string name = NameEncoding.Unsanitize(child.Name);

                // A container holds a tree, and the tree is the shape. ck-cmd
                // rebuilds these from the Havok body it fits to the geometry; there is
                // no Havok here, but the FBX node structure says the same thing and
                // says it more directly.
                if (IsPassThrough(name))
                {
                    if (BuildShapeFrom(child, name, depth + 1) is not { } inner)
                        continue;

                    // The compressed-mesh path builds its own tree, because the same
                    // Havok call that chunks the mesh returns one. Wrapping it again
                    // would give the body two.
                    if (inner.Name == "bhkMoppBvTreeShape")
                        return inner;

                    return BuildMoppTree(child, inner, name) ?? inner;
                }

                if (ContainerFor(name) is { } suffixed)
                {
                    // The suffix narrows it to a family; the carried class says which
                    // member, since a transform shape and a convex transform shape
                    // share a suffix.
                    string container = FbxNodeType.Read(child, _model, suffixed, "bhkShape");

                    if (BuildContainer(container, child, name, depth) is { } rebuilt)
                        return rebuilt;

                    continue;
                }

                if (ReadShapePoints(child) is not { Count: > 0 } points)
                    continue;

                // The suffix decides the primitive. Guessing from the geometry would
                // silently swap a sphere for a box: their tessellations are not
                // reliably distinguishable.
                NifItem? built = null;

                if (name.EndsWith("_box", StringComparison.Ordinal))
                    built = BuildBox(points);
                else if (name.EndsWith("_sphere", StringComparison.Ordinal))
                    built = BuildSphere(points);
                else if (name.EndsWith("_capsule", StringComparison.Ordinal))
                    built = BuildCapsule(points, child);
                else if (name.EndsWith("_cylinder", StringComparison.Ordinal))
                    built = BuildCylinder(points, child);
                else if (name.EndsWith("_convex", StringComparison.Ordinal))
                    built = BuildConvex(points, child);
                else if (name.EndsWith("_plane", StringComparison.Ordinal))
                    built = BuildPlane(points);
                else if (name.EndsWith("_strips", StringComparison.Ordinal))
                    built = BuildTriStrips(child, name);
                else if (name.EndsWith("_mesh", StringComparison.Ordinal))
                    built = BuildCompressedMesh(child, name);

                if (built is null)
                    continue;

                // Size comes back from the geometry; the material cannot, because
                // nothing in the triangles says wood rather than stone.
                ReadCollisionMaterial(built, child, name);

                return built;
            }

            return null;
        }

        /// <summary>
        /// Rebuilds a MOPP tree over the shape it indexes, generating its code.
        /// </summary>
        /// <remarks>
        /// The code is never carried. It is a Havok-proprietary index over a specific
        /// arrangement of triangles, and a round trip refits every shape it covers, so
        /// what came in describes a shape that no longer exactly exists. Regenerating
        /// it against what was actually rebuilt is the only way it can be right — and
        /// the measurement that settled it is worth recording: regenerated code never
        /// matches vanilla byte for byte, on either the compressed-mesh path or this
        /// one, so carrying bytes would only be preserving the *appearance* of
        /// fidelity.
        ///
        /// mopper builds a tree over triangles. A tree over a collection of primitives
        /// -- a `bhkListShape` of capsules, which 86 of the game's meshes have -- has
        /// leaves that are child indices rather than triangle indices, and no backend
        /// can produce one. Those pass through, with a warning that says so, rather
        /// than getting a tree whose leaves point at children that do not exist.
        /// </remarks>
        private NifItem? BuildMoppTree(FbxObject node, NifItem inner, string name)
        {
            if (MoppGenerator.Resolve() is not { } generator)
            {
                Warnings.Add(
                    $"{name}: the MOPP tree needs generating. {MoppGenerator.DescribeUnavailability()}");

                return null;
            }

            MoppResult? built;

            if (TriangleGeometryUnder(node) is { Triangles.Count: > 0 } mesh)
            {
                built = generator.GenerateSimpleMesh(mesh.Vertices, mesh.Triangles);
            }
            else if (MoppShapeWriter.Describe(_model, inner) is { Length: > 0 } description)
            {
                // A tree over a collection of primitives. Its leaves are child indices
                // rather than triangle indices, so a tessellation cannot stand in for
                // one -- the backend has to build the primitives as Havok shapes and
                // index those, which is what ck-cmd's HKXWrangler does.
                built = generator.GenerateCollection(description);
            }
            else
            {
                Warnings.Add(
                    $"{name}: a MOPP tree over {inner.Name} is not one this build can "
                    + "describe to the generator, so the tree is dropped");

                return null;
            }

            if (built is null)
            {
                Warnings.Add($"{name}: MOPP generation failed, the tree is dropped{Said(generator)}");
                return null;
            }

            ReportBackendTrouble(generator, name);

            NifItem mopp = _model.InsertBlock("bhkMoppBvTreeShape");

            _model.SetRef(mopp, "Shape", inner);
            WriteMoppCode(mopp, built);

            return mopp;
        }

        /// <summary>
        /// The triangles a MOPP tree would index, gathered from beneath a node.
        /// </summary>
        /// <remarks>
        /// One mesh, however many nodes deep it sits: a tree wraps one shape, and the
        /// only shapes it can index this way are the triangle ones.
        /// </remarks>
        private MeshGeometry? TriangleGeometryUnder(FbxObject node, int depth = 0)
        {
            if (depth > 8)
                return null;

            foreach (FbxObject child in _scene.ChildrenOf(node.Id).Where(o => o.Class == "Model"))
            {
                string name = NameEncoding.Unsanitize(child.Name);

                if (name.EndsWith("_strips", StringComparison.Ordinal)
                    || name.EndsWith("_mesh", StringComparison.Ordinal))
                {
                    if (ReadCollisionMesh(child) is { Triangles.Count: > 0 } mesh)
                        return mesh;
                }

                if (TriangleGeometryUnder(child, depth + 1) is { } deeper)
                    return deeper;
            }

            return null;
        }

        /// <summary>
        /// Whether a container is walked through rather than rebuilt.
        /// </summary>
        /// <remarks>
        /// Only the MOPP tree. Its code has to be generated -- an empty wrapper cannot
        /// even be written -- and the compressed mesh path makes one properly when it
        /// needs it, so nothing is lost by passing through here.
        /// </remarks>
        private static bool IsPassThrough(string name) =>
            name.EndsWith("_mopp", StringComparison.Ordinal);

        /// <summary>The block a container node stands for, or null when it is not one.</summary>
        private static string? ContainerFor(string name) => name switch
        {
            _ when name.EndsWith("_convex_list", StringComparison.Ordinal) => "bhkConvexListShape",
            _ when name.EndsWith("_list", StringComparison.Ordinal) => "bhkListShape",

            _ when name.EndsWith("_transform", StringComparison.Ordinal) => "bhkTransformShape",
            _ => null
        };

        /// <summary>
        /// Rebuilds a container shape and everything under it.
        /// </summary>
        /// <remarks>
        /// The old behaviour was to walk through a container and return the first leaf
        /// it found, on the grounds that Havok would rebuild the tree. Havok is not
        /// here, and a list shape with six boxes came back as one box -- five sixths of
        /// the collision gone, with the shape that remained the right shape.
        ///
        /// A list keeps every child. A MOPP tree and a transform shape wrap one, and a
        /// MOPP's data is regenerated rather than carried, so the wrapper is all there
        /// is to rebuild.
        /// </remarks>
        private NifItem? BuildContainer(string type, FbxObject node, string name, int depth)
        {
            var children = new List<NifItem>();

            foreach (FbxObject child in _scene.ChildrenOf(node.Id).Where(o => o.Class == "Model"))
            {
                // One child at a time, so a list keeps all of them rather than the
                // first: BuildShapeFrom stops at the first shape it can build.
                if (BuildShapeFrom(node, name, depth + 1, only: child) is { } built)
                    children.Add(built);
            }

            if (children.Count == 0)
                return null;

            if (!_model.KnowsBlock(type))
                return children[0];

            NifItem container = _model.InsertBlock(type);

            if (type is "bhkListShape" or "bhkConvexListShape")
            {
                if (_model.SetArraySize(container, "Num Sub Shapes", "Sub Shapes", children.Count)
                    is { } subShapes)
                {
                    for (int i = 0; i < children.Count && i < subShapes.Children.Count; i++)
                        subShapes.Children[i].Value.SetLink(_model.IndexOf(children[i]));
                }

                ApplyContainerMaterial(container, node, children[0]);
            }
            else
            {
                _model.SetRef(container, "Shape", children[0]);

                // A transform shape is the transform: it is how the game puts a box or
                // a sphere anywhere but the body's origin, since neither block has a
                // centre of its own. Nothing wrote it, so every such shape came back at
                // the origin and the collision sat where the object was not.
                if (type is "bhkTransformShape" or "bhkConvexTransformShape")
                    WriteHavokTransform(container, ReadTransform(node));

                ApplyContainerMaterial(container, node, children[0]);

                if (children.Count > 1)
                {
                    Warnings.Add(
                        $"{name}: a {type} holds one shape and this node has {children.Count}, "
                        + "the rest are dropped");
                }
            }

            return container;
        }

        /// <summary>
        /// Gives a container shape its material: the one it carried, or its child's.
        /// </summary>
        /// <remarks>
        /// A container carries a material as well as wrapping shapes that carry theirs,
        /// so it is carried -- see `NifToFbx.ContainerMaterialProperty`. The child's is
        /// the fallback for a scene that has none, and it is a good one: across a
        /// 2,500-mesh sample a transform shape agrees with its child in all 319 cases
        /// with a child to ask, and a list in 133 of 141.
        ///
        /// The fallback cannot answer at all when the child has no material field --
        /// a `bhkMoppBvTreeShape` has none -- which is why the carry exists.
        /// </remarks>
        private void ApplyContainerMaterial(NifItem container, FbxObject node, NifItem child)
        {
            if (node.Properties.GetString(NifToFbx.ContainerMaterialProperty) is { Length: > 0 } carried
                && FbxCollisionMaterial.Apply(_model, container, carried))
            {
                return;
            }

            if (FbxCollisionMaterial.MaterialField(child) is { } wrapped)
                FbxCollisionMaterial.MaterialField(container)?.Value.SetCount(wrapped.Value.ToUInt());
        }

        /// <summary>
        /// Writes a node's placement into the matrix a transform shape carries.
        /// </summary>
        /// <remarks>
        /// The mirror of <c>NifToFbx.HavokTransformOf</c>. Stored row by row with the
        /// translation in the fourth row, and the fourth column left at zero — which is
        /// what the game writes, including the zero in the corner where a homogeneous
        /// matrix would have a one. Only the translation carries units.
        /// </remarks>
        private void WriteHavokTransform(NifItem shape, NifTransform transform)
        {
            if (_model.FindItem(shape, "Transform") is not { } item)
                return;

            NifMatrix33 r = transform.Rotation;

            item.Value.Set(new NifMatrix44
            {
                M11 = r.M11, M12 = r.M12, M13 = r.M13,
                M21 = r.M21, M22 = r.M22, M23 = r.M23,
                M31 = r.M31, M32 = r.M32, M33 = r.M33,
                M41 = transform.Translation.X / ShapeTessellator.BhkScaleFactor,
                M42 = transform.Translation.Y / ShapeTessellator.BhkScaleFactor,
                M43 = transform.Translation.Z / ShapeTessellator.BhkScaleFactor
            });
        }

        /// <summary>
        /// The vertices of a collision node's mesh, converted back to Havok metres.
        /// </summary>
        private List<NifVector3>? ReadShapePoints(FbxObject node)
        {
            FbxObject? geometry = _scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry");

            if (geometry is null)
                return null;

            MeshGeometry? mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options
            {
                // Collision geometry carries no UVs, so the flips are irrelevant.
                InvertU = false,
                InvertV = false
            });

            if (mesh is null || mesh.IsEmpty)
                return null;

            var points = new List<NifVector3>(mesh.Vertices.Count);

            foreach (NifVector3 v in mesh.Vertices)
            {
                points.Add(new NifVector3(
                    v.X * ShapeTessellator.BhkScaleFactorInverse,
                    v.Y * ShapeTessellator.BhkScaleFactorInverse,
                    v.Z * ShapeTessellator.BhkScaleFactorInverse));
            }

            return points;
        }

        /// <summary>
        /// Restores the Havok material from the FBX material on the collision mesh.
        /// </summary>
        /// <remarks>
        /// The export names the material after the enum, as ck-cmd does, so a shape
        /// that came from a NIF arrives with its material spelled out and a shape
        /// authored in a DCC tool arrives with whatever the artist named it. An
        /// unrecognised name is reported rather than silently left as stone: the
        /// material decides footstep sound and impact response, and a wrong one is not
        /// visible in the mesh.
        /// </remarks>
        private void ReadCollisionMaterial(NifItem shape, FbxObject holder, string name)
        {
            // A chunked mesh keeps its materials in a table on its data block, one per
            // chunk, and WriteChunkMaterials has already filled it from these same FBX
            // materials. The shape itself has no material field to apply one to, so
            // asking would only produce a warning about a material that did travel.
            if (shape.Name == "bhkCompressedMeshShape" || shape.Name == "bhkMoppBvTreeShape")
                return;

            FbxObject? material = _scene.ChildrenOf(holder.Id)
                .FirstOrDefault(o => o.Class == "Material" && !FbxLodSizes.IsLevelMaterial(o.Name));

            if (material is null)
                return;

            string spelled = NameEncoding.Unsanitize(material.Name);

            if (spelled.Length == 0 || FbxCollisionMaterial.Apply(_model, shape, spelled))
                return;

            Warnings.Add(
                $"{name}: \"{spelled}\" is not a Skyrim Havok material, "
                + "the shape keeps the default");
        }

        private NifItem BuildBox(IReadOnlyList<NifVector3> points)
        {
            (_, NifVector3 half) = ShapeFitter.FitBox(points);

            NifItem shape = _model.InsertBlock("bhkBoxShape");
            _model.FindItem(shape, "Dimensions")?.Value.Set(half);
            SetFloat(shape, "Radius", MathF.Min(half.X, MathF.Min(half.Y, half.Z)));

            return shape;
        }

        private NifItem BuildSphere(IReadOnlyList<NifVector3> points)
        {
            (_, float radius) = ShapeFitter.FitSphere(points);

            NifItem shape = _model.InsertBlock("bhkSphereShape");
            SetFloat(shape, "Radius", radius);

            return shape;
        }

        /// <summary>
        /// Builds a capsule, keeping the end order the source had.
        /// </summary>
        /// <remarks>
        /// The fit cannot recover which end was `First Point` -- a capsule's cloud is
        /// symmetric about its middle -- so a capsule that came from a NIF carries the
        /// direction it had, and the fitted pair is turned to match. Without it, a
        /// capsule authored the way 94.9% of Skyrim's are came back with its two points
        /// exchanged: the same volume, and not the same file.
        /// </remarks>
        private NifItem BuildCapsule(IReadOnlyList<NifVector3> points, FbxObject node)
        {
            (NifVector3 first, NifVector3 second, float radius) =
                ShapeFitter.FitCapsule(points, CarriedAxis(node));

            NifItem shape = _model.InsertBlock("bhkCapsuleShape");
            _model.FindItem(shape, "First Point")?.Value.Set(first);
            _model.FindItem(shape, "Second Point")?.Value.Set(second);
            SetFloat(shape, "Radius", radius);
            SetFloat(shape, "Radius 1", radius);
            SetFloat(shape, "Radius 2", radius);

            return shape;
        }

        /// <summary>
        /// Rebuilds a cylinder, whose ends are discs rather than hemispheres.
        /// </summary>
        /// <remarks>
        /// Havok stores both points as four-component vectors, and the fourth
        /// component is not padding: it holds the radius again, and Havok reads it.
        /// </remarks>
        /// <summary>The axis carried on a node, or null when it carried none.</summary>
        private static NifVector3? CarriedAxis(FbxObject node)
        {
            string text = node.Properties.GetString(NifToFbx.ShapeAxisProperty);

            if (text.Length == 0)
                return null;

            string[] parts = text.Split(',');

            if (parts.Length != 3)
                return null;

            float[] axis = new float[3];

            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(
                        parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out axis[i]))
                    return null;
            }

            return new NifVector3(axis[0], axis[1], axis[2]);
        }

        private NifItem BuildCylinder(IReadOnlyList<NifVector3> points, FbxObject node)
        {
            (NifVector3 first, NifVector3 second, float radius) =
                ShapeFitter.FitCylinder(points, CarriedAxis(node));

            NifItem shape = _model.InsertBlock("bhkCylinderShape");

            _model.FindItem(shape, "Vertex A")?.Value.Set(
                new NifVector4(first.X, first.Y, first.Z, radius));

            _model.FindItem(shape, "Vertex B")?.Value.Set(
                new NifVector4(second.X, second.Y, second.Z, radius));

            SetFloat(shape, "Cylinder Radius", radius);
            SetFloat(shape, "Radius", radius);

            return shape;
        }

        /// <summary>
        /// Builds a <c>bhkCompressedMeshShape</c>, which needs Havok to chunk and
        /// quantise the mesh and to build the MOPP tree that indexes it.
        /// </summary>
        /// <remarks>
        /// This is the one shape that cannot be produced from open code: the chunk
        /// layout, the transforms and the MOPP tree all come out of the same Havok
        /// pass, so they have to be generated together by mopper. Without it, the
        /// shape is reported rather than approximated — a mesh collision fitted to a
        /// primitive would be silently wrong in a way that only shows up in game.
        /// </remarks>
        /// <summary>Rebuilds a plane and the box that bounds it.</summary>
        private NifItem BuildPlane(IReadOnlyList<NifVector3> points)
        {
            (NifVector3 normal, float constant, NifVector3 centre, NifVector3 half) =
                ShapeFitter.FitPlane(points);

            NifItem shape = _model.InsertBlock("bhkPlaneShape");

            _model.FindItem(shape, "Plane Normal")?.Value.Set(normal);
            SetFloat(shape, "Plane Constant", constant);

            _model.FindItem(shape, "AABB Center")?.Value.Set(
                new NifVector4(centre.X, centre.Y, centre.Z, 0f));

            _model.FindItem(shape, "AABB Half Extents")?.Value.Set(
                new NifVector4(half.X, half.Y, half.Z, 0f));

            return shape;
        }

        /// <summary>Four comma-separated floats, or null when there are not four.</summary>
        private static NifVector4? ParseVector4(string text)
        {
            if (text.Length == 0)
                return null;

            string[] parts = text.Split(',');

            if (parts.Length != 4)
                return null;

            var values = new float[4];

            for (int i = 0; i < 4; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }

            return new NifVector4(values[0], values[1], values[2], values[3]);
        }

        /// <summary>
        /// Rebuilds a <c>bhkNiTriStripsShape</c>, the LE-era mesh collision.
        /// </summary>
        /// <remarks>
        /// Its geometry is real triangles rather than the chunked form a compressed
        /// mesh uses, so no Havok is needed to build it — only to index it, which is
        /// the MOPP tree above and is generated separately (§5.7.3).
        ///
        /// The strips are written one triangle each. A strip is a compression of the
        /// index list and nothing reads it back as anything else; a three-point strip
        /// per triangle says the same mesh, and reconstructing longer runs would be
        /// guessing at how the original tool happened to split them.
        /// </remarks>
        private NifItem? BuildTriStrips(FbxObject node, string name)
        {
            if (ReadCollisionMesh(node) is not { } mesh || mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{name}: strips collision node has no geometry");
                return null;
            }

            NifItem shape = _model.InsertBlock("bhkNiTriStripsShape");

            // Both written only when the scene carried them. nif.xml's defaults -- 0.1
            // and (1,1,1,0) -- are what these mean when nothing said otherwise, and
            // InsertBlock has already applied them, so the fallback is to leave them
            // alone rather than to spell them out again here.
            if (float.TryParse(
                    node.Properties.GetString(NifToFbx.StripsRadiusProperty),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
            {
                SetFloat(shape, "Radius", radius);
            }

            if (ParseVector4(node.Properties.GetString(NifToFbx.StripsScaleProperty)) is { } scale)
                _model.FindItem(shape, "Scale")?.Value.Set(scale);

            // One shape can hold several data blocks, and FBX has one mesh per node,
            // so the seams travel as properties and the merged mesh is cut back along
            // them. A mesh with none recorded is one block, which is what it is.
            var parts = FbxStripsParts.Read(node, mesh.Triangles.Count);
            var blocks = new List<NifItem>();
            int at = 0;

            foreach (int count in parts.Count > 0 ? parts : [mesh.Triangles.Count])
            {
                // Each part keeps only the vertices its own triangles use, renumbered.
                // The vertices cannot simply be sliced: the mesh reader welds corners
                // that agree, so a part's vertices are no longer a contiguous run of
                // the merged list and there is no offset to subtract.
                var slice = new MeshGeometry();
                var moved = new Dictionary<ushort, ushort>();

                ushort Keep(ushort original)
                {
                    if (moved.TryGetValue(original, out ushort mapped))
                        return mapped;

                    mapped = (ushort)slice.Vertices.Count;
                    moved[original] = mapped;
                    slice.Vertices.Add(mesh.Vertices[original]);

                    return mapped;
                }

                for (int i = 0; i < count && at + i < mesh.Triangles.Count; i++)
                {
                    NifTriangle t = mesh.Triangles[at + i];

                    slice.Triangles.Add(new NifTriangle(Keep(t.V1), Keep(t.V2), Keep(t.V3)));
                }

                at += count;

                blocks.Add(WriteStripsData(slice));
            }

            if (_model.SetArraySize(shape, "Num Strips Data", "Strips Data", blocks.Count) is { } refs)
            {
                for (int i = 0; i < blocks.Count && i < refs.Children.Count; i++)
                    refs.Children[i].Value.SetLink(_model.IndexOf(blocks[i]));
            }

            _model.SetArraySize(shape, "Num Filters", "Filters", blocks.Count);

            return shape;
        }

        /// <summary>Writes one <c>NiTriStripsData</c>, a three-point strip per triangle.</summary>
        private NifItem WriteStripsData(MeshGeometry mesh)
        {
            NifItem data = _model.InsertBlock("NiTriStripsData");

            SetCount(data, "Num Vertices", (uint)mesh.Vertices.Count);
            SetBool(data, "Has Vertices", true);
            WriteVector3Array(data, "Vertices", mesh.Vertices);

            SetCount(data, "Num Triangles", (uint)mesh.Triangles.Count);
            SetCount(data, "Num Strips", (uint)mesh.Triangles.Count);
            SetBool(data, "Has Points", true);

            if (_model.SetArraySize(data, "Num Strips", "Strip Lengths", mesh.Triangles.Count)
                is { } lengths)
            {
                foreach (NifItem length in lengths.Children)
                    length.Value.SetCount(3);
            }

            if (_model.FindItem(data, "Points") is { } points)
            {
                points.InvalidateConditionsRecursive();
                _model.UpdateArraySize(points);

                for (int i = 0; i < mesh.Triangles.Count && i < points.Children.Count; i++)
                {
                    NifItem strip = points.Children[i];
                    _model.UpdateArraySize(strip);

                    if (strip.Children.Count < 3)
                        continue;

                    strip.Children[0].Value.SetCount(mesh.Triangles[i].V1);
                    strip.Children[1].Value.SetCount(mesh.Triangles[i].V2);
                    strip.Children[2].Value.SetCount(mesh.Triangles[i].V3);
                }
            }

            (NifVector3 centre, float radius) = mesh.ComputeBoundingSphere();
            _model.FindItem(data, @"Bounding Sphere\Center")?.Value.Set(centre);
            _model.FindItem(data, @"Bounding Sphere\Radius")?.Value.SetFloat(radius);

            return data;
        }

        /// <summary>
        /// What the backend said for itself, ready to append to a warning.
        /// </summary>
        /// <remarks>
        /// "Generation failed" on its own sends the reader to the wrong place. Havok
        /// is usually willing to say what it did not like, and once said it had built
        /// the thing and merely disapproved of the winding.
        /// </remarks>
        private static string Said(IMoppGenerator generator) =>
            generator.LastDiagnostics is { Length: > 0 } said ? $" -- {said}" : string.Empty;

        /// <summary>
        /// Reports a backend that failed, even when a later attempt succeeded.
        /// </summary>
        /// <remarks>
        /// A generation is retried, so a backend that crashes on one model can be
        /// hidden by the retry that works: the file comes out right and nothing says a
        /// model just killed the tool. That is the case worth reporting loudest,
        /// because it is the only chance to learn which model does it.
        /// </remarks>
        private void ReportBackendTrouble(IMoppGenerator generator, string name)
        {
            if (generator.LastFailures.Count == 0)
                return;

            Warnings.Add(
                $"{name}: the MOPP backend failed and was retried -- "
                + string.Join("; ", generator.LastFailures));
        }

        /// <summary>
        /// The mesh under a collision node, in Havok units.
        /// </summary>
        /// <remarks>
        /// The export scales collision geometry into game units so it sits with the
        /// rest of the scene; everything a Havok block stores is in metres, so it
        /// comes back down again here.
        /// </remarks>
        private MeshGeometry? ReadCollisionMesh(FbxObject node)
        {
            FbxObject? geometry = _scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry");

            MeshGeometry? mesh = geometry is null
                ? null
                : FbxMeshReader.Read(geometry, new FbxMeshReader.Options { InvertU = false, InvertV = false });

            if (mesh is null)
                return null;

            ShapeTessellator.Scale(mesh, ShapeTessellator.BhkScaleFactorInverse);

            return mesh;
        }

        private NifItem? BuildCompressedMesh(FbxObject node, string name)
        {
            IMoppGenerator? generator = MoppGenerator.Resolve();

            if (generator is null)
            {
                Warnings.Add($"{name}: mesh collision needs MOPP generation. {MoppGenerator.DescribeUnavailability()}");
                return null;
            }

            FbxObject? geometry = _scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry");
            MeshGeometry? mesh = geometry is null
                ? null
                : FbxMeshReader.Read(geometry, new FbxMeshReader.Options { InvertU = false, InvertV = false });

            if (mesh is null || mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{name}: mesh collision node has no geometry");
                return null;
            }

            // Havok works in metres.
            var vertices = new List<NifVector3>(mesh.Vertices.Count);

            foreach (NifVector3 v in mesh.Vertices)
            {
                vertices.Add(new NifVector3(
                    v.X * ShapeTessellator.BhkScaleFactorInverse,
                    v.Y * ShapeTessellator.BhkScaleFactorInverse,
                    v.Z * ShapeTessellator.BhkScaleFactorInverse));
            }

            List<string> materials = CollisionMaterialsOf(node);
            List<string> materialLayers = CollisionLayersOf(node);
            List<MoppGeometry> pieces = SplitByMaterial(geometry!, mesh, vertices, materials.Count);

            // At least one, matching the table written below. A node with no material at
            // all would otherwise ask Havok for an empty table while every triangle
            // claims material 0, which is a contradiction to hand a physics engine even
            // where it happens to survive it.
            CompressedMeshResult? built = generator.GenerateCompressedMesh(
                pieces, Math.Max(materials.Count, 1));

            if (built is null)
            {
                Warnings.Add(
                    $"{name}: MOPP generation failed for the mesh collision shape{Said(generator)}");

                return null;
            }

            ReportBackendTrouble(generator, name);

            NifItem shape = _model.InsertBlock("bhkCompressedMeshShape");
            NifItem data = _model.InsertBlock("bhkCompressedMeshShapeData");

            // "Points to root node?", says nif.xml, hedging. It is not a hedge: of the
            // 8,188 compressed mesh shapes Skyrim ships, all 8,188 point at the root
            // block. ck-cmd instead points it at the node the collision hangs off
            // (FBXWrangler.cpp:4977), which is the same block whenever collision sits
            // on the root and a different one whenever it does not -- so the vanilla
            // files are followed here rather than the reference (spec §4.8.1).
            //
            // `User Data` beside it is left alone. Every vanilla value is 16-byte
            // aligned and they cluster in tight runs (0x1078Axxx and friends, across
            // 0x4DA140 to 0x171ECE60): heap addresses from whatever machine Bethesda
            // exported on. There is nothing there to reconstruct, and ck-cmd never
            // writes it either.
            if (_sceneRoot is not null)
                _model.SetRef(shape, "Target", _sceneRoot);

            if (float.TryParse(
                    node.Properties.GetString(NifToFbx.CompressedMeshUnknownProperty),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float unknown))
            {
                SetFloat(shape, "Unknown Float 1", unknown);
            }

            // Radius, Radius Copy, Scale and Scale Copy are not written. nif.xml gives
            // this block 0.005 and (1,1,1,0) for them and InsertBlock applies that, so
            // writing the same numbers again only puts a second copy of the schema in
            // the code, where it can drift from the first.
            _model.SetRef(shape, "Data", data);

            WriteCompressedMeshData(data, built, materials, materialLayers);

            // Havok reaches the shape through a MOPP tree, never directly.
            NifItem mopp = _model.InsertBlock("bhkMoppBvTreeShape");
            _model.SetRef(mopp, "Shape", shape);
            WriteMoppCode(mopp, built.Mopp);

            return mopp;
        }

        /// <summary>Writes the MOPP tree and the quantisation it was built against.</summary>
        /// <remarks>
        /// The scale sits on the shape, the offset and the code inside the
        /// <c>MOPP Code</c> block. The code itself is a <em>binary</em> array, so it
        /// is one blob sized by <c>Data Size</c> rather than a byte per element.
        /// </remarks>
        private void WriteMoppCode(NifItem mopp, MoppResult result)
        {
            // The quantisation belongs in the code's own Offset, as its W: nif.xml
            // says "the quantization factor is equal to 256*256 divided by this
            // number", and NifSkope writes Vector4(origin, scale) there. The shape's
            // own Scale is a different field, which every vanilla file leaves at 1 --
            // writing mopper's number into it left W at zero, and a zero W is a
            // quantisation the engine cannot divide by.
            _model.FindItem(mopp, @"MOPP Code\Offset")?.Value.Set(
                new NifVector4(result.Origin.X, result.Origin.Y, result.Origin.Z, result.Scale));

            // Chunk subdivision is the PS3 layout. mopper's compressed mesh path used to
            // ask for it and now asks for the PC one, as HKXWrangler does
            // (HKXWrangler.cpp:3268), so the tree this describes really is built without
            // it -- and 2,065 of the 2,088 compressed mesh trees Skyrim ships say the
            // same. The other 23 hold values outside the 0..2 the enum defines, 205 and
            // 130 and the like, which are uninitialised bytes rather than a third kind.
            SetEnum(mopp, @"MOPP Code\Build Type", "hkMoppCodeBuildType", "BUILT_WITHOUT_CHUNK_SUBDIVISION");

            if (_model.SetArraySize(mopp, @"MOPP Code\Data Size", @"MOPP Code\Data", result.Code.Length)
                is { Children.Count: > 0 } blob)
            {
                blob.Children[0].Value.Set(result.Code);
            }
        }

        /// <summary>
        /// The Havok material of each material attached to a collision node, in the
        /// order the chunk table will hold them.
        /// </summary>
        private List<string> CollisionMaterialsOf(FbxObject node) =>
            [.. CollisionMaterialObjectsOf(node).Select(o => NameEncoding.Unsanitize(o.Name))];

        /// <summary>
        /// The collision layer each of those materials carries, in the same order.
        /// </summary>
        /// <remarks>
        /// A chunk material is a `bhkMeshMaterial`: a Havok material *and* a filter of
        /// its own, layer included. The layer was neither read from the entry nor
        /// written back to it, so every rebuilt chunk took nif.xml's default of
        /// `SKYL_STATIC` -- 108 differences over a 1,200-mesh sample, with
        /// `expspidersackrare.nif` holding 12 against our 1.
        ///
        /// A collision layer is not decoration: it decides what a thing collides with,
        /// and it is the input to the body's motion profile.
        /// </remarks>
        private List<string> CollisionLayersOf(FbxObject node) =>
            [.. CollisionMaterialObjectsOf(node)
                .Select(o => o.Properties.GetString(FbxCollisionMaterial.LayerProperty))];

        private List<FbxObject> CollisionMaterialObjectsOf(FbxObject node) =>
            [.. _scene.ChildrenOf(node.Id)
                .Where(o => o.Class == "Material" && !FbxLodSizes.IsLevelMaterial(o.Name))];

        /// <summary>
        /// Splits a collision mesh into one piece per material.
        /// </summary>
        /// <remarks>
        /// Havok gives a chunk the material of the triangles that built it, so a mesh
        /// with two materials has to reach mopper as two geometries. Sent whole, every
        /// chunk comes back on the same material and a floor of stone and wood collides
        /// as one substance.
        ///
        /// Each piece carries only the vertices its own triangles reach, renumbered,
        /// since Havok welds and indexes per geometry.
        /// </remarks>
        private static List<MoppGeometry> SplitByMaterial(
            FbxObject geometry, MeshGeometry mesh, List<NifVector3> vertices, int materialCount)
        {
            List<int>? perPolygon = FbxMeshReader.ReadPolygonMaterials(geometry);

            if (materialCount <= 1 || perPolygon is null || mesh.TrianglePolygons.Count != mesh.Triangles.Count)
                return [new MoppGeometry(vertices, mesh.Triangles)];

            var byMaterial = new SortedDictionary<int, List<NifTriangle>>();

            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                int polygon = mesh.TrianglePolygons[i];

                int material = polygon >= 0 && polygon < perPolygon.Count
                    ? Math.Clamp(perPolygon[polygon], 0, materialCount - 1)
                    : 0;

                if (!byMaterial.TryGetValue(material, out var list))
                    byMaterial[material] = list = [];

                list.Add(mesh.Triangles[i]);
            }

            var pieces = new List<MoppGeometry>(byMaterial.Count);

            foreach ((int material, List<NifTriangle> triangles) in byMaterial)
            {
                var map = new Dictionary<ushort, ushort>();
                var own = new List<NifVector3>();
                var renumbered = new List<NifTriangle>(triangles.Count);

                ushort Index(ushort vertex)
                {
                    if (map.TryGetValue(vertex, out ushort mapped))
                        return mapped;

                    mapped = (ushort)own.Count;
                    map[vertex] = mapped;
                    own.Add(vertices[vertex]);

                    return mapped;
                }

                foreach (NifTriangle t in triangles)
                    renumbered.Add(new NifTriangle(Index(t.V1), Index(t.V2), Index(t.V3)));

                pieces.Add(new MoppGeometry(own, renumbered, material));
            }

            return pieces;
        }

        /// <summary>Writes the chunked mesh Havok produced.</summary>
        private void WriteCompressedMeshData(
            NifItem data,
            CompressedMeshResult built,
            IReadOnlyList<string> materials,
            IReadOnlyList<string> materialLayers)
        {
            _model.FindItem(data, @"AABB\Min")?.Value.Set(built.BoundsMin);
            _model.FindItem(data, @"AABB\Max")?.Value.Set(built.BoundsMax);

            // The index widths Havok packs chunk vertices with -- 17 and 18 bits, and
            // the masks that go with them -- are nif.xml's own defaults for this block
            // and are applied when it is created, so they are not written again here.
            // `Error` is not written either. nif.xml's default is 0.001, which is the
            // step mopper packs a chunk's offsets in (`createMeshShape(0.001f, ...)`)
            // and what all 8,188 compressed shapes Skyrim ships carry. The export reads
            // this field rather than assuming that step, so the one thing that matters
            // is that the number in the file matches the one mopper used -- and the
            // schema already says it.

            if (_model.SetArraySize(data, "Num Big Verts", "Big Verts", built.BigVertices.Count) is { } bigVerts)
            {
                for (int i = 0; i < built.BigVertices.Count && i < bigVerts.Children.Count; i++)
                    bigVerts.Children[i].Value.Set(built.BigVertices[i]);
            }

            if (_model.SetArraySize(data, "Num Big Tris", "Big Tris", built.BigTriangles.Count) is { } bigTris)
            {
                for (int i = 0; i < built.BigTriangles.Count && i < bigTris.Children.Count; i++)
                {
                    var (a, b, c, material, welding) = built.BigTriangles[i];
                    NifItem entry = bigTris.Children[i];

                    _model.FindItem(entry, "Triangle")?.Value.Set(new NifTriangle((ushort)a, (ushort)b, (ushort)c));
                    _model.FindItem(entry, "Material")?.Value.SetCount(material);
                    _model.FindItem(entry, "Welding Info")?.Value.SetCount(welding);
                }
            }

            if (_model.SetArraySize(data, "Num Transforms", "Chunk Transforms", built.Transforms.Count)
                is { } transforms)
            {
                for (int i = 0; i < built.Transforms.Count && i < transforms.Children.Count; i++)
                {
                    NifItem entry = transforms.Children[i];
                    _model.FindItem(entry, "Translation")?.Value.Set(built.Transforms[i].Translation);
                    _model.FindItem(entry, "Rotation")?.Value.Set(built.Transforms[i].Rotation);
                }
            }

            WriteChunkMaterials(data, materials, materialLayers);

            if (_model.SetArraySize(data, "Num Chunks", "Chunks", built.Chunks.Count) is not { } chunks)
                return;

            for (int i = 0; i < built.Chunks.Count && i < chunks.Children.Count; i++)
            {
                CompressedMeshChunk source = built.Chunks[i];
                NifItem chunk = chunks.Children[i];

                _model.FindItem(chunk, "Translation")?.Value.Set(source.Offset);

                // Havok's own index into the table written above, which is meaningful
                // now that the materials reach it: mopper's -ccmm sets a material on
                // every triangle of a geometry, and Havok gives each chunk the material
                // its triangles carried. Clamped all the same -- an index outside the
                // table is a number the engine would read past the end of it, and this
                // field used to hold exactly that.
                uint material = built.Chunks[i].MaterialInfo;

                _model.FindItem(chunk, "Material Index")?.Value.SetCount(
                    material < (uint)Math.Max(materials.Count, 1) ? material : 0u);
                _model.FindItem(chunk, "Transform Index")?.Value.SetCount(source.TransformIndex);

                // mopper prints a hard-coded 65535 here, which is what Havok expects.
                _model.FindItem(chunk, "Reference")?.Value.SetCount(65535);

                WriteUShorts(chunk, "Num Vertices", "Vertices", source.Vertices);
                WriteUShorts(chunk, "Num Indices", "Indices", source.Indices);
                WriteUShorts(chunk, "Num Strips", "Strips", source.StripLengths);
                WriteUShorts(chunk, "Num Welding Info", "Welding Info", source.WeldingInfo);
            }
        }

        /// <summary>
        /// Writes the one-entry material table a rebuilt chunked mesh refers to.
        /// </summary>
        /// <remarks>
        /// A compressed mesh shape has no material of its own: its materials live in
        /// this table on the data block, and the chunks index into it. So
        /// `FbxCollisionMaterial.Apply`, which looks for a material field on the shape
        /// and does not follow refs, never found one -- every rebuilt mesh collision
        /// kept the default material whatever the scene said.
        ///
        /// One entry per material on the collision node, in the order the FBX connects
        /// them, which is the order the geometry's per-polygon material channel indexes.
        /// </remarks>
        private void WriteChunkMaterials(
            NifItem data, IReadOnlyList<string> materials, IReadOnlyList<string> layers)
        {
            if (_model.SetArraySize(data, "Num Materials", "Chunk Materials", Math.Max(materials.Count, 1))
                is not { Children.Count: > 0 } table)
            {
                return;
            }

            for (int i = 0; i < materials.Count && i < table.Children.Count; i++)
            {
                // The entry's own filter, which is not the body's: a chunk material
                // carries a layer of its own and nothing else was writing it.
                if (i < layers.Count && layers[i].Length > 0)
                    FbxCollisionMaterial.ApplyLayer(_model, table.Children[i], layers[i]);

                if (materials[i].Length == 0)
                    continue;

                if (!FbxCollisionMaterial.Apply(_model, table.Children[i], materials[i]))
                {
                    Warnings.Add(
                        $"\"{materials[i]}\" is not a Skyrim Havok material, "
                        + "the chunked mesh keeps the default for it");
                }
            }
        }

        private void WriteUShorts(NifItem parent, string countField, string arrayField, IReadOnlyList<ushort> values)
        {
            if (_model.SetArraySize(parent, countField, arrayField, values.Count) is not { } array)
                return;

            for (int i = 0; i < values.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetCount(values[i]);
        }


        private NifItem BuildConvex(IReadOnlyList<NifVector3> points, FbxObject node)
        {
            (List<NifVector4> vertices, List<NifVector4> planes) = ShapeFitter.FitConvex(points);

            NifItem shape = _model.InsertBlock("bhkConvexVerticesShape");

            // The shell Havok puts around the hull. nif.xml says as much -- "a shell
            // that is added around the shape" -- and a vanilla hull's planes sit exactly
            // that far outside its own corners: of the 852 hulls sampled, 420 match the
            // rule on every plane to within 1e-4 and 587 to within 1e-2.
            //
            // Carried, because it is authored: 0.05 is the commonest of many values and
            // 78 hulls use zero. se-cmd wrote a flat 0.01, which is neither.
            //
            // Written only when carried. 0.05 is also nif.xml's default for
            // bhkConvexShape and InsertBlock has applied it, so a scene that says
            // nothing already has it without this repeating the number.
            float radius = float.TryParse(
                node.Properties.GetString(NifToFbx.ConvexRadiusProperty),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float carried)
                ? carried
                : _model.FindItem(shape, "Radius")?.Value.ToFloat() ?? 0f;

            SetFloat(shape, "Radius", radius);

            // And the planes go out with it, or the shell would be a shell around
            // nothing: the face planes are what Havok collides against.
            for (int i = 0; i < planes.Count; i++)
            {
                NifVector4 p = planes[i];
                planes[i] = new NifVector4(p.X, p.Y, p.Z, p.W - radius);
            }

            if (_model.SetArraySize(shape, "Num Vertices", "Vertices", vertices.Count) is { } vertexArray)
            {
                for (int i = 0; i < vertices.Count && i < vertexArray.Children.Count; i++)
                    vertexArray.Children[i].Value.Set(vertices[i]);
            }

            // Havok needs the face planes as well as the points; it does not derive
            // them from the hull.
            if (_model.SetArraySize(shape, "Num Normals", "Normals", planes.Count) is { } planeArray)
            {
                for (int i = 0; i < planes.Count && i < planeArray.Children.Count; i++)
                    planeArray.Children[i].Value.Set(planes[i]);
            }

            return shape;
        }

        /// <summary>
        /// Which partitions draw each control point, for telling vertices apart.
        /// </summary>
        /// <remarks>
        /// The twenty-third factor of the vertex key, and the one that needed no
        /// channel of its own: FBX expresses a split skin as several skin deformers on
        /// one mesh, so which partitions a point belongs to is already in the scene and
        /// this only reads it back off.
        ///
        /// A set rather than a partition number. The partitions share one vertex array
        /// and a point on the seam between two body parts is drawn by both -- of 1,908
        /// multi-partition shapes in a 1,200-mesh sample, 1,645 share at least one
        /// vertex, 39,644 slots in all. Naming one of the two would split a vertex the
        /// file holds once.
        ///
        /// Null unless the scene said the skin was split, since a single partition
        /// gives every point the same answer and an answer everything shares tells
        /// nothing apart.
        /// </remarks>
        private static Dictionary<int, string>? PartitionSignatures(SkinData? skin)
        {
            if (skin is null || skin.Partitions.Count < 2)
                return null;

            var byPoint = new Dictionary<int, List<int>>();

            for (int p = 0; p < skin.Partitions.Count; p++)
            {
                foreach (ushort point in skin.Partitions[p].Vertices)
                {
                    if (!byPoint.TryGetValue(point, out List<int>? list))
                        byPoint[point] = list = [];

                    list.Add(p);
                }
            }

            var signatures = new Dictionary<int, string>(byPoint.Count);

            foreach ((int point, List<int> list) in byPoint)
            {
                list.Sort();
                signatures[point] = string.Join(",", list);
            }

            return signatures;
        }

        /// <summary>
        /// What each control point's skinning looks like, for telling vertices apart.
        /// </summary>
        /// <remarks>
        /// Every pair of a bone and a weight, heaviest first, and not just the four
        /// the renderer will draw with.
        ///
        /// Four is the limit on the partition and on the vertex buffer, not on the
        /// skin: `NiSkinData` keeps what was authored, and the game's own files put
        /// more than four there often enough to matter -- 4,319 vertices over a
        /// 3,000-mesh sample. So the fifth influence is not discarded on the way
        /// through, and a point that carries one is not the same vertex as a point that
        /// does not, however alike their heaviest four.
        ///
        /// This key was once trimmed to four and renormalised over them, back when the
        /// writer cut `NiSkinData` down to the same four. Two points alike in their
        /// heaviest four then keyed the same however they differed beyond it, which was
        /// right while the difference was about to be thrown away and wrong once it was
        /// kept: the two merge into one vertex, and one of the two sets of weights is
        /// the one the file ends up with.
        ///
        /// Weights are compared as authored, unscaled, for the same reason -- that is
        /// what `NiSkinData` will hold. Ties break on the name, which `List.Sort`
        /// leaves to chance; two points bound the same way must key the same, and equal
        /// weights are the one case where the ordering is otherwise arbitrary.
        ///
        /// Each influence names its bone rather than its place in the bone list,
        /// because the place is a fact about the list and not about the binding. A skin
        /// can hold one bone twice -- 51 of the 5,872 skinned shapes in a 4,000-mesh
        /// sample do, `dlc01/landscape/trees/winteraspen02.nif` among them -- and then
        /// one binding is spelled two ways, and two control points moved identically
        /// are held apart by which entry happened to record them. A bone with no name
        /// falls back to its list position, since two unnamed bones sharing a signature
        /// would merge control points that move differently and drop one of the two
        /// sets of weights, which is the one direction of error this key exists to
        /// prevent.
        /// </remarks>
        private static Dictionary<int, string>? InfluenceSignatures(SkinData? skin)
        {
            if (skin is null || skin.Bones.Count == 0)
                return null;

            var byPoint = new Dictionary<int, List<(string Bone, float Weight)>>();

            for (int b = 0; b < skin.Bones.Count; b++)
            {
                string bone = skin.Bones[b].Name is { Length: > 0 } named
                    ? named
                    : b.ToString(CultureInfo.InvariantCulture);

                foreach ((ushort point, float weight) in skin.Bones[b].Weights)
                {
                    // Weightless entries are dropped by ByVertex before the trim ever
                    // sees them, so counting them here would make a point look like it
                    // has influences the vertex will not hold.
                    if (weight <= 0f)
                        continue;

                    if (!byPoint.TryGetValue(point, out List<(string, float)>? list))
                        byPoint[point] = list = [];

                    list.Add((bone, weight));
                }
            }

            var signatures = new Dictionary<int, string>(byPoint.Count);

            foreach ((int point, List<(string Bone, float Weight)> list) in byPoint)
            {
                list.Sort((a, b) =>
                {
                    int byWeight = b.Weight.CompareTo(a.Weight);

                    return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Bone, b.Bone);
                });

                var text = new System.Text.StringBuilder();

                foreach ((string bone, float weight) in list)
                {
                    text.Append(bone).Append(':')
                        .Append(weight.ToString("R", CultureInfo.InvariantCulture))
                        .Append(',');
                }

                signatures[point] = text.ToString();
            }

            return signatures;
        }

        /// <summary>
        /// Moves a skin's weights from control points onto the vertices they became.
        /// </summary>
        /// <remarks>
        /// A cluster addresses control points; the mesh reader decides what the
        /// vertices are, in its own order and with identical ones merged. Applying a
        /// cluster's indices to the vertex list without this puts every weight on
        /// whichever vertex happens to hold that number — the mesh still loads, still
        /// has the right bones, and deforms wrongly, which nothing downstream can see.
        ///
        /// Two control points only merge when their influences are identical, so a
        /// weight that lands on a vertex already carrying one from its twin is the same
        /// weight and is dropped rather than added twice.
        /// </remarks>
        private void RemapSkinToVertices(SkinData? skin, MeshGeometry mesh, string name)
        {
            if (skin is null || mesh.VertexOfControlPoint.Count == 0)
                return;

            int lost = 0;

            foreach (SkinBone bone in skin.Bones)
            {
                var moved = new List<(ushort Vertex, float Weight)>(bone.Weights.Count);
                var already = new HashSet<ushort>();

                foreach ((ushort point, float weight) in bone.Weights)
                {
                    if (!mesh.VertexOfControlPoint.TryGetValue(point, out ushort vertex))
                    {
                        // A control point no triangle reaches is not a vertex, so there
                        // is nothing for its weight to hold on to.
                        lost++;
                        continue;
                    }

                    if (already.Add(vertex))
                        moved.Add((vertex, weight));
                }

                bone.Weights.Clear();
                bone.Weights.AddRange(moved);
            }

            if (lost > 0)
                Warnings.Add($"{name}: {lost} bone weights sat on control points no triangle uses");
        }

        /// <summary>Builds a <c>NiTriShape</c> and its data from an FBX geometry.</summary>
        private NifItem? BuildShape(FbxObject geometry, FbxObject holder, NifTransform transform)
        {
            var readerOptions = new FbxMeshReader.Options
            {
                InvertU = _options.InvertU,
                InvertV = _options.InvertV
            };

            // The skin is read before the mesh, not after. Two things need it that
            // early: a vertex is not fully described without the bones that move it,
            // so the reader cannot tell two apart without this; and the weights come
            // back indexed by control point and have to be brought over to vertices
            // once the reader has decided what the vertices are.
            SkinData? skin = FbxSkinIO.ReadSkin(_scene, geometry);

            readerOptions.Influences = InfluenceSignatures(skin);
            readerOptions.Partitions = PartitionSignatures(skin);

            MeshGeometry? mesh = FbxMeshReader.Read(geometry, readerOptions);

            if (mesh is null || mesh.IsEmpty)
            {
                Warnings.Add($"{geometry.Name}: no usable geometry, skipping");
                return null;
            }

            RemapSkinToVertices(skin, mesh, geometry.Name);

            if (mesh.Triangles.Count == 0)
            {
                Warnings.Add($"{geometry.Name}: no triangles, skipping");
                return null;
            }

            // A mesh that arrives without normals gets them computed, as ck-cmd does
            // (`FBXWrangler.cpp:3393`), since a DCC that exported none would otherwise
            // give a shape that renders unlit.
            //
            // Unless the shape said it has none. A NIF may hold one -- the game ships
            // 341 in a 1,500-mesh sample, 3.5% of its shapes -- and computing them for
            // those rewrites the whole vertex buffer: every offset in `Vertex Desc`
            // moves along by one and `Vertex Data Size` grows from 5 to 6.
            if (!mesh.HasNormals
                && geometry.Properties.GetString(NifToFbx.ShapeHasNoNormalsProperty).Length == 0)
            {
                mesh.RecalculateNormals();
            }

            // Normal maps are read in tangent space, so tangents a shape has are
            // regenerated here rather than carried: the ones the FBX holds were split
            // for its own vertex layout, and these have to match the vertices actually
            // being written.
            //
            // Regenerated, not introduced. A shape whose FBX carries no tangents did
            // not have them in the NIF it came from, or was authored without them, and
            // giving it some changes the vertex layout: nif.xml puts `Bitangent X` and
            // `Unused W` in the same slot and picks between them by the Tangents flag,
            // so a shape that gains tangents loses whatever that word held. 105 of the
            // shapes sampled were gaining them.
            if (mesh.HasUvs && mesh.HasTangents)
                TangentSpace.Generate(mesh);

            // Which geometry class this was, when the scene says. The edition only
            // decides when it does not: SE files hold NiTriShape as freely as
            // BSTriShape, so choosing by edition alone converts every shape in a file
            // to whichever class the edition prefers.
            NifItem shape = BuildGeometry(geometry, mesh, skin is not null);

            _model.SetTransform(shape, transform);

            // A shape can be animated too, and its holder was unwrapped, so it has
            // to be findable by name. TryAdd, because a real node of the same name
            // is the better target of the two.
            _nodesByName.TryAdd(NameEncoding.Unsanitize(geometry.Name), shape);

            BuildMaterial(shape, holder);

            // After the material, since a flipbook controller joins the shader
            // property's chain and the shader property is what the material builds.
            if (NifFlipWriter.HasFlipControllers(holder))
            {
                _model.WriteFlipControllers(
                    holder, shape, _model.GetRef(shape, "Shader Property") ?? shape, Warnings);
            }

            // Deferred: the bones are nodes elsewhere in the scene and may not have
            // been converted yet, so skins are wired up once the whole tree is
            // built.
            if (skin is not null)
            {
                _pendingSkins.Add((
                    shape, skin, mesh.Vertices.Count,
                    mesh.Triangles, mesh.TrianglePartitions, geometry));
            }

            return shape;
        }

        /// <summary>
        /// Packs the bone weights into an SE shape's vertices.
        /// </summary>
        /// <remarks>
        /// SE reads a skinned mesh's weights from the vertex buffer, not from
        /// <c>NiSkinData</c>, so a shape that has the blocks but not these renders
        /// rigid while looking fully rigged in a NIF editor.
        ///
        /// The indices are into the shape's own bone list, which is only settled once
        /// the skin has been written: a bone whose node is missing is dropped there,
        /// and every index after it moves. So the list is read back and matched by
        /// name rather than assumed to be the order the skin arrived in.
        /// </remarks>
        private void WriteVertexSkinning(NifItem shape, SkinData skin)
        {
            if (_model.FindItem(shape, "Vertex Data") is not { } vertices
                || _model.GetRef(shape, "Skin") is not { } instance)
            {
                return;
            }

            var boneIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
            uint next = 0;

            foreach (NifItem bone in _model.GetRefArray(instance, "Bones"))
                boneIndex.TryAdd(_model.GetName(bone), next++);

            var byVertex = skin.ByVertex();

            for (int i = 0; i < vertices.Children.Count; i++)
            {
                NifItem vertex = vertices.Children[i];

                if (_model.FindItem(vertex, "Bone Weights") is not { } weights
                    || _model.FindItem(vertex, "Bone Indices") is not { } indices)
                {
                    return;
                }

                // Both are fixed four-element arrays, but nothing has sized them yet:
                // a freshly inserted block leaves its arrays empty until asked, and
                // writing into an array with no elements writes nowhere.
                _model.UpdateArraySize(weights);
                _model.UpdateArraySize(indices);

                if (!byVertex.TryGetValue((ushort)i, out var influences))
                    continue;

                // Four is what the vertex holds, and ByVertex has already put the
                // heaviest first, so the ones that do not fit are the ones to lose.
                float total = 0f;
                int used = Math.Min(4, influences.Count);

                for (int j = 0; j < used; j++)
                    total += influences[j].Weight;

                for (int j = 0; j < used && j < weights.Children.Count; j++)
                {
                    (int bone, float weight) = influences[j];

                    string name = bone < skin.Bones.Count ? skin.Bones[bone].Name : string.Empty;

                    if (!boneIndex.TryGetValue(name, out uint index))
                        continue;

                    // Renormalised over the four that were kept, so a vertex whose fifth
                    // influence was dropped is not left slightly limp -- but only when it
                    // needs it.
                    //
                    // Dividing by a total that is already one is not a no-op in floating
                    // point: a total of 0.99999994 is enough to move a weight into the
                    // neighbouring half, and the game stores these as halves. Every
                    // weight on every fully-weighted vertex came back a few parts in ten
                    // thousand adrift, for arithmetic with nothing to correct.
                    //
                    // The scale is SkinData's, so this copy and the partition's agree
                    // on when to rescale and by how much.
                    weights.Children[j].Value.SetFloat(weight * SkinData.VertexScale(total));

                    if (j < indices.Children.Count)
                        indices.Children[j].Value.SetCount(index);

                }
            }
        }

        /// <summary>
        /// Whether this scene is a skeleton rather than an object.
        /// </summary>
        /// <remarks>
        /// Only consulted for a scene that carries no classes of its own. Worked out
        /// from the constraints when the caller has not said: a ragdoll constraint is
        /// something only a skeleton has, so a scene holding one is a rig — which is
        /// more than ck-cmd can do, since its <c>export_rig</c> is a flag the caller
        /// must know to set.
        /// </remarks>
        private bool IsSkeletonRig()
        {
            if (_options.SkeletonRig is { } stated)
                return stated;

            _isRig ??= _options.ImportConstraints
                       && _scene.ReadConstraints().Any(
                           c => c.Type.Contains("Ragdoll", StringComparison.OrdinalIgnoreCase));

            return _isRig.Value;
        }

        private bool? _isRig;

        /// <summary>Skins waiting for the whole node tree to exist.</summary>
        /// <remarks>
        /// The triangles come along because a partition carries its own copy of
        /// them, remapped to the vertices that partition lists.
        /// </remarks>
        private readonly List<(NifItem Shape, SkinData Skin, int VertexCount,
            List<NifTriangle> Triangles, List<int> TrianglePartitions, FbxObject Geometry)>
            _pendingSkins = [];

        /// <summary>
        /// The level sizes the mesh being built was marked with, if it was marked.
        /// </summary>
        private int[]? _lodSizes;

        /// <summary>
        /// Reads a level-of-detail marking, and puts the triangles in the order it means.
        /// </summary>
        /// <remarks>
        /// The levels are a material per polygon named <c>LOD0</c>, <c>LOD1</c>,
        /// <c>LOD2</c> — the one per-face channel a DCC tool lets an artist edit
        /// (§5.2.4). A NIF stores them as three counts into one triangle list, which
        /// only means anything if the triangles are grouped by level and the groups are
        /// in order, so the grouping happens here, before the geometry is written.
        ///
        /// Resolved by material name rather than by index: an artist who adds or
        /// removes a slot would otherwise shift every triangle a level, silently.
        /// A mesh with no LOD material is not marked, and keeps whatever the counts
        /// carried across said.
        /// </remarks>
        private int[]? ReadLodMarking(FbxObject geometry, MeshGeometry mesh)
        {
            if (FbxMeshReader.ReadPolygonMaterials(geometry) is not { } perPolygon
                || mesh.TrianglePolygons.Count != mesh.Triangles.Count)
            {
                return null;
            }

            if (_scene.ParentsOf(geometry.Id).FirstOrDefault() is not { } holder)
                return null;

            var byIndex = _scene.ChildrenOf(holder.Id)
                .Where(o => o.Class == "Material")
                .Select(o => LevelOf(o.Name))
                .ToList();

            if (byIndex.All(level => level < 0))
                return null;

            var levels = new List<int>(mesh.Triangles.Count);

            foreach (int polygon in mesh.TrianglePolygons)
            {
                int at = polygon < perPolygon.Count ? perPolygon[polygon] : -1;

                // A face left on the shape's own material belongs to no level, and
                // GroupByLevel keeps it at the end rather than dropping it.
                levels.Add(at >= 0 && at < byIndex.Count ? byIndex[at] : -1);
            }

            (List<int> order, int[] sizes) = FbxLodSizes.GroupByLevel(levels);

            var triangles = order.Select(i => mesh.Triangles[i]).ToList();
            var polygons = order.Select(i => mesh.TrianglePolygons[i]).ToList();

            // The partition each triangle is drawn by moves with it. Left in place it
            // would still be a list of the right length, describing the triangles that
            // used to be at those indices.
            var partitions = mesh.TrianglePartitions.Count == mesh.Triangles.Count
                ? order.Select(i => mesh.TrianglePartitions[i]).ToList()
                : null;

            mesh.Triangles.Clear();
            mesh.Triangles.AddRange(triangles);
            mesh.TrianglePolygons.Clear();
            mesh.TrianglePolygons.AddRange(polygons);

            if (partitions is not null)
            {
                mesh.TrianglePartitions.Clear();
                mesh.TrianglePartitions.AddRange(partitions);
            }

            return sizes;

            static int LevelOf(string name)
            {
                for (int level = 0; level < FbxLodSizes.Levels; level++)
                {
                    if (name == FbxLodSizes.LevelMaterial(level))
                        return level;
                }

                return -1;
            }
        }

        /// <summary>Nodes by name, for resolving bones.</summary>
        private readonly Dictionary<string, NifItem> _nodesByName = new(StringComparer.Ordinal);

        /// <summary>
        /// Pointer fields waiting for the node they name, and the name they want.
        /// </summary>
        /// <remarks>
        /// A pointer is the upward half of a two-way link and is never followed when a
        /// block is carried, or the thing it aims at comes across as a second copy
        /// attached to nothing. So the name travels instead and the link is made here,
        /// once every node the scene holds has been built and can be found.
        /// </remarks>
        private readonly List<(NifItem Field, string Named)> _pendingPointers = [];

        /// <summary>Records a pointer to resolve once the tree exists.</summary>
        private void AimAt(NifItem field, string named) => _pendingPointers.Add((field, named));

        /// <summary>
        /// Builds every skin once the nodes its bones refer to exist.
        /// </summary>
        private void BuildPendingSkins(NifItem root)
        {
            // Shapes that shared a skin data block in the source share one here, keyed
            // on which block it was rather than on what is in it (§5.2.1).
            var shared = new Dictionary<int, (NifItem Data, NifItem Partition)>();

            foreach ((NifItem shape, SkinData skin, int vertexCount, var triangles,
                      var trianglePartitions, FbxObject geometry) in _pendingSkins)
            {
                var missing = _model.WriteSkin(
                    shape, skin, _nodesByName, root, vertexCount, triangles,
                    _options.SkinInstanceType, shared, trianglePartitions);

                foreach (string bone in missing)
                    Warnings.Add($"{_model.GetName(shape)}: no node named \"{bone}\", its influence is dropped");

                WriteVertexSkinning(shape, skin);
                MoveGeometryIntoSkinPartition(shape, geometry);
            }

            _pendingSkins.Clear();
        }

        /// <summary>
        /// Rebuilds a shape that has no vertices, from the node standing for it.
        /// </summary>
        /// <remarks>
        /// The counterpart of the export's own empty-shape path (§5.2.4). Everything a
        /// shape carries except the mesh, since there is no mesh: a dummy TriShape is
        /// where a lightning controller puts the geometry it generates, and the file
        /// holds the shape, its shader and its alpha property with nothing in between.
        ///
        /// A NiTriBasedGeom still needs its data block — the class keeps its vertices
        /// there and a null Data is not a shape the engine will load — so an empty one
        /// is built. A BSTriShape packs its vertices inline and needs nothing.
        /// </remarks>
        private NifItem BuildEmptyShape(FbxObject model, string name, NifTransform transform)
        {
            string carried = FbxNodeType.Read(model, _model, string.Empty, "NiAVObject");

            string type = carried.Length > 0
                          && (_model.Database.Inherits(carried, "NiTriBasedGeom")
                              || _model.Database.Inherits(carried, "BSTriShape"))
                ? carried
                : "BSTriShape";

            if (_options.LegendaryEdition && _model.Database.Inherits(type, "BSTriShape"))
                type = "NiTriShape";

            NifItem shape = _model.InsertBlock(type);

            _model.SetString(shape, "Name", FbxNodeType.ReadName(model, name));
            FbxNodeType.ReadFlags(model, _model, shape);
            _model.SetTransform(shape, transform);
            _nodesByName.TryAdd(name, shape);

            FbxNodeType.ReadFields(
                model, _model, shape,
                _model.BlockInherits(shape, "BSTriShape") ? "BSTriShape" : "NiTriBasedGeom");

            FbxLodSizes.Read(model, _model, shape);
            FbxDynamicShape.Read(model, _model, shape, []);
            FbxExtraDataWriter.ReadExtraData(model, _model, shape, Warnings);
            BuildMaterial(shape, model);

            if (!_model.BlockInherits(shape, "BSTriShape"))
                _model.SetRef(shape, "Data", _model.InsertBlock("NiTriShapeData"));

            return shape;
        }

        /// <summary>
        /// Builds the geometry block this shape was, or the one its edition wants.
        /// </summary>
        /// <remarks>
        /// The two families differ in where the vertices live, not merely in name.
        /// A <c>BSTriShape</c> packs them inline; everything under
        /// <c>NiTriBasedGeom</c> keeps them in a data block beside it — and
        /// <c>BSLODTriShape</c> is in that second family despite its name, which is
        /// why it was coming back as a <c>BSTriShape</c> with a stray
        /// <c>NiTriShapeData</c> left over.
        ///
        /// `BSTriShape` does not exist before Skyrim SE, so a carried one is refused
        /// when building for LE.
        /// </remarks>
        private NifItem BuildGeometry(FbxObject geometry, MeshGeometry mesh, bool skinned)
        {
            string carried = FbxNodeType.Read(geometry, _model, string.Empty, "NiAVObject");

            _lodSizes = ReadLodMarking(geometry, mesh);

            NifItem built = BuildGeometryBlock(geometry, mesh, skinned, carried);

            // A shape carries extra data as a node does. Read after the block exists,
            // whichever class it turned out to be.
            FbxExtraDataWriter.ReadExtraData(geometry, _model, built, Warnings);

            return built;
        }

        private NifItem BuildGeometryBlock(
            FbxObject geometry, MeshGeometry mesh, bool skinned, string carried)
        {

            if (carried.Length > 0 && _model.Database.Inherits(carried, "BSTriShape"))
            {
                return _options.LegendaryEdition
                    ? BuildNiTriShape(geometry, mesh, "NiTriShape")
                    : BuildBsTriShape(geometry, mesh, skinned);
            }

            if (carried.Length > 0 && _model.Database.Inherits(carried, "NiTriBasedGeom"))
                return BuildNiTriShape(geometry, mesh, carried);

            return _options.LegendaryEdition
                ? BuildNiTriShape(geometry, mesh, "NiTriShape")
                : BuildBsTriShape(geometry, mesh, skinned);
        }

        private NifItem BuildNiTriShape(FbxObject geometry, MeshGeometry mesh, string type)
        {
            NifItem shape = _model.InsertBlock(type);
            _model.SetString(
                shape, "Name",
                FbxNodeType.ReadName(geometry, NameEncoding.Unsanitize(geometry.Name)));
            FbxNodeType.ReadFlags(geometry, _model, shape);

            // A BSLODTriShape's levels are counts into its one triangle list, and a
            // shape whose counts are all zero draws nothing at any distance.
            FbxLodSizes.Read(geometry, _model, shape);

            // An artist marking faces in a DCC tool outranks the counts that came in.
            if (_lodSizes is { } sizes)
                FbxLodSizes.WriteSizes(_model, shape, sizes);
            FbxNodeType.ReadFields(geometry, _model, shape, "NiTriBasedGeom");
            ReadActiveMaterial(geometry, shape);

            NifItem data = _model.InsertBlock("NiTriShapeData");
            WriteGeometryData(data, mesh);

            _model.SetRef(shape, "Data", data);

            return shape;
        }

        /// <summary>
        /// Builds a <c>BSTriShape</c>, which packs its vertices inline rather than
        /// referencing a data block.
        /// </summary>
        /// <remarks>
        /// The layout is described by <c>Vertex Desc</c>: its top bits say which
        /// attributes each vertex carries, and its low nibbles record the stride and
        /// the offset of each attribute within a vertex. The array's fields are
        /// conditional on those same flags, so the descriptor has to be written
        /// before the array is sized or the elements come out the wrong shape.
        /// </remarks>
        private NifItem BuildBsTriShape(FbxObject geometry, MeshGeometry mesh, bool skinned)
        {
            NifItem shape = _model.InsertBlock(
                FbxNodeType.Read(geometry, _model, "BSTriShape", "BSTriShape"));
            _model.SetString(
                shape, "Name",
                FbxNodeType.ReadName(geometry, NameEncoding.Unsanitize(geometry.Name)));
            FbxNodeType.ReadFlags(geometry, _model, shape);

            ReadActiveMaterial(geometry, shape);

            var descriptor = BuildVertexDescriptor(
                mesh, skinned, _model.BlockInherits(shape, "BSDynamicTriShape"));

            _model.FindItem(shape, "Vertex Desc")?.Value.SetCount(descriptor.Value);

            SetCount(shape, "Num Vertices", (uint)mesh.Vertices.Count);
            SetCount(shape, "Num Triangles", (uint)mesh.Triangles.Count);

            // Stored rather than derived, though nif.xml gives the formula.
            SetCount(shape, "Data Size",
                (uint)(descriptor.VertexSize * mesh.Vertices.Count + mesh.Triangles.Count * 6));

            (NifVector3 center, float radius) = mesh.ComputeBoundingSphere();
            _model.FindItem(shape, @"Bounding Sphere\Center")?.Value.Set(center);
            _model.FindItem(shape, @"Bounding Sphere\Radius")?.Value.SetFloat(radius);

            // The descriptor is set, so sizing now produces elements with the right
            // fields present.
            if (_model.FindItem(shape, "Vertex Data") is { } vertexData)
            {
                vertexData.InvalidateConditionsRecursive();
                _model.UpdateArraySize(vertexData);

                for (int i = 0; i < mesh.Vertices.Count && i < vertexData.Children.Count; i++)
                    WriteVertex(vertexData.Children[i], mesh, i);
            }

            if (_model.FindItem(shape, "Triangles") is { } triangles)
            {
                triangles.InvalidateConditionsRecursive();
                _model.UpdateArraySize(triangles);

                for (int i = 0; i < mesh.Triangles.Count && i < triangles.Children.Count; i++)
                    triangles.Children[i].Value.Set(mesh.Triangles[i]);
            }

            // A dynamic shape's own buffer: the positions again, plus the fourth
            // component that was carried because nothing here can derive it.
            FbxDynamicShape.Read(geometry, _model, shape, mesh.Vertices);

            return shape;
        }

        /// <summary>Writes one packed vertex.</summary>
        private void WriteVertex(NifItem vertex, MeshGeometry mesh, int index)
        {
            _model.FindItem(vertex, "Vertex")?.Value.Set(mesh.Vertices[index]);

            if (mesh.HasUvs && index < mesh.Uvs.Count)
            {
                // Already in NIF's V direction. `FbxMeshReader` turns it back on the way
                // in — that is what its `InvertV` option is for, and it defaults to on —
                // so flipping here as well put V through an odd number of flips and every
                // texture coordinate came back as `1 - v`.
                //
                // The `NiTriShapeData` path a hundred lines down has always written
                // `mesh.Uvs[i]` straight through. Two places writing one convention, and
                // only one of them right: nothing compared a UV until the round trip
                // started comparing whole graphs, and then ten of twenty-four fixtures
                // said so at once.
                _model.FindItem(vertex, "UV")?.Value.Set(mesh.Uvs[index]);
            }

            if (mesh.HasNormals && index < mesh.Normals.Count)
                _model.FindItem(vertex, "Normal")?.Value.Set(mesh.Normals[index]);

            if (mesh.HasTangents && index < mesh.Tangents.Count)
            {
                _model.FindItem(vertex, "Tangent")?.Value.Set(mesh.Tangents[index]);

                // The bitangent is split across three lanes: X sits beside the
                // position, Y and Z beside the normal and tangent.
                NifVector3 bitangent = mesh.Bitangents[index];

                _model.FindItem(vertex, "Bitangent X")?.Value.SetFloat(bitangent.X);
                _model.FindItem(vertex, "Bitangent Y")?.Value.SetCount(SNormToByte(bitangent.Y));
                _model.FindItem(vertex, "Bitangent Z")?.Value.SetCount(SNormToByte(bitangent.Z));
            }

            if (mesh.HasColors && index < mesh.Colors.Count)
                _model.FindItem(vertex, "Vertex Colors")?.Value.Set(mesh.Colors[index]);

            // The two words with no channel of their own, back where they came from.
            // Only written when the layout has them: Unused W shares its slot with
            // Bitangent X and exists only on a shape without tangents, and Eye Data
            // only under its own flag.
            if (mesh.HasUnusedW && index < mesh.UnusedW.Count)
                _model.FindItem(vertex, "Unused W")?.Value.SetCount(mesh.UnusedW[index]);

            if (mesh.HasEyeData && index < mesh.EyeData.Count)
                _model.FindItem(vertex, "Eye Data")?.Value.SetFloat(mesh.EyeData[index]);
        }

        /// <summary>Puts back the index of a geometry's active material.</summary>
        /// <remarks>
        /// `Material Data` sits on `NiGeometry`, below the base class the field carrier
        /// treats as a shape's own, so nothing carried this and every rebuilt shape took
        /// nif.xml's default of -1. The files hold 0 as often as -1 with no materials
        /// either way, so there is nothing to assume and it has to travel.
        /// </remarks>
        private void ReadActiveMaterial(FbxObject geometry, NifItem shape)
        {
            string text = geometry.Properties.GetString(NifToFbx.ActiveMaterialProperty);

            if (int.TryParse(
                    text,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int active))
            {
                _model.FindItem(shape, @"Material Data\Active Material")?.Value.SetCount(
                    unchecked((uint)active));
            }
        }

        private static uint SNormToByte(float value) =>
            (uint)Math.Clamp(MathF.Round((value + 1f) / 2f * 255f), 0f, 255f);

        /// <summary>
        /// Works out the vertex descriptor for a mesh: which attributes are present,
        /// how large a vertex is, and where each attribute sits inside one.
        /// </summary>
        private static (ulong Value, int VertexSize) BuildVertexDescriptor(
            MeshGeometry mesh, bool skinned, bool dynamic)
        {
            var flags = VertexFlags.Vertex;

            // A dynamic shape holds no position in its vertex. Its own second buffer of
            // Vector4s is where the shape actually is -- the static ones are zero in
            // every file seen -- so the format does not store them twice and the flag is
            // off. nif.xml follows the flag through: without it a vertex has no `Vertex`
            // and no `Bitangent X`, and the struct starts at the texture coordinate.
            //
            // Computing this from the mesh alone gave every dynamic shape a position it
            // does not carry -- a 40-byte vertex where the game writes 24. The
            // descriptor has to be calculated, not carried, so the thing to get right is
            // what the vertex actually holds.
            //
            // Full precision travels with it: all four dynamic shapes among the fixtures
            // set it and none of the others do.
            if (dynamic)
                flags = (flags & ~VertexFlags.Vertex) | VertexFlags.FullPrecision;

            if (skinned)
                flags |= VertexFlags.Skinned;

            if (mesh.HasUvs)
                flags |= VertexFlags.UV;

            if (mesh.HasNormals)
                flags |= VertexFlags.Normal;

            if (mesh.HasTangents)
                flags |= VertexFlags.Tangent;

            if (mesh.HasColors)
                flags |= VertexFlags.Colors;

            // The eye marker has its own flag and its own place at the end of the
            // vertex. Without it nif.xml gives the field no condition to be live under,
            // so a value written into it goes nowhere.
            if (mesh.HasEyeData)
                flags |= VertexFlags.EyeData;

            // Field order and sizes follow BSVertexDataSSE: a full-precision
            // position, a float taking the fourth lane (the bitangent's X),
            // half-precision UVs, then signed bytes for the normal and tangent with
            // the rest of the bitangent packed into their spare lanes.
            //
            // The position has no offset member: it is always first, and takes sixteen
            // bytes with the bitangent's X in its fourth lane. A vertex without one
            // starts at zero instead.
            var desc = new BSVertexDesc { Flags = flags };
            uint offset = flags.HasFlag(VertexFlags.Vertex) ? 16u : 0u;

            // A dynamic shape's own buffer holds one Vector4 per vertex, and the
            // descriptor records how wide that entry is beside how wide the static
            // vertex is. Left at zero the shape says its dynamic vertex has no size.
            if (dynamic)
                desc.Set(BSVertexDesc.Member.DynamicVertexSize, 16);

            if (mesh.HasUvs)
            {
                desc.Set(BSVertexDesc.Member.UV1Offset, offset);
                offset += 4;
            }

            if (mesh.HasNormals)
            {
                desc.Set(BSVertexDesc.Member.NormalOffset, offset);
                offset += 4;
            }

            if (mesh.HasTangents)
            {
                desc.Set(BSVertexDesc.Member.TangentOffset, offset);
                offset += 4;
            }

            if (mesh.HasColors)
            {
                desc.Set(BSVertexDesc.Member.ColorOffset, offset);
                offset += 4;
            }

            if (skinned)
            {
                // Four half-precision weights and four byte indices, twelve bytes. SE
                // reads a skinned mesh's weights from here and not from NiSkinData, so
                // a shape without them is fully rigged in an editor and rigid in game.
                desc.Set(BSVertexDesc.Member.SkinningDataOffset, offset);
                offset += 12;
            }

            // Last in the vertex, after the weights: one float saying whether this
            // vertex is the eye.
            if (mesh.HasEyeData)
            {
                desc.Set(BSVertexDesc.Member.EyeDataOffset, offset);
                offset += 4;
            }

            desc.VertexSize = offset;

            return (desc.Value, (int)offset);
        }

        /// <summary><c>BSGeometryDataFlags</c> bit 12, which announces the tangents.</summary>
        private const uint HasTangentsFlag = 0x1000;

        /// <summary>Fills a <c>NiTriShapeData</c> from the neutral mesh.</summary>
        private void WriteGeometryData(NifItem data, MeshGeometry mesh)
        {
            SetCount(data, "Num Vertices", (uint)mesh.Vertices.Count);
            SetBool(data, "Has Vertices", true);

            // The UV set count lives in the low six bits of the data flags, and the
            // UV array's length expression reads it, so it has to be set before the
            // array is sized.
            uint uvSets = mesh.HasUvs ? 1u : 0u;

            // Bit 12 announces the tangent arrays. Writing them without it leaves
            // them in the file for nothing to read.
            uint bsFlags = uvSets | (mesh.HasTangents ? HasTangentsFlag : 0u);

            SetCount(data, "Data Flags", uvSets);
            SetCount(data, "BS Data Flags", bsFlags);

            SetBool(data, "Has Normals", mesh.HasNormals);
            SetBool(data, "Has Vertex Colors", mesh.HasColors);

            WriteVector3Array(data, "Vertices", mesh.Vertices);

            if (mesh.HasNormals)
                WriteVector3Array(data, "Normals", mesh.Normals);

            if (mesh.HasTangents)
            {
                WriteVector3Array(data, "Tangents", mesh.Tangents);
                WriteVector3Array(data, "Bitangents", mesh.Bitangents);
            }

            if (mesh.HasColors && _model.FindItem(data, "Vertex Colors") is { } colors)
            {
                colors.InvalidateConditionsRecursive();
                _model.UpdateArraySize(colors);

                for (int i = 0; i < mesh.Colors.Count && i < colors.Children.Count; i++)
                    colors.Children[i].Value.Set(mesh.Colors[i]);
            }

            if (mesh.HasUvs && _model.FindItem(data, "UV Sets") is { } sets)
            {
                sets.InvalidateConditionsRecursive();
                _model.UpdateArraySize(sets);

                // Outer index is the set, inner the vertex.
                if (sets.Child(0) is { } set0)
                {
                    _model.UpdateArraySize(set0);

                    for (int i = 0; i < mesh.Uvs.Count && i < set0.Children.Count; i++)
                        set0.Children[i].Value.Set(mesh.Uvs[i]);
                }
            }

            (NifVector3 center, float radius) = mesh.ComputeBoundingSphere();
            _model.FindItem(data, @"Bounding Sphere\Center")?.Value.Set(center);
            _model.FindItem(data, @"Bounding Sphere\Radius")?.Value.SetFloat(radius);

            SetCount(data, "Num Triangles", (uint)mesh.Triangles.Count);
            SetCount(data, "Num Triangle Points", (uint)(mesh.Triangles.Count * 3));
            SetBool(data, "Has Triangles", true);

            if (_model.FindItem(data, "Triangles") is { } triangles)
            {
                triangles.InvalidateConditionsRecursive();
                _model.UpdateArraySize(triangles);

                for (int i = 0; i < mesh.Triangles.Count && i < triangles.Children.Count; i++)
                    triangles.Children[i].Value.Set(mesh.Triangles[i]);
            }
        }

        /// <summary>
        /// Rebuilds a shader property from the material attached to the mesh holder.
        /// </summary>
        private void BuildMaterial(NifItem shape, FbxObject holder)
        {
            // A shape has one material. The level markers (§5.2.4) are materials too
            // and are not one of them, so they are passed over rather than shaded with.
            FbxObject? material = _scene.ChildrenOf(holder.Id)
                .FirstOrDefault(o => o.Class == "Material" && !FbxLodSizes.IsLevelMaterial(o.Name));

            if (material is null)
                return;

            // The material says which shader it came from; only an effect shader
            // records it, since a lighting shader is what everything else rebuilds as.
            if (FbxEffectShader.WasWritten(material))
            {
                _model.SetRef(shape, "Shader Property", FbxEffectShader.Read(material, _model));

                BuildAlphaProperty(shape, material.Properties);
                return;
            }

            NifItem shader = _model.InsertBlock("BSLightingShaderProperty");
            FbxProperties properties = material.Properties;

            // The shader block's own name. Empty on nearly every shader the game ships,
            // and not on all of them: a water shader is called "water", and one that
            // comes back nameless is a block nothing can find that way.
            if (properties.GetString(FbxMaterialWriter.ShaderNameProperty) is { Length: > 0 } shaderName)
                _model.SetString(shader, "Name", shaderName);

            SetFloat(shader, "Glossiness", (float)properties.GetDouble("ShininessExponent"));

            // FBX keeps the specular factor over 0..1, NIF over 0..999.
            SetFloat(shader, "Specular Strength", (float)(properties.GetDouble("SpecularFactor") * 999.0));

            (double sr, double sg, double sb) = properties.GetVector3("SpecularColor", 1.0);
            _model.FindItem(shader, "Specular Color")?.Value.Set(
                new NifColor3((float)sr, (float)sg, (float)sb));

            (double er, double eg, double eb) = properties.GetVector3("EmissiveColor");
            _model.FindItem(shader, "Emissive Color")?.Value.Set(
                new NifColor3((float)er, (float)eg, (float)eb));

            SetFloat(shader, "Emissive Multiple", (float)properties.GetDouble("EmissiveFactor", 1.0));
            SetFloat(shader, "Alpha", (float)(1.0 - properties.GetDouble("TransparencyFactor")));
            // Which shading path this is, by name. Written out by the exporter and never
            // read back, so every rebuilt lighting shader stayed on type 0 whatever the
            // file said -- and the type is not only a label: nif.xml makes
            // `Environment Map Scale` conditional on it being 1, so an environment-mapped
            // shader lost its scale as well and reported two differences for one cause.
            //
            // Set before the fields whose existence depends on it, and the conditions
            // re-evaluated, or the field is still absent when it is written.
            if (properties.GetString("shader_type") is { Length: > 0 } typeName
                && _model.FindItem(shader, "Shader Type") is { } shaderType
                && _model.Database.TryGetEnumOptionValue(shaderType.Type, typeName, out uint typeValue))
            {
                shaderType.Value.SetCount(typeValue);
                shader.InvalidateConditionsRecursive();
            }

            SetFloat(shader, "Environment Map Scale", (float)properties.GetDouble("environment_map_scale"));

            // Defaults to wrapping in both directions, which is nif.xml's default and
            // the commonest, so a scene that never carried one is unchanged.
            if (properties.Contains("texture_clamp_mode"))
                SetCount(shader, "Texture Clamp Mode", (uint)properties.GetInt("texture_clamp_mode"));

            // ...and the rest of what this shading path carries. Written after the type
            // above, since that is what makes them exist at all.
            foreach (string field in MaterialData.ShaderTypeFields)
            {
                string text = properties.GetString(MaterialData.ShaderTypeFieldProperty(field));

                if (text.Length > 0)
                    SetShaderField(shader, field, text);
            }

            // Lighting-shader only. SetFloat finds nothing on an effect shader and does
            // nothing, so these are safe to write for either.
            SetFloat(shader, "Lighting Effect 1", (float)properties.GetDouble("lighting_effect_1", 0.3));
            SetFloat(shader, "Lighting Effect 2", (float)properties.GetDouble("lighting_effect_2", 2.0));
            SetFloat(shader, "Refraction Strength", (float)properties.GetDouble("refraction_strength"));

            ReadUvTransform(shader, material);

            NifItem textureSet = BuildTextureSet(material);
            _model.SetRef(shader, "Texture Set", textureSet);

            WriteShaderFlags(shader, shape, holder, properties);

            _model.SetRef(shape, "Shader Property", shader);

            BuildAlphaProperty(shape, properties);
        }

        /// <summary>
        /// Puts one carried shader field back, whatever shape it is.
        /// </summary>
        /// <remarks>
        /// Does nothing when the field is not live: a shader on a different path than
        /// the one the text came from has no such field, and nif.xml's condition is
        /// what decides. The counterpart of `NifToFbx.ShaderFieldText`.
        /// </remarks>
        private void SetShaderField(NifItem shader, string field, string text)
        {
            if (_model.FindItem(shader, field) is not { } item)
                return;

            string[] parts = text.Split(',');
            var numbers = new float[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                    return;
            }

            switch (item.Value.Get<object>())
            {
                case NifColor3 when numbers.Length == 3:
                    item.Value.Set(new NifColor3(numbers[0], numbers[1], numbers[2]));
                    break;

                case NifVector2 when numbers.Length == 2:
                    item.Value.Set(new NifVector2(numbers[0], numbers[1]));
                    break;

                default:
                    if (numbers.Length == 1)
                        item.Value.SetFloat(numbers[0]);

                    break;
            }
        }

        /// <summary>
        /// Restores the two shader flag words, forcing the two bits about the mesh.
        /// </summary>
        /// <remarks>
        /// Neither word was written at all, so every rebuilt shader took nif.xml's
        /// defaults -- and those are worth little here: 225 distinct values of
        /// `Shader Flags 1` across 20,576 vanilla shaders, with the commonest covering
        /// 33%.
        ///
        /// Carried whole and not adjusted, though two bits do describe the mesh rather
        /// than its lighting: `Skinned` (flags 1, bit 1) and `Vertex_Colors` (flags 2,
        /// bit 5). Vanilla ties both to the content -- of 20,576 shapes not one skinned
        /// shape lacks the skinned bit -- so forcing them on was tried.
        ///
        /// Forcing `Skinned` changes nothing: it is already right everywhere. Forcing
        /// `Vertex_Colors` sets the bit on 53 shapes across the fixtures whose source
        /// shader has it clear, because the rebuilt shape really does carry colours the
        /// source shape did not. That is a difference in the geometry, and writing it
        /// into the shader word would hide where it comes from rather than fix it. So
        /// neither is forced, and the words go back exactly as they came.
        /// </remarks>
        private void WriteShaderFlags(
            NifItem shader, NifItem shape, FbxObject holder, FbxProperties properties)
        {
            bool carried = properties.Has(FbxMaterialWriter.ShaderFlags1Property)
                           || properties.Has(FbxMaterialWriter.ShaderFlags2Property);

            Carry("Shader Flags 1", FbxMaterialWriter.ShaderFlags1Property);
            Carry("Shader Flags 2", FbxMaterialWriter.ShaderFlags2Property);

            void Carry(string field, string property)
            {
                if (_model.FindItem(shader, field) is not { } item || !properties.Has(property))
                    return;

                // Signed on the way through, as ck-cmd stores it; two's complement puts
                // the top bit back where it belongs.
                item.Value.SetCount(unchecked((uint)properties.GetInt(property)));
            }

            if (carried)
                return;

            // Nothing travelled, so this is a material authored in a DCC tool. ck-cmd
            // derives four bits for that case and se-cmd follows it, since a scene may
            // pass through either tool (FBXWrangler.cpp:3445-3451 and :3576).
            //
            // Only reached when the words are absent: when they are present ck-cmd
            // overwrites both wholesale at :3626, discarding everything it derived, and
            // so does this.
            (bool colours, bool translucent) = VertexColoursOf(shape);

            Derive("Shader Flags 2", VertexColorsFlag, colours);
            Derive("Shader Flags 1", VertexAlphaFlag, colours && translucent);
            Derive("Shader Flags 1", SkinnedFlag, HasSkinDeformer(holder));
            Derive("Shader Flags 1", SpecularFlag, properties.GetDouble("SpecularFactor") > 0.0);

            void Derive(string field, uint bit, bool set)
            {
                if (_model.FindItem(shader, field) is not { } item)
                    return;

                uint value = item.Value.ToUInt();

                item.Value.SetCount(set ? value | bit : value & ~bit);
            }
        }

        /// <summary>nif.xml: "Required For Skinned Meshes".</summary>
        private const uint SkinnedFlag = 1u << 1;

        /// <summary>nif.xml: "Enables using alpha component of vertex colors".</summary>
        private const uint VertexAlphaFlag = 1u << 3;

        /// <summary>nif.xml: "Has Vertex Colors".</summary>
        private const uint VertexColorsFlag = 1u << 5;

        /// <summary>The specular bit, which ck-cmd ties to the material's factor.</summary>
        private const uint SpecularFlag = 1u << 0;

        /// <summary>
        /// Whether the shape just built carries vertex colours, and whether any of them
        /// is less than opaque.
        /// </summary>
        /// <remarks>
        /// Asked of the shape's own vertex descriptor, which `BuildGeometry` has already
        /// written by the time a material is built. The obvious-looking alternative --
        /// whether a vertex has a `Vertex Colors` field -- is always true: the field is
        /// conditional on this very flag, and it is present in the tree either way.
        ///
        /// ck-cmd asks the same of the data it has just built, `data->GetHasVertexColors()`
        /// (`FBXWrangler.cpp:3445`), and takes the alpha bit from any colour under full
        /// opacity (`:3315`).
        /// </remarks>
        private (bool Any, bool Translucent) VertexColoursOf(NifItem shape)
        {
            if (_model.FindItem(shape, "Vertex Data") is { Children.Count: > 0 } buffer)
            {
                var descriptor = new BSVertexDesc(
                    _model.FindItem(shape, "Vertex Desc")?.Value.ToUInt64() ?? 0);

                if (!descriptor.HasFlag(VertexFlags.Colors))
                    return (false, false);

                return (true, buffer.Children.Any(
                    v => _model.FindItem(v, "Vertex Colors") is { } c
                         && c.Value.Get<NifColor4>().A < 1f));
            }

            if (_model.GetRef(shape, "Data") is not { } data
                || _model.GetUInt(data, "Has Vertex Colors") == 0)
            {
                return (false, false);
            }

            return (true, _model.FindItem(data, "Vertex Colors") is { } colours
                          && colours.Children.Any(c => c.Value.Get<NifColor4>().A < 1f));
        }

        /// <summary>Whether the geometry under a holder is skinned, as ck-cmd asks it.</summary>
        private bool HasSkinDeformer(FbxObject holder) =>
            _scene.ChildrenOf(holder.Id)
                .Where(o => o.Class == "Geometry")
                .Any(g => _scene.ChildrenOf(g.Id)
                    .Any(o => o.Class == "Deformer" && o.SubClass == "Skin"));


        /// <summary>
        /// Recovers the shader's UV offset and scale from the material's textures.
        /// </summary>
        /// <remarks>
        /// FBX carries these per texture, as <c>ModelUVTranslation</c> and
        /// <c>ModelUVScaling</c>, while a NIF shader has one pair for all of its
        /// slots. The first texture that names them wins, which is the same pair the
        /// export wrote onto every slot.
        ///
        /// The default matters more than it looks. A shader is authored with an
        /// identity scale of one, not zero, and a zero here does not fail loudly --
        /// it multiplies every texture coordinate in the mesh to nothing.
        /// </remarks>
        private void ReadUvTransform(NifItem shader, FbxObject material)
        {
            var offset = new NifVector2(0f, 0f);
            var scale = new NifVector2(1f, 1f);

            foreach ((FbxObject texture, _) in _scene.PropertyConnectionsTo(material.Id))
            {
                if (Pair(texture, "ModelUVTranslation") is { } t)
                    offset = t;

                if (Pair(texture, "ModelUVScaling") is { } s)
                {
                    scale = s;
                    break;
                }
            }

            _model.FindItem(shader, "UV Offset")?.Value.Set(offset);
            _model.FindItem(shader, "UV Scale")?.Value.Set(scale);
        }

        /// <summary>Reads a two-double FBX record, if it is there and well formed.</summary>
        private static NifVector2? Pair(FbxObject texture, string name)
        {
            if (texture.Child(name) is not { } node || node.Properties.Count < 2)
                return null;

            try
            {
                return new NifVector2(
                    System.Convert.ToSingle(node.Properties[0]),
                    System.Convert.ToSingle(node.Properties[1]));
            }
            catch (Exception e) when (e is InvalidCastException or FormatException)
            {
                return null;
            }
        }

        /// <summary>Alpha properties built so far, by the source block they came from.</summary>
        /// <remarks>
        /// Keyed on identity rather than on content. Bethesda's files point several
        /// shapes at one block and also carry identical blocks side by side, so
        /// merging by equality is as wrong as never merging at all.
        /// </remarks>
        private readonly Dictionary<string, NifItem> _alphaProperties = new(StringComparer.Ordinal);

        /// <summary>Texture sets built so far, by the source block they came from.</summary>
        private readonly Dictionary<string, NifItem> _textureSets = new(StringComparer.Ordinal);

        private NifItem BuildTextureSet(FbxObject material)
        {
            // Skyrim always writes nine slots, whether or not they are used.
            const int SlotCount = 9;
            var paths = new string[SlotCount];

            foreach ((FbxObject texture, string property) in _scene.PropertyConnectionsTo(material.Id))
            {
                int slot = property switch
                {
                    "DiffuseColor" => MaterialData.DiffuseSlot,
                    "NormalMap" => MaterialData.NormalSlot,
                    _ when property.StartsWith("slot", StringComparison.Ordinal)
                        && int.TryParse(property.AsSpan(4), out int n) => n - 1,
                    _ => -1
                };

                if (slot < 0 || slot >= SlotCount)
                    continue;

                string path = texture.Child("RelativeFilename")?.Properties.FirstOrDefault() as string
                    ?? texture.Child("FileName")?.Properties.FirstOrDefault() as string
                    ?? string.Empty;

                paths[slot] = MaterialData.NormalizeTexturePath(path);
            }

            // Shapes that shared a set in the source share one here. Keyed on which
            // block it was rather than on the paths, since a file can hold two
            // identical sets on purpose -- rebuilding by content would merge those.
            string key = material.Properties.GetString(FbxMaterialWriter.TextureSetIdProperty);

            if (key.Length > 0 && _textureSets.TryGetValue(key, out NifItem? shared))
                return shared;

            NifItem set = _model.InsertBlock("BSShaderTextureSet");

            if (_model.SetArraySize(set, "Num Textures", "Textures", SlotCount) is { } textures)
            {
                for (int i = 0; i < SlotCount && i < textures.Children.Count; i++)
                    textures.Children[i].Value.Set(paths[i] ?? string.Empty);
            }

            if (key.Length > 0)
                _textureSets[key] = set;

            return set;
        }

        /// <summary>
        /// Reassembles a <c>NiAlphaProperty</c> from the user properties the export
        /// side spread it across.
        /// </summary>
        private void BuildAlphaProperty(NifItem shape, FbxProperties properties)
        {
            if (!properties.Contains("source_blend_mode") && !properties.Contains("alpha_test_enable"))
                return;

            var alpha = new AlphaSettings
            {
                ColorBlendingEnable = properties.GetBool("color_blending_enable"),
                SourceBlendMode = AlphaSettings.ParseBlendMode(properties.GetString("source_blend_mode")),
                DestinationBlendMode = AlphaSettings.ParseBlendMode(properties.GetString("destination_blend_mode")),
                AlphaTestEnable = properties.GetBool("alpha_test_enable"),
                AlphaTestMode = AlphaSettings.ParseTestMode(properties.GetString("alpha_test_mode")),
                NoSorter = properties.GetBool("no_sorter_flag"),
                CloneUnique = properties.GetBool("clone_unique_flag"),
                EditorAlphaThreshold = properties.GetBool("editor_alpha_threshold_flag"),
                Threshold = (byte)properties.GetInt("alpha_test_threshold")
            };

            // An all-zero flags word means nothing was set; FBXWrangler emits no
            // property in that case rather than an inert one.
            if (alpha.ToFlags() == 0)
                return;

            // Shapes that shared a block in the source share one here. Eight shapes
            // pointing at two alpha properties came back with eight.
            string key = properties.GetString(FbxMaterialWriter.AlphaIdProperty);

            if (key.Length == 0 || !_alphaProperties.TryGetValue(key, out NifItem? block))
            {
                block = _model.InsertBlock("NiAlphaProperty");
                SetCount(block, "Flags", alpha.ToFlags());
                SetCount(block, "Threshold", alpha.Threshold);

                if (key.Length > 0)
                    _alphaProperties[key] = block;
            }

            _model.SetRef(shape, "Alpha Property", block);
        }

        // --- helpers ----------------------------------------------------------

        /// <summary>True when a model carries geometry, directly or via a holder.</summary>
        private bool HasGeometry(FbxObject model) =>
            _scene.ChildrenOf(model.Id).Any(o => o.Class == "Geometry");

        private NifTransform ReadTransform(FbxObject model)
        {
            (double tx, double ty, double tz) = model.Properties.GetVector3("Lcl Translation");
            (double rx, double ry, double rz) = model.Properties.GetVector3("Lcl Rotation");
            (double sx, double sy, double sz) = model.Properties.GetVector3("Lcl Scaling", 1.0);

            // NIF has no non-uniform scale, so a non-uniform one has to collapse.
            var scale = (float)((sx + sy + sz) / 3.0);

            if (Math.Abs(sx - sy) > 1e-4 || Math.Abs(sy - sz) > 1e-4)
                Warnings.Add($"{model.Name}: non-uniform scale ({sx:G4}, {sy:G4}, {sz:G4}) averaged to {scale:G4}");

            return new NifTransform(
                new NifVector3((float)tx, (float)ty, (float)tz),
                NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz),
                scale);
        }

        private void AttachChildren(NifItem node, List<NifItem> children)
        {
            if (children.Count == 0)
                return;

            if (_model.SetArraySize(node, "Num Children", "Children", children.Count) is not { } array)
                return;

            for (int i = 0; i < children.Count && i < array.Children.Count; i++)
                array.Children[i].Value.SetLink(_model.IndexOf(children[i]));
        }

        private void WriteVector3Array(NifItem block, string field, IReadOnlyList<NifVector3> values)
        {
            if (_model.FindItem(block, field) is not { } array)
                return;

            array.InvalidateConditionsRecursive();
            _model.UpdateArraySize(array);

            for (int i = 0; i < values.Count && i < array.Children.Count; i++)
                array.Children[i].Value.Set(values[i]);
        }

        /// <summary>
        /// Hands a skinned SE shape's geometry to its skin partition.
        /// </summary>
        /// <remarks>
        /// A skinned `BSTriShape` keeps nothing in itself: the vertices and the triangles
        /// live in the `NiSkinPartition` and the shape's own counts are zero. `NifToFbx`
        /// already reads it that way, following the skin when the shape holds nothing,
        /// and every skinned fixture but one is written that way.
        ///
        /// The import had nowhere else to put it. `NifSkinWriter.WriteSkinPartitions`
        /// sizes the per-partition arrays and never touches the block's own, so the
        /// geometry stayed on the shape and the partition came back empty — six fields
        /// reporting one cause.
        ///
        /// The one exception keeps a copy in both places, and which form a file used
        /// cannot be read back out of an FBX, so the exporter records it and this obeys
        /// it. Done after the fact rather than written there to begin with, because the
        /// partition does not exist until the skin is wired up, and that waits for every
        /// bone node to be built.
        /// </remarks>
        private void MoveGeometryIntoSkinPartition(NifItem shape, FbxObject? geometry)
        {
            if (_model.FindItem(shape, "Vertex Data") is not { Children.Count: > 0 } vertices)
                return;

            NifItem? skin = _model.GetRef(shape, "Skin");

            NifItem? partition = skin is null
                ? null
                : _model.GetRef(skin, "Skin Partition")
                  ?? (_model.GetRef(skin, "Data") is { } data ? _model.GetRef(data, "Skin Partition") : null);

            // Only the SSE form of the block has anywhere to put this.
            if (partition is null || _model.FindItem(partition, "Vertex Desc") is null)
                return;

            ulong descriptor = _model.FindItem(shape, "Vertex Desc")?.Value.ToUInt64() ?? 0;

            // nif.xml: the descriptor's low nibble is the vertex size in four-byte units,
            // and the array's length reads as `Data Size / Vertex Size`, so both have to
            // be right before it can be sized. The partition's descriptor is the shape's
            // -- checked against every skinned fixture, where the two always agree.
            var size = (uint)((descriptor & 0xF) * 4);

            if (size == 0)
                return;

            int count = vertices.Children.Count;

            _model.FindItem(partition, "Vertex Desc")?.Value.SetCount(descriptor);
            _model.FindItem(partition, "Vertex Size")?.Value.SetCount(size);
            _model.FindItem(partition, "Data Size")?.Value.SetCount((ulong)count * size);

            // Each partition entry repeats the descriptor. nif.xml gives SkinPartition
            // its own `Vertex Desc` beside the block's, and the game writes the same
            // value in both; only the block's was being set, so every entry came back
            // saying a vertex holds nothing.
            if (_model.FindItem(partition, "Partitions") is { } entries)
            {
                foreach (NifItem entry in entries.Children)
                    _model.FindItem(entry, "Vertex Desc")?.Value.SetCount(descriptor);
            }

            if (_model.FindItem(partition, "Vertex Data") is not { } target)
                return;

            target.InvalidateConditionsRecursive();
            _model.UpdateArraySize(target);

            for (int i = 0; i < count && i < target.Children.Count; i++)
                CopyValues(vertices.Children[i], target.Children[i]);

            // A file that kept both copies keeps both.
            if (geometry is not null
                && geometry.Properties.GetString(NifToFbx.ShapeKeepsGeometryProperty).Length > 0)
            {
                return;
            }

            // Data Size at zero is what empties the shape's own vertex array — nif.xml
            // makes the array conditional on it — and the triangles go with it.
            SetCount(shape, "Num Triangles", 0);
            SetCount(shape, "Data Size", 0);

            // But not the vertex count, if this is a dynamic shape. A
            // `BSDynamicTriShape` keeps its own second buffer of positions, sized by
            // `Num Vertices`, and the game's dynamic shapes carry that count while their
            // `Data Size` is zero. Zeroing it here threw the buffer away.
            if (!_model.BlockInherits(shape, "BSDynamicTriShape"))
                SetCount(shape, "Num Vertices", 0);

            foreach (string array in new[] { "Vertex Data", "Triangles" })
            {
                if (_model.FindItem(shape, array) is not { } item)
                    continue;

                item.InvalidateConditionsRecursive();
                _model.UpdateArraySize(item);
            }
        }

        /// <summary>Copies one item's values into another of the same shape.</summary>
        /// <remarks>
        /// Sizing the destination as it goes. A vertex holds fixed arrays of its own —
        /// four bone weights, four bone indices — and sizing the outer array does not
        /// reach them, so without this the recursion finds nothing to write into and the
        /// weights arrive as zeros while everything around them copies cleanly.
        /// </remarks>
        private void CopyValues(NifItem from, NifItem to)
        {
            to.Value = from.Value;

            if (to.Children.Count < from.Children.Count)
            {
                to.InvalidateConditionsRecursive();
                _model.UpdateArraySize(to);
            }

            for (int i = 0; i < from.Children.Count && i < to.Children.Count; i++)
                CopyValues(from.Children[i], to.Children[i]);
        }

        private void SetCount(NifItem block, string field, uint value) =>
            _model.FindItem(block, field)?.Value.SetCount(value);

        private void SetFloat(NifItem block, string field, float value) =>
            _model.FindItem(block, field)?.Value.SetFloat(value);

        private void SetBool(NifItem block, string field, bool value) =>
            _model.FindItem(block, field)?.Value.SetCount(value ? 1u : 0u);
    }
}
