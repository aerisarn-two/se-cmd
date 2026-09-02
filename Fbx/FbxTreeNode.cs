using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a <c>BSTreeNode</c>'s two lists of bones through FBX.
    /// </summary>
    /// <remarks>
    /// A tree's trunk and branches are bent by the engine rather than by a sequence, and
    /// `BSTreeNode` names the nodes it bends: `Bones 1` the trunk and `Bones` the
    /// branches. Both are `Ref` arrays, so neither survives as a carried field, and every
    /// rebuilt tree came back naming nothing.
    ///
    /// Not derivable, unlike the master particle system's list. The nodes named are not
    /// the tree node's children -- of the 55 `BSTreeNode`s the game ships, not one names
    /// its own children in either list -- but bones further down, and which of them count
    /// as branches is a decision the author made: `treepineforestash01` names one trunk
    /// and eight branches out of a subtree holding many more nodes.
    ///
    /// So the names travel, and are resolved against the rebuilt tree the same way every
    /// other pointer is.
    /// </remarks>
    public static class FbxTreeNode
    {
        /// <summary>The node the tree bends at the trunk.</summary>
        public const string TrunkProperty = "nif_tree_trunk";

        /// <summary>The nodes it bends as branches.</summary>
        public const string BranchesProperty = "nif_tree_branches";

        /// <summary>The separator between names, which cannot occur in one.</summary>
        private const char Separator = '\u001f';

        /// <summary>Records both lists, when the block is a tree node.</summary>
        public static void Write(FbxObject node, NifModel model, NifItem block)
        {
            if (block.Name != "BSTreeNode")
                return;

            Record(node, model, block, "Bones 1", TrunkProperty);
            Record(node, model, block, "Bones", BranchesProperty);
        }

        private static void Record(
            FbxObject node, NifModel model, NifItem block, string field, string property)
        {
            var names = model.GetRefArray(block, field)
                .Select(model.GetName)
                .Where(n => n.Length > 0)
                .ToList();

            if (names.Count > 0)
                node.Properties.SetUserString(property, string.Join(Separator, names));
        }

        /// <summary>Puts both lists back, deferring each name for the caller to resolve.</summary>
        /// <param name="aimAt">
        /// Takes a link and the name it should point at, once the whole tree is built.
        /// </param>
        public static void Read(
            FbxObject node, NifModel model, NifItem block, Action<NifItem, string> aimAt)
        {
            if (block.Name != "BSTreeNode")
                return;

            Restore(node, model, block, "Num Bones 1", "Bones 1", TrunkProperty, aimAt);
            Restore(node, model, block, "Num Bones 2", "Bones", BranchesProperty, aimAt);
        }

        private static void Restore(
            FbxObject node, NifModel model, NifItem block,
            string countField, string field, string property, Action<NifItem, string> aimAt)
        {
            string text = node.Properties.GetString(property);

            // No property at all leaves the block as the schema built it, which is what
            // a scene from anywhere else looks like.
            if (text.Length == 0)
                return;

            string[] names = text.Split(Separator);

            if (model.SetArraySize(block, countField, field, names.Length) is not { } array)
                return;

            for (int i = 0; i < names.Length && i < array.Children.Count; i++)
                aimAt(array.Children[i], names[i]);
        }
    }
}
