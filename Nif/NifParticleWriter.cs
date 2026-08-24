using SECmd.Fbx;

namespace SECmd.Nif
{
    /// <summary>
    /// Rebuilds a particle system from the properties on its node.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="FbxParticleWriter"/>. Nothing in ck-cmd does this,
    /// in either direction — a particle system exported through FBXWrangler comes
    /// back as a bare node with its emitter, its data and all of its modifiers gone.
    ///
    /// Links between blocks are carried by the name of what they pointed at, and
    /// resolved in two passes: a link naming one of this system's own modifiers is
    /// wired straight away, and one naming a node elsewhere in the scene has to wait
    /// until the whole tree exists, exactly as skins and animation do.
    /// </remarks>
    public static class NifParticleWriter
    {
        /// <summary>Reads what a node says about the particle system it stands for.</summary>
        public static bool HasParticleSystem(FbxObject node) =>
            node.Properties.GetString(FbxParticleWriter.TypeProperty).Length > 0;

        /// <summary>A link waiting for the node it names to exist.</summary>
        public readonly record struct PendingParticleLink(NifItem Link, string TargetName, string Context);

        /// <summary>
        /// Builds the system, its data block and its modifiers.
        /// </summary>
        /// <param name="pending">
        /// Collects links naming something outside this system, for the caller to
        /// resolve once the whole tree is built.
        /// </param>
        /// <returns>The system block, or null when the node carries none.</returns>
        public static NifItem? WriteParticleSystem(
            this NifModel model, FbxScene scene, FbxObject node, string name,
            List<string> warnings, List<PendingParticleLink> pending)
        {
            string type = node.Properties.GetString(FbxParticleWriter.TypeProperty);

            if (type.Length == 0)
                return null;

            if (!model.KnowsBlock(type))
            {
                warnings.Add($"{name}: unknown particle system type \"{type}\", it is dropped");
                return null;
            }

            var fields = Fields(node);
            var links = new List<(NifItem Link, string TargetName)>();

            NifItem system = model.InsertBlock(type);
            model.SetString(system, "Name", name);

            Read(model, system, fields, FbxParticleWriter.SystemPrefix, links);

            string dataType = node.Properties.GetString(FbxParticleWriter.DataTypeProperty);

            if (dataType.Length > 0 && model.KnowsBlock(dataType))
            {
                NifItem data = model.InsertBlock(dataType);
                Read(model, data, fields, FbxParticleWriter.DataPrefix, links);
                model.SetRef(system, "Data", data);
            }

            var modifiers = WriteModifiers(model, scene, node, system, name, warnings, links);

            ResolveLinks(model, links, modifiers, name, pending);

            // The controllers that animate nothing. They are not animation and the
            // animation layer cannot see them, so they travel with the structure.
            FbxParticleWriter.ReadStructuralControllers(node, model, system, warnings);

            return system;
        }

        /// <summary>
        /// Wires up what can be wired now, and defers the rest.
        /// </summary>
        /// <remarks>
        /// A link naming one of this system's own modifiers — an age-death modifier
        /// naming its spawn modifier — is resolvable the moment the stack exists. One
        /// naming a node is not: an emitter object may be a sibling the walk has not
        /// reached, so it waits for the tree.
        /// </remarks>
        private static void ResolveLinks(
            NifModel model,
            List<(NifItem Link, string TargetName)> links,
            IReadOnlyDictionary<string, NifItem> modifiers,
            string name,
            List<PendingParticleLink> pending)
        {
            foreach ((NifItem link, string target) in links)
            {
                if (modifiers.TryGetValue(target, out NifItem? modifier))
                    link.Value.SetLink(model.IndexOf(modifier));
                else
                    pending.Add(new PendingParticleLink(link, target, name));
            }
        }

        /// <summary>
        /// Builds the modifier stack from the child nodes standing for it.
        /// </summary>
        /// <remarks>
        /// Sibling order is stack order: a modifier moved in an outliner is meant to
        /// move in the file. Each modifier also points back at the system it belongs
        /// to, without which it is in the array and attached to nothing.
        /// </remarks>
        private static Dictionary<string, NifItem> WriteModifiers(
            NifModel model, FbxScene scene, FbxObject node, NifItem system,
            string name, List<string> warnings,
            List<(NifItem Link, string TargetName)> links)
        {
            var byName = new Dictionary<string, NifItem>(StringComparer.Ordinal);
            var built = new List<NifItem>();

            foreach (FbxObject child in scene.ChildrenOf(node.Id))
            {
                if (child.Class != "Model" || !FbxParticleWriter.IsModifierNode(child))
                    continue;

                string type = child.Properties.GetString(FbxParticleWriter.ModifierTypeProperty);

                if (!model.KnowsBlock(type))
                {
                    warnings.Add($"{name}: unknown particle modifier \"{type}\", it is dropped");
                    continue;
                }

                NifItem modifier = model.InsertBlock(type);

                // The node's own name has been through FBX's naming rules and may
                // have been changed in a DCC tool; this is the one a controller binds
                // to.
                string modifierName = child.Properties.GetString(FbxParticleWriter.ModifierNameProperty);

                model.SetString(modifier, "Name", modifierName);
                Read(model, modifier, Fields(child), string.Empty, links);

                model.SetRef(modifier, "Target", system);
                BuildColliders(model, scene, child, modifier, name, warnings, links);

                // A modifier that is only referenced is a block, not a step: it comes
                // back so whatever names it can find it, and stays out of the array
                // the system runs.
                if (!FbxParticleWriter.IsDetachedModifier(child))
                    built.Add(modifier);

                if (modifierName.Length > 0)
                    byName.TryAdd(modifierName, modifier);
            }

            if (model.SetArraySize(system, "Num Modifiers", "Modifiers", built.Count) is { } array)
            {
                for (int i = 0; i < built.Count && i < array.Children.Count; i++)
                    array.Children[i].Value.SetLink(model.IndexOf(built[i]));
            }

            return byName;
        }

        /// <summary>
        /// Rebuilds a collider manager's chain from the nodes under it.
        /// </summary>
        /// <remarks>
        /// Sibling order is chain order, so each collider is linked to the next and
        /// every one points back at the manager. A chain that lost its links is a set
        /// of colliders the manager never reaches.
        /// </remarks>
        private static void BuildColliders(
            NifModel model, FbxScene scene, FbxObject node, NifItem modifier,
            string name, List<string> warnings,
            List<(NifItem Link, string TargetName)> links)
        {
            NifItem? previous = null;

            foreach (FbxObject child in scene.ChildrenOf(node.Id))
            {
                if (child.Class != "Model" || !FbxParticleWriter.IsColliderNode(child))
                    continue;

                string type = child.Properties.GetString(FbxParticleWriter.ColliderTypeProperty);

                if (!model.KnowsBlock(type))
                {
                    warnings.Add($"{name}: unknown particle collider \"{type}\", it is dropped");
                    continue;
                }

                NifItem collider = model.InsertBlock(type);

                Read(model, collider, Fields(child), string.Empty, links);
                model.SetRef(collider, "Parent", modifier);

                if (previous is null)
                    model.SetRef(modifier, "Collider", collider);
                else
                    model.SetRef(previous, "Next Collider", collider);

                previous = collider;
            }
        }

        private static void Read(
            NifModel model, NifItem block, IReadOnlyDictionary<string, string> fields, string prefix,
            List<(NifItem Link, string TargetName)> links)
        {
            NifFieldCodec.Read(
                model, block, prefix,
                name => fields.GetValueOrDefault(name),

                // The name is the node's, and the counts are rewritten from what was
                // actually rebuilt rather than from what the source had.
                child => child.Name is "Name" or "Num Extra Data List" or "Num Modifiers" or "Num Properties",

                (name, item) =>
                {
                    if (fields.GetValueOrDefault($"{name}{FbxParticleWriter.LinkSuffix}") is { Length: > 0 } target)
                        links.Add((item, target));
                });
        }

        /// <summary>Every user property on the node, by name.</summary>
        private static Dictionary<string, string> Fields(FbxObject node)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (FbxProperty70 property in node.Properties.All)
            {
                if (property.IsUserDefined && property.Values.Count > 0)
                    fields[property.Name] = property.Values[0]?.ToString() ?? string.Empty;
            }

            return fields;
        }

    }
}
