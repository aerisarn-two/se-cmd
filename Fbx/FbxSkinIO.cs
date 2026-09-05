using LeanMeshIO.Formats.Fbx;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Reads and writes FBX skin deformers.
    /// </summary>
    /// <remarks>
    /// FBX models skinning as two object classes. A <c>Deformer</c> of subclass
    /// <c>Skin</c> hangs off the geometry, and one <c>Deformer</c> of subclass
    /// <c>Cluster</c> per bone hangs off the skin, carrying that bone's vertex
    /// indices and weights. The bone itself is an ordinary <c>Model</c>, connected
    /// to its cluster (spec §4.6).
    ///
    /// A cluster stores two matrices. <c>TransformLink</c> is the bone's world
    /// transform at bind time, <c>Transform</c> is the mesh's. Together they define
    /// the bind pose; swapping them is the usual cause of a mesh that explodes on
    /// the first frame.
    /// </remarks>
    public static class FbxSkinIO
    {
        /// <summary>The property counting the shape's body slots.</summary>
        /// <remarks>
        /// A slot says which part of a body a skin partition is, and it is the whole of
        /// the difference between the two skin instance classes: a shape with slots is
        /// a `BSDismemberSkinInstance`, one without is a plain `NiSkinInstance`. So the
        /// class is not carried — it follows from whether these are here, which means
        /// the two cannot disagree.
        ///
        /// ck-cmd carries none of this. Its export never mentions body parts, and its
        /// import sets every partition to `SBP_32_BODY` in a branch that cannot run.
        /// </remarks>
        public const string SlotCountProperty = "body_slots";

        /// <summary>Prefix on one slot, before its index.</summary>
        public const string SlotPrefix = "body_slot_";

        /// <summary>
        /// The property naming the shape's skin instance class.
        /// </summary>
        /// <remarks>
        /// Carried alongside the slots rather than derived from them, because the
        /// absence of slots means two different things. A shape that had a plain
        /// `NiSkinInstance` has none because that class has none; a shape authored in
        /// a DCC tool has none because nothing put them there. Deriving the class from
        /// an empty list rebuilds the first as a dismember instance, which is the
        /// thing this set out to fix.
        ///
        /// Slots remain the data; this is provenance, and it is only consulted when it
        /// is there.
        /// </remarks>
        public const string InstanceTypeProperty = "nif_skin_instance";

        /// <summary>
        /// The property naming which skin data block this skin shared.
        /// </summary>
        /// <remarks>
        /// Two shapes naming the same one get a single <c>NiSkinData</c> and a single
        /// <c>NiSkinPartition</c> back, as the game's own files have. See
        /// <see cref="SkinData.SkinDataId"/>.
        /// </remarks>
        public const string DataIdProperty = "nif_skin_data";

        /// <summary>Which partition a skin deformer stands for, in the file's order.</summary>
        public const string PartitionProperty = "nif_skin_partition";

        /// <summary>Which level of detail a partition draws at.</summary>
        /// <remarks>
        /// A per-partition fact, so it rides on the deformer that stands for the
        /// partition rather than with the whole-skin facts on the first one. Written
        /// only when it is not zero, which is every skin in the game but the 72 that
        /// belong to trees.
        /// </remarks>
        public const string PartitionLodProperty = "nif_partition_lod";

        /// <summary>Which entry of the skin's bone list a cluster is.</summary>
        /// <remarks>
        /// A skin's bone list may name one bone several times. 72 of the 26,940 skins
        /// the game ships do, and they are exactly the 72 with a partition above LOD 0:
        /// a tree gets its own set of entries per level, `treepineforest05` holding nine
        /// -- `[Trunk] [Trunk, C02, C04, Mid01] [Trunk, C02, C04, Mid01]` -- for four
        /// distinct bones.
        ///
        /// Which entry a cluster belongs to is not recoverable from the cluster. Keying
        /// on the bone's name and bind pose gets most of the way and no further, since a
        /// tree repeats a bone at the same pose in two levels; `treepineforestash01` has
        /// 22 clusters against 19 entries, so the entries are not one per cluster either
        /// and cannot simply be counted. So the entry travels.
        ///
        /// Absent -- a scene from a DCC, or one exported before this -- the reader falls
        /// back to name and pose, which is what it always did.
        /// </remarks>
        public const string BoneEntryProperty = "nif_bone_entry";

        /// <summary>The vertex count a bone's skin-data entry declared.</summary>
        /// <remarks>
        /// Only meaningful for a skin that kept its weights out of `NiSkinData`, where
        /// the count sits beside an array that is switched off and says whatever the file
        /// chose. See <see cref="SkinBone.DeclaredWeightCount"/>.
        /// </remarks>
        public const string DeclaredWeightsProperty = "nif_bone_declared_weights";

        /// <summary>Set when the source kept its weights out of `NiSkinData`.</summary>
        /// <remarks>
        /// Written only for the shapes that did, so a scene that came from anywhere else
        /// gets the ordinary arrangement -- weights in both copies -- which is what all
        /// but half a percent of the game's skins have.
        /// </remarks>
        public const string BufferWeightsProperty = "nif_skin_weights_buffer_only";

        /// <summary>Where the node a skin's bones are measured against rides.</summary>
        /// <remarks>
        /// `Skeleton Root` names the node the bind transforms are relative to. It was
        /// read off the skin and then dropped, and every rebuilt skin was pointed at
        /// the file's own root instead -- right for most meshes and wrong for every
        /// facegen head, whose skeleton root is a node one level down: 234 skins in a
        /// 1,500-mesh sample name a `NiNode` where the rebuild names the `BSFadeNode`
        /// above it.
        /// </remarks>
        public const string SkeletonRootProperty = "nif_skeleton_root";

        /// <summary>Where the skin's own bind transform rides.</summary>
        /// <remarks>
        /// `NiSkinData` carries one transform for the whole skin as well as one per
        /// bone, and only the per-bone ones have a place in FBX: a cluster's
        /// `TransformLink` is its bone at bind time and its `Transform` is the mesh.
        /// The mesh side is written as the identity here, and the bone transforms are
        /// built against that identity, so putting the skin's transform there instead
        /// would change what a DCC tool computes for the bind pose.
        ///
        /// So it travels beside the other whole-skin facts. Left behind it defaulted to
        /// the identity, and 485 of 574 remaining `Translation` differences in a
        /// 1,200-mesh sample were this one field: `akatoshamuletgo.nif` holds
        /// (0, -82.5534, 71.0324) and got (0, 0, 0).
        /// </remarks>
        public const string SkinTransformProperty = "nif_skin_transform";

        private const int SkinVersion = 101;
        private const int ClusterVersion = 100;

        /// <summary>
        /// Writes a skin and its clusters, connecting them to the geometry and to
        /// the bone models.
        /// </summary>
        /// <param name="bones">
        /// The bone models by name. A bone with no model is skipped and reported,
        /// since a cluster with no link deforms nothing.
        /// </param>
        public static List<string> AddSkin(
            FbxScene scene,
            FbxObject geometry,
            SkinData skin,
            IReadOnlyDictionary<string, FbxObject> bones,
            NifTransform meshTransform)
        {
            var problems = new List<string>();

            if (skin.IsEmpty)
                return problems;

            // One skin deformer per partition, which is how FBX says this and how
            // ck-cmd says it too: it counts a mesh's skin deformers to get the
            // partition count (`FBXWrangler.cpp:2826`) and creates one per partition
            // block on the way out (`:1046`). A partition is a set of bones and the
            // vertices they draw, and a deformer with its clusters is exactly that, so
            // nothing has to be invented to carry it.
            //
            // A shape with no partitions gets a single deformer holding everything,
            // which is what every unpartitioned skin already was.
            int count = Math.Max(1, skin.Partitions.Count);

            // Every vertex some partition names. A vertex map lists what its partition
            // *draws*, and a weighted vertex no triangle of any partition reaches is in
            // none of them -- 62 of the 322 in the Ebony Mail's first-person cuirass,
            // carrying 136 weights. Restricting each cluster to its own map therefore
            // dropped those weights entirely and the mesh came away from its bones.
            // They go to the first deformer, so that everything the skin holds is
            // written exactly once.
            var mapped = new HashSet<ushort>();

            foreach (SkinPartitionInfo part in skin.Partitions)
                mapped.UnionWith(part.Vertices);

            for (int p = 0; p < count; p++)
                AddOnePartition(scene, geometry, skin, bones, meshTransform, p, count, mapped, problems);

            return problems;
        }

        /// <summary>Writes one partition as a skin deformer and its clusters.</summary>
        private static void AddOnePartition(
            FbxScene scene,
            FbxObject geometry,
            SkinData skin,
            IReadOnlyDictionary<string, FbxObject> bones,
            NifTransform meshTransform,
            int index,
            int count,
            HashSet<ushort> mapped,
            List<string> problems)
        {
            SkinPartitionInfo? part = index < skin.Partitions.Count ? skin.Partitions[index] : null;

            // Which vertices this deformer carries the weights for. Null when the shape
            // was never partitioned, meaning every weight belongs to the one deformer.
            // Otherwise its own map, and -- for the first -- every weighted vertex no
            // map names at all, so that no weight goes unwritten.
            HashSet<ushort>? covered = null;

            if (part is { Vertices.Count: > 0 })
            {
                covered = [.. part.Vertices];

                if (index == 0)
                {
                    foreach (SkinBone bone in skin.Bones)
                        foreach ((ushort vertex, float _) in bone.Weights)
                            if (!mapped.Contains(vertex))
                                covered.Add(vertex);
                }
            }

            string suffix = count > 1
                ? "_skin" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "_skin";

            FbxObject skinObject = scene.AddObject("Deformer", geometry.Name + suffix, "Skin");

            scene.Connect(skinObject, geometry);

            // Which partition this is, so the reader keeps the order the file had
            // rather than the order the objects happen to come back in.
            skinObject.Properties.SetUserString(
                PartitionProperty, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // The level this slice draws at, which is a fact about the slice.
            if (part is { LodLevel: > 0 })
            {
                skinObject.Properties.SetUserString(
                    PartitionLodProperty,
                    part.LodLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // The whole-skin facts go on the first deformer only. They describe the
            // skin, not the slice, and repeating them on every partition would invite
            // a reader to wonder which copy is authoritative.
            if (index > 0)
            {
                WriteClusters(scene, skin, bones, meshTransform, part, covered, index, skinObject, problems);
                return;
            }

            // The class the shape had, when the scene came from a NIF at all.
            if (skin.InstanceType.Length > 0)
                skinObject.Properties.SetUserString(InstanceTypeProperty, skin.InstanceType);

            // Which copy of the weights the file kept. Only when it is the unusual one.
            if (!skin.WeightsInBoneList)
                skinObject.Properties.SetUserString(BufferWeightsProperty, "1");

            // The skin's own bind transform, which has nowhere else to go.
            if (!skin.SkinTransform.Equals(NifTransform.Identity))
                skinObject.Properties.SetUserString(SkinTransformProperty, Matrix(skin.SkinTransform));

            // ...and the node its bones are measured against.
            if (skin.SkeletonRoot.Length > 0)
                skinObject.Properties.SetUserString(SkeletonRootProperty, skin.SkeletonRoot);

            // Which skin data it shared, so two shapes that shared one still do.
            if (skin.SkinDataId >= 0)
            {
                skinObject.Properties.SetUserString(
                    DataIdProperty,
                    skin.SkinDataId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            // The body slots, named rather than numbered so a reader can check them.
            if (skin.BodySlots.Count > 0)
            {
                skinObject.Properties.SetUserString(
                    SlotCountProperty,
                    skin.BodySlots.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

                for (int i = 0; i < skin.BodySlots.Count; i++)
                {
                    (string slot, uint flags) = skin.BodySlots[i];

                    skinObject.Properties.SetUserString($"{SlotPrefix}{i}", slot);
                    skinObject.Properties.SetUserString(
                        $"{SlotPrefix}{i}_flags",
                        flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            WriteClusters(scene, skin, bones, meshTransform, part, covered, index, skinObject, problems);
        }

        /// <summary>Writes one deformer's clusters: its bones and their weights.</summary>
        private static void WriteClusters(
            FbxScene scene,
            SkinData skin,
            IReadOnlyDictionary<string, FbxObject> bones,
            NifTransform meshTransform,
            SkinPartitionInfo? part,
            HashSet<ushort>? covered,
            int index,
            FbxObject skinObject,
            List<string> problems)
        {
            FbxNode node = skinObject.Node;

            node.Nodes.Add(new FbxNode("Version", SkinVersion));
            node.Nodes.Add(new FbxNode("Link_DeformAcuracy", 50.0));
            node.Nodes.Add(new FbxNode("SkinningType", "Linear"));

            for (int b = 0; b < skin.Bones.Count; b++)
            {
                SkinBone bone = skin.Bones[b];

                // Which bones this deformer gets is decided by whether it has any of
                // their weights to write, checked below, and not by the partition's own
                // bone list. The two agree in the game's files, and where they do not it
                // is the weights that are the fact: a bone moving a vertex this
                // partition covers has to have a cluster here, or the weight is written
                // nowhere and the mesh comes away from its bones.

                if (!bones.TryGetValue(bone.Name, out FbxObject? boneModel))
                {
                    // Reported once, not once per partition: the bone is missing from
                    // the scene, which is one fault however many slices name it.
                    if (index == 0)
                        problems.Add($"{bone.Name}: no node for this bone, its influence is dropped");

                    continue;
                }

                List<(ushort Vertex, float Weight)> weightList = covered is null
                    ? bone.Weights
                    : [.. bone.Weights.Where(w => covered.Contains(w.Vertex))];

                // A bone the skin names but nothing weights is still part of the skin.
                // The bone list has an entry for it and the instance holds a reference,
                // so dropping its cluster drops the bone -- and with the weights now read
                // from the partition rather than from `NiSkinData`, a bone weighted only
                // in the latter has none. Seven meshes in a 2,000-mesh sample lost
                // exactly one bone that way.
                //
                // Written on the first deformer only, since one empty cluster is enough
                // to carry the bone across and one per partition would be noise. The
                // reader takes the bone from the cluster before it looks at the weights,
                // so an empty one arrives as a bone with nothing on it, which is what it
                // is.
                bool keepEmpty = bone.Weights.Count == 0 && index == 0;

                if (weightList.Count == 0 && !keepEmpty)
                    continue;

                FbxObject cluster = scene.AddObject("Deformer", bone.Name + "_cluster", "Cluster");
                FbxNode clusterNode = cluster.Node;

                // Which entry of the skin's bone list this is, which the cluster cannot
                // otherwise say when the list names one bone more than once.
                cluster.Properties.SetUserString(
                    BoneEntryProperty, b.ToString(System.Globalization.CultureInfo.InvariantCulture));

                if (bone.DeclaredWeightCount is { } declared)
                {
                    cluster.Properties.SetUserString(
                        DeclaredWeightsProperty,
                        declared.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                clusterNode.Nodes.Add(new FbxNode("Version", ClusterVersion));
                clusterNode.Nodes.Add(new FbxNode("UserData", string.Empty, string.Empty));

                var indices = new int[weightList.Count];
                var weights = new double[weightList.Count];

                for (int i = 0; i < weightList.Count; i++)
                {
                    indices[i] = weightList[i].Vertex;
                    weights[i] = weightList[i].Weight;
                }

                clusterNode.Nodes.Add(new FbxNode("Indexes", indices));
                clusterNode.Nodes.Add(new FbxNode("Weights", weights));

                // Transform is the mesh at bind time, TransformLink the bone.
                clusterNode.Nodes.Add(new FbxNode("Transform", ToMatrixArray(meshTransform)));
                clusterNode.Nodes.Add(new FbxNode("TransformLink", ToMatrixArray(bone.SkinTransform)));

                scene.Connect(cluster, skinObject);
                scene.Connect(boneModel, cluster);
            }
        }

        /// <summary>
        /// Reads the skin attached to a geometry, or null when it has none.
        /// </summary>
        public static SkinData? ReadSkin(FbxScene scene, FbxObject geometry)
        {
            // Every skin deformer on the geometry, not the first. FBX allows a mesh
            // several, and one per partition is how both this exporter and ck-cmd say
            // a skin was split -- taking only the first read a dismembered shape as a
            // single undivided one and lost the body parts with it.
            List<FbxObject> skinObjects = scene.ChildrenOf(geometry.Id)
                .Where(o => o.Class == "Deformer" && o.SubClass == "Skin")
                .OrderBy(o => PartitionIndexOf(o))
                .ToList();

            if (skinObjects.Count == 0)
                return null;

            // The whole-skin facts live on the first, which is the one that carries
            // them out.
            FbxObject skinObject = skinObjects[0];

            var skin = new SkinData
            {
                WeightsInBoneList =
                    skinObject.Properties.GetString(BufferWeightsProperty).Length == 0,
                InstanceType = skinObject.Properties.GetString(InstanceTypeProperty),
                SkinDataId = int.TryParse(
                    skinObject.Properties.GetString(DataIdProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int dataId)
                    ? dataId
                    : -1,
                SkinTransform = ParseMatrix(skinObject.Properties.GetString(SkinTransformProperty)),
                SkeletonRoot = skinObject.Properties.GetString(SkeletonRootProperty)
            };

            if (int.TryParse(
                    skinObject.Properties.GetString(SlotCountProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int slots))
            {
                for (int i = 0; i < slots; i++)
                {
                    string name = skinObject.Properties.GetString($"{SlotPrefix}{i}");

                    if (name.Length == 0)
                        continue;

                    uint.TryParse(
                        skinObject.Properties.GetString($"{SlotPrefix}{i}_flags"),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out uint flags);

                    skin.BodySlots.Add((name, flags));
                }
            }

            // One bone list for the whole skin, with each partition naming its share.
            // A bone that several partitions draw with is one bone here: the weights
            // are a fact about the vertex, and the split is a fact about the draw.
            //
            // Keyed on the bind pose as well as the name, because a name is not enough
            // to say two clusters are the same bone. Skyrim's conifers list one bone
            // several times with a different bind transform each: `treepineforest02`
            // gives TrunkBone three entries, two at (601.158, -4.757, -7.342) and one
            // at the origin, each moving a different third of the trunk. Keyed on the
            // name alone those collapse into one entry, and two thirds of the vertices
            // are then bound against a pose that was never theirs -- which is a mesh
            // visibly off its skeleton, not a rounding difference.
            var boneAt = new Dictionary<(string Name, string Pose), int>();
            var seen = new HashSet<(int Bone, ushort Vertex)>();

            // When every cluster says which entry it is, the list is built from that
            // instead, in the order the entries were numbered. Only then: a scene that
            // says nothing keeps the name-and-pose rule above, and one that says it for
            // some clusters and not others is not trusted for any of them, since a list
            // half built from entries and half from names would number neither right.
            Dictionary<int, int> byEntry = AllocateCarriedEntries(scene, skinObjects, skin);

            for (int p = 0; p < skinObjects.Count; p++)
            {
                var info = new SkinPartitionInfo
                {
                    LodLevel = uint.TryParse(
                        skinObjects[p].Properties.GetString(PartitionLodProperty),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out uint level)
                        ? level
                        : 0,
                };

                var covered = new SortedSet<ushort>();

                ReadOnePartition(scene, skinObjects[p], skin, boneAt, byEntry, seen, info, covered);

                info.Vertices.AddRange(covered);

                // Only when the scene actually said it was split. One deformer is what
                // an unpartitioned skin has always looked like, and calling that a
                // partition would put the shape's own split where there was none.
                if (skinObjects.Count > 1)
                    skin.Partitions.Add(info);
            }

            return skin.IsEmpty ? null : skin;
        }

        /// <summary>
        /// A bind pose as text, for telling two entries of one bone apart.
        /// </summary>
        /// <remarks>
        /// Rounded, because the pose makes the round trip through a matrix and back and
        /// need only be recognised, not reproduced. Six decimals is far finer than the
        /// hundreds of units that separate the poses this exists to distinguish, and
        /// far coarser than the drift decomposition introduces.
        /// </remarks>
        private static string Pose(NifTransform t)
        {
            System.Numerics.Matrix4x4 m = t.ToMatrix();

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{m.M11:F6},{m.M12:F6},{m.M13:F6},{m.M21:F6},{m.M22:F6},{m.M23:F6},"
                + $"{m.M31:F6},{m.M32:F6},{m.M33:F6},{m.M41:F6},{m.M42:F6},{m.M43:F6}");
        }

        /// <summary>The partition a skin deformer stands for, or its scene order.</summary>
        private static int PartitionIndexOf(FbxObject skinObject) =>
            int.TryParse(
                skinObject.Properties.GetString(PartitionProperty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int index)
                ? index
                : int.MaxValue;

        /// <summary>Reads one deformer's clusters into the shared bone list.</summary>
        /// <summary>The declared weight count a cluster carries, if any.</summary>
        private static uint? DeclaredWeightsOf(FbxObject cluster) =>
            uint.TryParse(
                cluster.Properties.GetString(DeclaredWeightsProperty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out uint declared)
                ? declared
                : null;

        /// <summary>The bone-list entry a cluster names, when it names one.</summary>
        private static int? EntryOf(FbxObject cluster) =>
            int.TryParse(
                cluster.Properties.GetString(BoneEntryProperty),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int entry) && entry >= 0
                ? entry
                : null;

        /// <summary>
        /// Builds the skin's bone list from the entries the clusters name, when they all
        /// name one.
        /// </summary>
        /// <remarks>
        /// Allocated in the order the entries were numbered rather than the order the
        /// clusters are met, so entry *n* here is entry *n* in the file it came from and
        /// every partition's bone index still means what it meant.
        ///
        /// A bone appearing under several entries is several entries, which is the whole
        /// point: it is how a tree gives each level of detail its own share of the list.
        /// </remarks>
        /// <returns>Carried entry to its place in the list, or empty when unavailable.</returns>
        private static Dictionary<int, int> AllocateCarriedEntries(
            FbxScene scene, List<FbxObject> skinObjects, SkinData skin)
        {
            var found = new SortedDictionary<int, FbxObject>();
            int clusters = 0;

            foreach (FbxObject skinObject in skinObjects)
            {
                foreach (FbxObject cluster in scene.ChildrenOf(skinObject.Id)
                             .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster"))
                {
                    clusters++;

                    if (EntryOf(cluster) is not { } entry)
                        return [];

                    // The first cluster naming an entry describes it; a later one for
                    // the same entry is another partition drawing with it.
                    found.TryAdd(entry, cluster);
                }
            }

            // A scene with no clusters says nothing either way.
            //
            // Every skin with clusters goes through here, not only the ones repeating a
            // bone. Restricting it to those was tried and is wrong: a tree's nine
            // clusters each name a *distinct* entry -- the repetition is of bones across
            // entries, not of entries across clusters -- so "more clusters than entries"
            // is exactly the test that misses it. The file's own numbering is the better
            // answer wherever it is available, and for a skin whose bones are one apiece
            // it is the same answer the name-and-pose rule gives.
            if (clusters == 0)
                return [];

            var byEntry = new Dictionary<int, int>(found.Count);

            foreach ((int entry, FbxObject cluster) in found)
            {
                if (scene.ChildrenOf(cluster.Id).FirstOrDefault(o => o.Class == "Model") is not { } boneModel)
                    return [];

                byEntry[entry] = skin.Bones.Count;

                skin.Bones.Add(new SkinBone
                {
                    Name = NameEncoding.Unsanitize(boneModel.Name),
                    SkinTransform = FromMatrixArray(cluster.Child("TransformLink")),
                    DeclaredWeightCount = DeclaredWeightsOf(cluster),
                });
            }

            return byEntry;
        }

        private static void ReadOnePartition(
            FbxScene scene,
            FbxObject skinObject,
            SkinData skin,
            Dictionary<(string Name, string Pose), int> boneAt,
            IReadOnlyDictionary<int, int> byEntry,
            HashSet<(int Bone, ushort Vertex)> seen,
            SkinPartitionInfo info,
            SortedSet<ushort> covered)
        {
            foreach (FbxObject cluster in scene.ChildrenOf(skinObject.Id)
                         .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster"))
            {
                // The bone is the Model connected into the cluster.
                FbxObject? boneModel = scene.ChildrenOf(cluster.Id).FirstOrDefault(o => o.Class == "Model");

                if (boneModel is null)
                    continue;

                var bone = new SkinBone
                {
                    // Decoded, not raw. FBX names cannot hold a space or a bracket, so
                    // a bone travels out as NPC_s_R_s_Thigh_s__ob_RThg_cb_ and has to
                    // come back as "NPC R Thigh [RThg]" to match the node it names.
                    // Left encoded it matches nothing, and since a skin whose bones all
                    // fail to resolve is dropped whole, every Skyrim body part loses
                    // its skinning without anything failing.
                    Name = NameEncoding.Unsanitize(boneModel.Name),
                    SkinTransform = FromMatrixArray(cluster.Child("TransformLink")),
                    DeclaredWeightCount = DeclaredWeightsOf(cluster),
                };

                int at;

                if (byEntry.Count > 0)
                {
                    // The entry the cluster named, already allocated.
                    if (EntryOf(cluster) is not { } entry || !byEntry.TryGetValue(entry, out at))
                        continue;
                }
                else
                {
                    // The same bone, in the same pose, in two partitions is one entry.
                    var key = (bone.Name, Pose(bone.SkinTransform));

                    if (!boneAt.TryGetValue(key, out at))
                    {
                        at = skin.Bones.Count;
                        boneAt[key] = at;
                        skin.Bones.Add(bone);
                    }
                }

                if (!info.Bones.Contains(at))
                    info.Bones.Add(at);

                var indices = cluster.Child("Indexes")?.Properties.FirstOrDefault() as int[];
                var weights = cluster.Child("Weights")?.Properties.FirstOrDefault() as double[];

                if (indices is null || weights is null)
                    continue;

                int count = Math.Min(indices.Length, weights.Length);

                for (int i = 0; i < count; i++)
                {
                    if (weights[i] <= 0)
                        continue;

                    var vertex = (ushort)indices[i];
                    covered.Add(vertex);

                    // A seam vertex is drawn by both partitions, so the same weight
                    // arrives twice. Added twice it would be counted twice, and the
                    // vertex would come back over-weighted towards that bone.
                    if (seen.Add((at, vertex)))
                        skin.Bones[at].Weights.Add((vertex, (float)weights[i]));
                }
            }
        }

        /// <summary>A transform as sixteen round-trippable numbers, for a property.</summary>
        private static string Matrix(NifTransform transform) =>
            string.Join(",", ToMatrixArray(transform)
                .Select(x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        /// <summary>The counterpart of <see cref="Matrix"/>, or the identity.</summary>
        private static NifTransform ParseMatrix(string text)
        {
            string[] parts = text.Split(',');

            if (parts.Length != 16)
                return NifTransform.Identity;

            var m = new float[16];

            for (int i = 0; i < 16; i++)
            {
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out m[i]))
                {
                    return NifTransform.Identity;
                }
            }

            return NifTransform.FromMatrix(new System.Numerics.Matrix4x4(
                m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
        }

        /// <summary>A transform as the row-major sixteen doubles FBX stores.</summary>
        private static double[] ToMatrixArray(NifTransform transform)
        {
            System.Numerics.Matrix4x4 m = transform.ToMatrix();

            return
            [
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44
            ];
        }

        private static NifTransform FromMatrixArray(FbxNode? node)
        {
            if (node?.Properties.FirstOrDefault() is not double[] { Length: 16 } m)
                return NifTransform.Identity;

            return NifTransform.FromMatrix(new System.Numerics.Matrix4x4(
                (float)m[0], (float)m[1], (float)m[2], (float)m[3],
                (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                (float)m[8], (float)m[9], (float)m[10], (float)m[11],
                (float)m[12], (float)m[13], (float)m[14], (float)m[15]));
        }
    }
}
