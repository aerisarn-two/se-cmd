using NIFSharp;

namespace SECmd.Nif
{
    /// <summary>
    /// Works out what a file's <c>BSXFlags</c> should say.
    /// </summary>
    /// <remarks>
    /// Every bit is a fact about the block graph — whether the file animates, whether
    /// it collides, whether it is a skeleton, whether its collision is one piece or
    /// many — so the value is derived rather than authored. Get it wrong and the mesh
    /// still loads and still looks right; the game simply treats it as something it is
    /// not.
    ///
    /// Follows ck-cmd's <c>calculateSkyrimBSXFlags</c>. See
    /// `docs/bsxflags-spec.md`, which records the bits, the two graph walks behind
    /// bits 5 and 7, and the places the original marks as uncertain.
    /// </remarks>
    public static class NifBsxFlags
    {
        /// <summary>The name the block always has.</summary>
        public const string BlockName = "BSX";

        /// <summary>The bits, by what they mean.</summary>
        public static class Bit
        {
            public const int Animated = 0;
            public const int Havok = 1;
            public const int Ragdoll = 2;
            public const int MultipleCollisions = 3;
            public const int AddonNode = 4;
            public const int EditorMarker = 5;
            public const int DynamicBodies = 6;
            public const int SingleChain = 7;
            public const int ExternalEmit = 9;
        }

        /// <summary>
        /// The block every walk starts from.
        /// </summary>
        /// <remarks>
        /// The footer names it, and it is not always block 0 — one corpus fixture
        /// exists purely to say so, with a <c>BSXFlags</c> sitting there instead. Half
        /// the bits are answers about the graph below the root, so starting from the
        /// wrong block answers a different question.
        /// </remarks>
        private static NifItem? RootOf(NifModel model)
        {
            if (model.FindItem(model.Footer, "Roots") is { Children.Count: > 0 } roots
                && model.GetBlock(roots.Children[0]) is { } named)
            {
                return named;
            }

            return model.Blocks.Count > 0 ? model.Blocks[0] : null;
        }

        /// <summary>Shader flag 1 bit 29, which is what bit 9 reports.</summary>
        private const uint ExternalEmittance = 1u << 29;

        private const uint QualityInvalid = 0;
        private const uint QualityFixed = 1;

        /// <summary>The value this file's <c>BSXFlags</c> should hold.</summary>
        public static uint Calculate(this NifModel model)
        {
            if (RootOf(model) is not { } root)
                return 0;

            uint flags = 0;

            int collisions = 0;
            int phantoms = 0;
            bool skeleton = false;
            bool skinned = false;
            bool multiBound = false;

            var bones = new HashSet<NifItem>();

            foreach (NifItem block in model.Blocks)
            {
                if (model.BlockInherits(block, "bhkCollisionObject"))
                    collisions++;

                if (model.BlockInherits(block, "bhkSPCollisionObject"))
                    phantoms++;

                if (model.BlockInherits(block, "bhkBlendCollisionObject"))
                    skeleton = true;

                if (model.BlockInherits(block, "NiSkinInstance"))
                {
                    skinned = true;

                    foreach (NifItem bone in model.GetRefArray(block, "Bones"))
                        bones.Add(bone);
                }

                if (block.Name == "BSMultiBound")
                    multiBound = true;
            }

            bool externalSkeleton = HasExternalSkeleton(model, root, skinned, bones);

            foreach (NifItem block in model.Blocks)
            {
                if (!skeleton && !externalSkeleton
                    && (model.BlockInherits(block, "NiTimeController")
                        || model.BlockInherits(block, "BSValueNode")))
                {
                    flags |= 1u << Bit.Animated;
                }

                if (model.BlockInherits(block, "bhkRigidBody") && (skeleton || IsDynamic(model, block)))
                    flags |= 1u << Bit.DynamicBodies;

                if (skeleton)
                    flags |= 1u << Bit.Ragdoll;

                if (model.BlockInherits(block, "BSValueNode")
                    || (model.BlockInherits(block, "NiNode")
                        && model.GetName(block).Contains("AddonNode", StringComparison.Ordinal)))
                {
                    flags |= 1u << Bit.AddonNode;
                }

                if ((model.BlockInherits(block, "BSLightingShaderProperty")
                     || model.BlockInherits(block, "BSEffectShaderProperty"))
                    && (model.GetUInt(block, "Shader Flags 1") & ExternalEmittance) != 0)
                {
                    flags |= 1u << Bit.ExternalEmit;
                }
            }

            if (IsSingleChain(model, root))
                flags |= 1u << Bit.SingleChain;

            if (HasEditorMarker(model, root))
                flags |= 1u << Bit.EditorMarker;

            if (collisions > 0 || phantoms > 0)
            {
                if (!skeleton && collisions > 0 && (!HasRootCollision(model, root, multiBound) || collisions > 1))
                    flags |= 1u << Bit.MultipleCollisions;

                flags |= 1u << Bit.Havok;
            }

            return flags;
        }

        /// <summary>
        /// Whether every bone the skin names lives outside this file.
        /// </summary>
        /// <remarks>
        /// A file skinned entirely to bones it does not contain is a body part meant
        /// to be attached to a skeleton, and bit 0 does not apply to it. The root has
        /// to be exactly <c>NiNode</c> rather than one of its subclasses, which is how
        /// the original tells an attachment from a scene.
        /// </remarks>
        private static bool HasExternalSkeleton(
            NifModel model, NifItem root, bool skinned, HashSet<NifItem> bones)
        {
            if (!skinned || !model.BlockInherits(root, "NiNode"))
                return false;

            foreach (NifItem child in model.GetChildren(root))
                bones.Remove(child);

            return bones.Count == 0 && root.Name == "NiNode";
        }

        private static bool IsDynamic(NifModel model, NifItem body)
        {
            uint quality = model.FindItem(body, "Quality Type") is { } inlined
                ? inlined.Value.ToUInt()
                : model.GetUInt(body, @"Rigid Body Info\Quality Type");

            return quality != QualityInvalid && quality != QualityFixed;
        }

        /// <summary>Whether the root itself carries the file's collision.</summary>
        private static bool HasRootCollision(NifModel model, NifItem root, bool multiBound)
        {
            if (root.Name == "BSTreeNode")
                return false;

            if (multiBound)
                return true;

            if (root.Name is not ("BSFadeNode" or "BSLeafAnimNode"))
                return false;

            return model.GetRef(root, "Collision Object") is { } collision
                   && model.BlockInherits(collision, "bhkCollisionObject");
        }

        // --- bit 7 ------------------------------------------------------------

        /// <summary>
        /// Whether the file is one collision, or one kinematic chain.
        /// </summary>
        /// <remarks>
        /// A chain of n bodies joined by n-1 constraints leaves one, which is what the
        /// subtraction below tests. Constraints are counted by distinct pairs of
        /// entities, so two joining the same two bodies count once.
        /// </remarks>
        private static bool IsSingleChain(NifModel model, NifItem root)
        {
            var counts = new ChainCounts();

            Walk(model, root, counts, [], childOfSwitch: false);

            bool single = counts.Collisions - counts.Constraints == 1;
            bool verified = single;

            if (counts.Phantoms > 0 && (single || counts.Collisions == 0))
                verified = true;

            if (counts.HasBranches)
            {
                verified = counts.Collisions == 0 && counts.Phantoms == 0
                    ? verified || counts.BranchesResult
                    : verified && counts.BranchesResult;
            }

            // Deliberately no "nothing at all counts as single" here. The original
            // has that only in the constructor used for a switch node's children, so
            // a file with no collision does not get this bit.
            return verified;
        }

        private sealed class ChainCounts
        {
            public int Collisions;
            public int Phantoms;
            public int Constraints;
            public bool HasBranches;
            public bool BranchesResult = true;
            public readonly HashSet<(NifItem?, NifItem?)> Pairs = [];
        }

        private static void Walk(
            NifModel model, NifItem block, ChainCounts counts, HashSet<NifItem> visited, bool childOfSwitch)
        {
            if (!visited.Add(block))
                return;

            if (block.Name == "NiSwitchNode")
            {
                counts.HasBranches = true;
                bool all = true;

                foreach (NifItem child in model.GetChildren(block))
                    all &= BranchIsSingle(model, child, visited);

                counts.BranchesResult = counts.BranchesResult || all;
            }

            if (model.BlockInherits(block, "bhkSPCollisionObject"))
                counts.Phantoms++;

            if (model.BlockInherits(block, "bhkCollisionObject"))
                counts.Collisions++;

            if (model.BlockInherits(block, "bhkConstraint"))
            {
                NifItem? a = model.FindItem(block, "Entity A") is { } ea ? model.GetBlock(ea) : null;
                NifItem? b = model.FindItem(block, "Entity B") is { } eb ? model.GetBlock(eb) : null;

                if (counts.Pairs.Add((a, b)))
                    counts.Constraints++;
            }

            foreach (NifItem reachable in Reachable(model, block))
                Walk(model, reachable, counts, visited, childOfSwitch);
        }

        /// <summary>One branch of a switch node, counted on its own.</summary>
        private static bool BranchIsSingle(NifModel model, NifItem branch, HashSet<NifItem> visited)
        {
            var counts = new ChainCounts();

            Walk(model, branch, counts, visited, childOfSwitch: true);

            bool single = counts.Collisions - counts.Constraints == 1;
            bool verified = single;

            if (counts.Phantoms > 0 && (single || counts.Collisions == 0))
                verified = true;

            if (counts.HasBranches)
            {
                verified = counts.Collisions == 0 && counts.Phantoms == 0
                    ? verified || counts.BranchesResult
                    : verified && counts.BranchesResult;
            }

            // The recursive form has this and the top-level one does not.
            if (counts.Phantoms == 0 && counts.Collisions == 0)
                verified = true;

            return verified;
        }

        // --- bit 5 ------------------------------------------------------------

        /// <summary>
        /// Whether an editor marker sits anywhere the editor would see it.
        /// </summary>
        /// <remarks>
        /// A marker inside a branch does not count, because only one branch shows at
        /// a time. For a switch node that means the first child only — the branch that
        /// is active by default — and for an ordered node it means none of them.
        /// </remarks>
        private static bool HasEditorMarker(NifModel model, NifItem root) =>
            FindMarker(model, root, [], insideBranch: false);

        private static bool FindMarker(
            NifModel model, NifItem block, HashSet<NifItem> visited, bool insideBranch)
        {
            if (!visited.Add(block))
                return false;

            bool found = false;

            if (block.Name == "NiSwitchNode")
            {
                var children = model.GetChildren(block).ToList();

                if (children.Count > 0)
                    found |= FindMarker(model, children[0], visited, insideBranch: false);

                foreach (NifItem child in children)
                    FindMarker(model, child, visited, insideBranch: true);

                return found;
            }

            if (block.Name == "BSOrderedNode")
            {
                foreach (NifItem child in model.GetChildren(block))
                    FindMarker(model, child, visited, insideBranch: true);

                return false;
            }

            if (!insideBranch
                && model.BlockInherits(block, "NiObjectNET")
                && model.GetName(block).Contains("EditorMarker", StringComparison.Ordinal))
            {
                found = true;
            }

            foreach (NifItem reachable in Reachable(model, block))
                found |= FindMarker(model, reachable, visited, insideBranch);

            return found;
        }

        /// <summary>
        /// The blocks a block points at, for the two walks above.
        /// </summary>
        /// <remarks>
        /// Every reference in the schema, wherever it sits — a plain field, a field
        /// inside a compound, an entry in an array. ck-cmd reaches these with a
        /// <c>RecursiveFieldVisitor</c>, which visits the object and then accepts over
        /// all of its valid fields, so anything short of the whole subtree counts a
        /// different graph than it does.
        ///
        /// Pointers are left out. They are the upward half of a two-way link — a
        /// collision object's <c>Target</c> naming the node that owns it — so following
        /// them would walk back out of the subtree being measured and into the rest of
        /// the file.
        /// </remarks>
        private static IEnumerable<NifItem> Reachable(NifModel model, NifItem block)
        {
            foreach (NifItem link in LinksUnder(block))
            {
                if (model.GetBlock(link) is { } target)
                    yield return target;
            }
        }

        /// <summary>Every reference leaf in a block's field tree.</summary>
        private static IEnumerable<NifItem> LinksUnder(NifItem item)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Value.Type == NifValueType.Link)
                {
                    yield return child;
                }
                else if (child.Value.Type != NifValueType.UpLink)
                {
                    foreach (NifItem nested in LinksUnder(child))
                        yield return nested;
                }
            }
        }
    }
}
