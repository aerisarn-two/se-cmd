using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries which kind of NIF node an FBX node stands for.
    /// </summary>
    /// <remarks>
    /// FBX has one kind of node, and NIF has a dozen that differ in what the engine
    /// does with them rather than in where they sit: a <c>BSOrderedNode</c> draws its
    /// children in a fixed order, a <c>BSMultiBoundNode</c> carries its own culling
    /// volume, a <c>BSLeafAnimNode</c> is a tree. Rebuilding all of them as
    /// <c>NiNode</c> loses that with nothing to show for it — the scene still has the
    /// right shape, and the engine treats it differently.
    ///
    /// The same applies to geometry, where the class decides what the engine does with
    /// the mesh rather than where it sits: a <c>BSDynamicTriShape</c> keeps a second
    /// vertex buffer the engine writes into every frame, which is how a cloak moves.
    ///
    /// The root matters most. <c>BSXFlags</c> asks twice whether the root is exactly
    /// <c>NiNode</c>, once for the external-skeleton test behind bit 0 and once for the
    /// root-collision test behind bit 3, so a body part whose root is rebuilt as
    /// <c>BSFadeNode</c> comes back claiming animation it does not have.
    /// </remarks>
    public static class FbxNodeType
    {
        /// <summary>The property the block type travels in.</summary>
        public const string Property = "nif_block_type";

        /// <summary>Prefix on the fields a specialised class adds to its base.</summary>
        public const string FieldPrefix = "nif_own_";

        /// <summary>Records which block an exported node came from.</summary>
        public static void Write(FbxObject node, NifItem block) =>
            node.Properties.SetUserString(Property, block.Name);

        /// <summary>
        /// Records the class *and* the fields it adds to its base.
        /// </summary>
        /// <remarks>
        /// Carrying a class without the thing the class is for is worse than not
        /// carrying it: a <c>BSLODTriShape</c> rebuilt without its triangle counts
        /// draws nothing at any distance, and a <c>BSOrderedNode</c> without its
        /// bound sorts against an empty one.
        ///
        /// Which fields those are is asked of the schema rather than listed here —
        /// everything the class declares that its base does not — so a class nobody
        /// has thought about yet is carried as completely as the ones that have been.
        /// Fields with their own carrier are left out, since two carriers writing the
        /// same field is how one of them ends up losing.
        /// </remarks>
        public static void WriteWithFields(
            FbxObject node, NifModel model, NifItem block, string baseClass, ISet<string>? except = null)
        {
            Write(node, block);

            foreach (NifFieldDef field in OwnFields(model, block.Name, baseClass))
            {
                if (except is not null && except.Contains(field.Name))
                    continue;

                if (model.FindItem(block, field.Name) is { Children.Count: 0 } item)
                    node.Properties.SetUserString(FieldPrefix + field.Name, NifFieldCodec.Format(model, item));
            }
        }

        /// <summary>Puts those fields back on a rebuilt block.</summary>
        public static void ReadFields(
            FbxObject node, NifModel model, NifItem block, string baseClass, ISet<string>? except = null)
        {
            foreach (NifFieldDef field in OwnFields(model, block.Name, baseClass))
            {
                if (except is not null && except.Contains(field.Name))
                    continue;

                if (node.Properties.GetString(FieldPrefix + field.Name) is { Length: > 0 } text
                    && model.FindItem(block, field.Name) is { Children.Count: 0 } item)
                {
                    NifFieldCodec.Assign(model, item, text);
                }
            }
        }

        /// <summary>What a class declares that its base does not.</summary>
        internal static IEnumerable<NifFieldDef> OwnFields(NifModel model, string blockName, string baseClass)
        {
            if (!model.KnowsBlock(blockName) || blockName == baseClass)
                return [];

            var inherited = model.Database.GetInheritedFields(baseClass)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);

            return model.Database.GetInheritedFields(blockName)
                .Where(f => !inherited.Contains(f.Name));
        }

        /// <summary>
        /// The block type to rebuild a node as.
        /// </summary>
        /// <remarks>
        /// A name only wins when the schema knows it and it really is a node, so a
        /// scene from elsewhere — or one whose property has been edited into something
        /// else — cannot turn a node into a shape or a controller.
        /// </remarks>
        /// <summary>
        /// Marks a node that stands for a shape with no vertices.
        /// </summary>
        /// <remarks>
        /// A shape with nothing in it is still a block. nif.xml says so outright about
        /// the commonest kind: a <c>BSProceduralLightningController</c> is "paired with
        /// dummy TriShapes", empty shapes the engine generates lightning into at
        /// runtime, and the game's staff bolts and rune projectiles are built from
        /// them. Exporting nothing lost the shape, its shader and its alpha property.
        ///
        /// FBX has no mesh with no vertices worth writing — a DCC tool given one shows
        /// an object that cannot be selected — so it travels as a plain node, and this
        /// is what says the node was a shape rather than a node.
        ///
        /// Marked explicitly rather than inferred from "a geometry class with no mesh
        /// attached", because that is also what an author typing a class name onto an
        /// ordinary node produces, and those are not the same thing.
        /// </remarks>
        public const string EmptyShapeProperty = "nif_empty_shape";

        /// <summary>Whether a node stands for a shape with no vertices.</summary>
        public static bool IsEmptyShape(FbxObject node) =>
            node.Properties.GetString(EmptyShapeProperty).Length > 0;

        /// <summary>
        /// Marks a node that is referenced but is not part of the scene tree.
        /// </summary>
        /// <remarks>
        /// A NIF can hold node subtrees that nothing parents and the scene graph never
        /// reaches, existing only because something points at them.
        /// `fxdragoncrashfurrow01` has three: a `BSPSysHavokUpdateModifier` names each
        /// as the debris its particles throw, and each is a node with a collision
        /// object and a shaded mesh under it.
        ///
        /// FBX has no way to hold an object that is in the file but not in the scene,
        /// so they are exported as scene roots and marked. On the way back the mark
        /// says: build this, and do **not** make it a child of the root — whatever
        /// pointed at it will claim it by name.
        /// </remarks>
        public const string DetachedProperty = "nif_detached";

        /// <summary>Whether a node is referenced rather than parented.</summary>
        public static bool IsDetached(FbxObject node) =>
            node.Properties.GetString(DetachedProperty).Length > 0;

        /// <summary>The property holding a block's real name, when FBX cannot.</summary>
        /// <remarks>
        /// Almost every node's name survives as the FBX object's own, through
        /// <see cref="NameEncoding"/>. One does not: a block with **no** name, which
        /// the game's cameras and a few effect nodes have. FBX has no anonymous
        /// object, so the export falls back to the class name — and without this the
        /// node came back called `NiCamera`.
        ///
        /// It also gives the animation layer something to bind to. That layer keys
        /// tracks by node name, and every unnamed node in a file is the same key.
        /// </remarks>
        public const string NameProperty = "nif_name";

        /// <summary>Records a name FBX cannot carry as the object's own.</summary>
        /// <remarks>
        /// Two cases, and both are about the FBX object being called something else.
        /// A block with **no** name is exported under its class; a block sharing a name
        /// with another is numbered, so a track can bind to one of them rather than to
        /// whichever came first.
        /// </remarks>
        public static void WriteName(FbxObject node, NifModel model, NifItem block)
        {
            if (model.FindItem(block, "Name") is null)
                return;

            string name = model.GetName(block);

            if (name != NameEncoding.Unsanitize(node.Name))
                node.Properties.SetUserString(NameProperty, name);
        }

        /// <summary>The name a node should be given, which is usually its own.</summary>
        public static string ReadName(FbxObject node, string fallback) =>
            node.Properties.Has(NameProperty) ? node.Properties.GetString(NameProperty) : fallback;

        /// <summary>The property an AV object's own flags travel in.</summary>
        /// <remarks>
        /// `Flags` sits on `NiAVObject`, below the class each node adds to, so the
        /// own-fields carrier above never sees it and every rebuilt node took nif.xml's
        /// default of 0x8000E.
        ///
        /// That default is not a safe assumption. Across 42,955 AV objects in a fifth
        /// of Skyrim's meshes the field takes **23** distinct values, and 0x8000E
        /// accounts for only 44% of them -- 0xE, the same flags without the 0x80000
        /// bit, accounts for another 44%. nif.xml says as much in its own comment:
        /// "FO4 lacks the 0x80000 flag always. Skyrim lacks it sometimes." Per class it
        /// is no better; `NiNode`'s commonest value covers 59.1% of 20,834 of them.
        ///
        /// The bits say whether a node is hidden, how it is culled, and whether it has
        /// a bounding volume, so a wrong one is not cosmetic.
        /// </remarks>
        public const string FlagsProperty = "nif_av_flags";

        /// <summary>Records an AV object's flags, when it has any.</summary>
        public static void WriteFlags(FbxObject node, NifModel model, NifItem block)
        {
            if (model.FindItem(block, "Flags") is not { Children.Count: 0 } flags)
                return;

            node.Properties.SetUserString(
                FlagsProperty,
                flags.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// What a node of a given class gets when nothing travelled with it.
        /// </summary>
        /// <remarks>
        /// A scene authored in a DCC tool carries no flags, and one value for every node
        /// is the wrong answer: the classes disagree, and they disagree about the bit
        /// that varies most. Measured over 102,000 AV objects in half of Skyrim's
        /// meshes, the commonest value per class, with its share:
        ///
        /// | Class | Flags | Share | Of |
        /// | --- | --- | --- | --- |
        /// | `NiNode` | `0xE` | 60.4% | 49,469 |
        /// | `BSTriShape` | `0x8000E` | 68.4% | 30,106 |
        /// | `BSDynamicTriShape` | `0xE` | 79.1% | 10,575 |
        /// | `BSFadeNode` | `0x8000E` | 65.7% | 9,135 |
        /// | `NiBillboardNode` | `0x8000E` | 43.6% | 1,012 |
        /// | `NiParticleSystem` | `0x8000E` | 67.1% | 777 |
        /// | `BSValueNode` | `0x8000E` | 87.5% | 337 |
        /// | `BSLeafAnimNode` | `0x8000E` | 55.1% | 138 |
        /// | `BSOrderedNode` | `0x8000E` | 37.3% | 126 |
        /// | `NiTriShape` | `0x8000E` | 71.5% | 123 |
        /// | `BSMultiBoundNode` | `0x8000E` | 75.0% | 120 |
        /// | `BSStripParticleSystem` | `0x4000E` | 39.8% | 93 |
        /// | `NiSwitchNode` | `0xE` | 61.7% | 81 |
        /// | `NiCamera` | `0x8000E` | 87.3% | 71 |
        /// | `BSMasterParticleSystem` | `0x8000E` | 95.7% | 46 |
        /// | `BSTreeNode` | `0x8080E` | 65.5% | 29 |
        /// | `BSLODTriShape` | `0x800000E` | 62.5% | 24 |
        /// | `BSBlastNode` | `0xF` | 63.2% | 19 |
        ///
        /// The two big node classes pull opposite ways -- `NiNode` towards `0xE` and
        /// `BSTriShape` towards `0x8000E` -- which is why a single default served
        /// neither, and why nif.xml's own `0x8000E` is right for barely half the file.
        ///
        /// nif.xml carries typed defaults for many of these and they mostly agree:
        /// `NiNode` 0xE, `BSTriShape` 0x8000E, `BSFadeNode` 0x8000E, `BSTreeNode`
        /// 0x8080E and `BSLODTriShape` 0x800000E all match. Where it differs the corpus
        /// wins, as it does everywhere else in this project: nif.xml gives
        /// `BSLeafAnimNode` 0x808000E where 55.1% of them are 0x8000E, `BSMultiBoundNode`
        /// 0xE where 75.0% are 0x8000E, and `BSBlastNode` 0x8000F where 63.2% are 0xF.
        ///
        /// Classes not listed keep nif.xml's `0x8000E`. Only classes with at least
        /// nineteen samples are here; below that the mode is not evidence.
        /// </remarks>
        private static readonly Dictionary<string, uint> ClassFlags = new(StringComparer.Ordinal)
        {
            ["NiNode"] = 0xE,
            ["BSDynamicTriShape"] = 0xE,
            ["NiSwitchNode"] = 0xE,
            ["BSTriShape"] = 0x8000E,
            ["BSFadeNode"] = 0x8000E,
            ["NiBillboardNode"] = 0x8000E,
            ["NiParticleSystem"] = 0x8000E,
            ["BSValueNode"] = 0x8000E,
            ["BSLeafAnimNode"] = 0x8000E,
            ["BSOrderedNode"] = 0x8000E,
            ["NiTriShape"] = 0x8000E,
            ["BSMultiBoundNode"] = 0x8000E,
            ["NiCamera"] = 0x8000E,
            ["BSMasterParticleSystem"] = 0x8000E,
            ["BSStripParticleSystem"] = 0x4000E,
            ["BSTreeNode"] = 0x8080E,
            ["BSLODTriShape"] = 0x800000E,
            ["BSBlastNode"] = 0xF
        };

        /// <summary>nif.xml's own default, for a class nothing was measured for.</summary>
        public const uint DefaultFlags = 0x8000E;

        /// <summary>The flags a node of this class is built with.</summary>
        public static uint DefaultFlagsFor(string blockClass) =>
            ClassFlags.TryGetValue(blockClass, out uint flags) ? flags : DefaultFlags;

        /// <summary>
        /// Puts a node's flags back, or gives it the ones its class usually has.
        /// </summary>
        public static void ReadFlags(FbxObject node, NifModel model, NifItem block)
        {
            if (model.FindItem(block, "Flags") is not { Children.Count: 0 } flags)
                return;

            flags.Value.SetCount(
                uint.TryParse(
                    node.Properties.GetString(FlagsProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out uint carried)
                    ? carried
                    : DefaultFlagsFor(block.Name));
        }

        public static string Read(FbxObject node, NifModel model, string fallback, string ancestor = "NiNode")
        {
            string name = node.Properties.GetString(Property);

            if (name.Length == 0 || !model.KnowsBlock(name) || !model.Database.Inherits(name, ancestor))
                return fallback;

            return name;
        }
    }
}
