using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a particle system through FBX as properties on its node.
    /// </summary>
    /// <remarks>
    /// FBX has no emitter, no modifier stack, and nothing that means what
    /// <c>NiPSysCylinderEmitter</c> means, so there is no conversion to make — only
    /// a choice between losing the system and carrying it across intact. ck-cmd
    /// makes the first choice: neither FBXWrangler nor HKXWrangler mentions
    /// particles, and a particle system exported through them comes back as a bare
    /// node.
    ///
    /// There is also no geometry to export. Skyrim's <c>NiPSysData</c> holds no
    /// vertices on disk — the corpus fixture has <c>Vertices = 0</c> and
    /// <c>BS Max Vertices = 18</c>, a capacity for a buffer the engine fills at
    /// runtime. ck-cmd's own NIF converter empties those arrays on purpose when it
    /// upgrades an older file, which is the same fact from the other side.
    ///
    /// So the node stays an empty, with its name, transform and animation, and the
    /// system rides along beside it.
    /// </remarks>
    public static class FbxParticleWriter
    {
        /// <summary>The property naming the particle system's block type.</summary>
        public const string TypeProperty = "particle_system";

        /// <summary>The property naming its data block's type.</summary>
        public const string DataTypeProperty = "particle_data";

        /// <summary>The property naming a modifier node's block type.</summary>
        /// <remarks>
        /// Also what marks the node as a modifier rather than a bone, so that the
        /// import walk does not turn the stack into eleven empty NiNodes.
        /// </remarks>
        public const string ModifierTypeProperty = "particle_modifier";

        /// <summary>The property carrying a modifier's own NIF name.</summary>
        /// <remarks>
        /// Separate from the node's name, which is sanitised for FBX and may have
        /// been renamed in a DCC tool. This is the name a controller binds to.
        /// </remarks>
        public const string ModifierNameProperty = "particle_modifier_name";

        /// <summary>The property naming a collider node's block type.</summary>
        /// <remarks>
        /// A collider manager holds a chain of colliders, each its own block. They
        /// hang under the manager for the same reason the modifiers hang under the
        /// system: the chain is a list, and a list is a thing a tree can show.
        /// </remarks>
        public const string ColliderTypeProperty = "particle_collider";

        /// <summary>Prefix on the system block's own fields.</summary>
        public const string SystemPrefix = "nps_";

        /// <summary>Prefix on the data block's fields.</summary>
        public const string DataPrefix = "npsd_";

        /// <summary>
        /// Suffix marking a property that names what a link pointed at.
        /// </summary>
        /// <remarks>
        /// A block index means nothing once exported, but the *name* of what it
        /// pointed at survives anything: an emitter object and a gravity object are
        /// named nodes, and a spawn modifier is a named modifier. Resolving by name is
        /// also what this project already does for skin bones, animation targets and
        /// constraint entities, so a particle system is not a special case.
        /// </remarks>
        public const string LinkSuffix = "_ref";

        /// <summary>
        /// Fields the node already carries, or that mean nothing outside the file.
        /// </summary>
        /// <remarks>
        /// The name and transform are the node's, and a count left behind without the
        /// array it sizes would make the rebuilt block claim references it has not
        /// got. A modifier's own name is carried separately, since the node's has been
        /// through FBX's naming rules.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Name", "Translation", "Rotation", "Scale",
            "Num Extra Data List", "Num Modifiers", "Num Properties"
        };

        /// <summary>
        /// Links the rebuild wires up for itself, so naming them would be redundant.
        /// </summary>
        /// <remarks>
        /// The system's own data and modifier list, and each modifier's pointer back
        /// to the system it belongs to. All three follow from the structure being
        /// rebuilt and cannot disagree with it.
        /// </remarks>
        private static readonly HashSet<string> StructuralLinks = new(StringComparer.Ordinal)
        {
            "Data", "Modifiers", "Target",

            // A collider's place in its chain and the manager it belongs to, both of
            // which the tree says already.
            "Collider", "Next Collider", "Parent"
        };

        /// <summary>Whether a block is a particle system this carries.</summary>
        public static bool IsParticleSystem(NifModel model, NifItem block) =>
            model.BlockInherits(block, "NiParticleSystem");

        /// <summary>
        /// Writes a particle system onto the node standing for it, with its modifier
        /// stack as child nodes.
        /// </summary>
        /// <remarks>
        /// One empty per modifier, in order, rather than one long list of properties
        /// on the system. The stack is then something a rigger can see and reorder in
        /// an outliner, and each modifier's fields are named as the file names them —
        /// <c>frame_count</c> rather than <c>npsm_7_frame_count</c>.
        ///
        /// Sibling order is the stack order. That is the point of putting them in the
        /// tree: moving one is meant to move it in the file too. The engine's own
        /// ordering still comes from each modifier's <c>Order</c> field, which is
        /// carried like any other, with array position breaking its ties.
        /// </remarks>
        public static void AddParticleSystem(
            FbxScene scene, FbxObject node, NifModel model, NifItem system)
        {
            node.Properties.SetUserString(TypeProperty, system.Name);

            Write(node, model, system, SystemPrefix);

            if (model.GetRef(system, "Data") is { } data)
            {
                node.Properties.SetUserString(DataTypeProperty, data.Name);
                Write(node, model, data, DataPrefix);
            }

            var inStack = model.GetRefArray(system, "Modifiers").ToList();

            foreach (NifItem modifier in inStack)
                AddModifier(scene, node, model, modifier, detached: false);

            // A modifier can point at another that is *not* in the stack. A
            // BSPSysHavokUpdateModifier names the rotation modifier it applies to the
            // debris it spawns, and that one is in nobody's Modifiers array -- so
            // walking the stack alone never reaches it and it was lost, three times
            // over in a dragon crash.
            foreach (NifItem modifier in inStack)
            {
                foreach (NifItem referenced in ReferencedModifiers(model, modifier))
                {
                    if (!inStack.Contains(referenced))
                        AddModifier(scene, node, model, referenced, detached: true);
                }
            }

            AddStructuralControllers(node, model, system);
        }

        /// <summary>The property counting the system's structural controllers.</summary>
        public const string ControllerCountProperty = FbxNodeControllers.CountProperty;

        /// <summary>Prefix on one structural controller's fields, before its index.</summary>
        public const string ControllerPrefix = FbxNodeControllers.Prefix;

        /// <summary>
        /// Carries the controllers on a particle system that animate nothing.
        /// </summary>
        /// <remarks>
        /// <c>NiPSysUpdateCtlr</c> is the switch that makes the system run at all, not
        /// animation, and the animation layer cannot carry it — that layer recognises
        /// a controller by what its interpolator drives, and this one has none (§5A.6).
        ///
        /// Nothing about this is particular to particle systems, and a skeleton's
        /// <c>BSLagBoneController</c> was lost for exactly the same reason, so the
        /// carrier itself lives in <see cref="FbxNodeControllers"/> and every node uses
        /// it. This is the particle system's call into it.
        /// </remarks>
        private static void AddStructuralControllers(FbxObject node, NifModel model, NifItem system) =>
            FbxNodeControllers.Write(node, model, system);

        /// <inheritdoc cref="FbxNodeControllers.Read"/>
        public static void ReadStructuralControllers(
            FbxObject node, NifModel model, NifItem system, List<string> warnings) =>
            FbxNodeControllers.Read(node, model, system, warnings);

        /// <summary>Whether a node stands for a particle modifier.</summary>
        public static bool IsModifierNode(FbxObject node) =>
            node.Properties.GetString(ModifierTypeProperty).Length > 0;

        /// <summary>Whether a node stands for one collider of a chain.</summary>
        public static bool IsColliderNode(FbxObject node) =>
            node.Properties.GetString(ColliderTypeProperty).Length > 0;

        /// <summary>The modifiers a modifier points at, which need not be in the stack.</summary>
        private static IEnumerable<NifItem> ReferencedModifiers(NifModel model, NifItem modifier)
        {
            foreach (NifItem child in modifier.Children)
            {
                if (child.Value.Type == NifValueType.Link
                    && model.GetBlock(child) is { } target
                    && model.BlockInherits(target, "NiPSysModifier"))
                {
                    yield return target;
                }
            }
        }

        /// <summary>
        /// The property marking a modifier that is not part of the stack.
        /// </summary>
        /// <remarks>
        /// It exists because another modifier names it, not because the system runs
        /// it, so it must come back as a block without joining the `Modifiers` array —
        /// joining would change what the system does.
        /// </remarks>
        public const string ModifierDetachedProperty = "particle_modifier_detached";

        /// <summary>Whether a modifier node is referenced rather than run.</summary>
        public static bool IsDetachedModifier(FbxObject node) =>
            node.Properties.GetString(ModifierDetachedProperty).Length > 0;

        private static void AddModifier(
            FbxScene scene, FbxObject parent, NifModel model, NifItem modifier, bool detached)
        {
            string name = model.GetString(modifier, "Name");

            FbxObject node = FbxMeshWriter.AddModel(
                scene,
                NameEncoding.Sanitize(name.Length > 0 ? name : modifier.Name),
                "Null",
                NifTransform.Identity);

            scene.Connect(node, parent);

            node.Properties.SetUserString(ModifierTypeProperty, modifier.Name);
            node.Properties.SetUserString(ModifierNameProperty, name);

            if (detached)
                node.Properties.SetUserString(ModifierDetachedProperty, "1");

            // No prefix: the node is the modifier, so there is nothing to
            // disambiguate it from.
            Write(node, model, modifier, string.Empty);

            AddColliders(scene, node, model, modifier);
        }

        /// <summary>Writes a collider manager's chain as children of its node.</summary>
        /// <remarks>
        /// Sibling order is chain order, as it is for the modifiers, so
        /// <c>Next Collider</c> is not carried: the tree says it.
        /// </remarks>
        private static void AddColliders(
            FbxScene scene, FbxObject parent, NifModel model, NifItem modifier)
        {
            int index = 0;

            for (NifItem? collider = model.GetRef(modifier, "Collider");
                 collider is not null;
                 collider = model.GetRef(collider, "Next Collider"))
            {
                // Colliders have no name of their own, so one is made from the type
                // and the position, which is all there is to tell them apart by.
                FbxObject node = FbxMeshWriter.AddModel(
                    scene, $"{collider.Name}_{index}", "Null", NifTransform.Identity);

                scene.Connect(node, parent);
                node.Properties.SetUserString(ColliderTypeProperty, collider.Name);

                Write(node, model, collider, string.Empty);
                index++;
            }
        }

        private static void Write(FbxObject node, NifModel model, NifItem block, string prefix)
        {
            NifFieldCodec.Write(
                model, block, prefix,
                (name, value) => node.Properties.SetUserString(name, value),
                child => Skipped.Contains(child.Name),
                (name, item) => WriteLink(node, model, block, name, item));
        }

        /// <summary>Records what a link pointed at, by name.</summary>
        private static void WriteLink(
            FbxObject node, NifModel model, NifItem block, string name, NifItem item)
        {
            if (StructuralLinks.Contains(item.Name))
                return;

            // A null link and a link to something nameless are the same thing here:
            // nothing to say, and a blank property would only look like a loss.
            if (model.GetBlock(item) is not { } target)
                return;

            // The unique name for a node, so several targets that share one can still
            // be told apart: a dragon crash spawns three debris meshes and every one
            // of them is called `NewRoot`.
            string targetName = model.BlockInherits(target, "NiAVObject")
                ? NifAnimAccess.TrackName(model, target)
                : model.GetName(target);

            if (targetName.Length > 0)
                node.Properties.SetUserString($"{name}{LinkSuffix}", targetName);
        }
    }
}
