using MeshIO.Formats.Fbx;
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

            for (int p = 0; p < count; p++)
                AddOnePartition(scene, geometry, skin, bones, meshTransform, p, count, problems);

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
            List<string> problems)
        {
            SkinPartitionInfo? part = index < skin.Partitions.Count ? skin.Partitions[index] : null;

            // Which vertices this partition draws. Null when the shape was never
            // partitioned, meaning every weight belongs to the one deformer.
            HashSet<ushort>? covered =
                part is { Vertices.Count: > 0 } ? [.. part.Vertices] : null;

            string suffix = count > 1
                ? "_skin" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "_skin";

            FbxObject skinObject = scene.AddObject("Deformer", geometry.Name + suffix, "Skin");

            scene.Connect(skinObject, geometry);

            // Which partition this is, so the reader keeps the order the file had
            // rather than the order the objects happen to come back in.
            skinObject.Properties.SetUserString(
                PartitionProperty, index.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

                // Only the bones this partition draws with. A partition names its own
                // sixty at most, and giving every deformer every bone would say the
                // shape was never split.
                if (part is { Bones.Count: > 0 } && !part.Bones.Contains(b))
                    continue;

                if (!bones.TryGetValue(bone.Name, out FbxObject? boneModel))
                {
                    // Reported once, not once per partition: the bone is missing from
                    // the scene, which is one fault however many slices name it.
                    if (index == 0)
                        problems.Add($"{bone.Name}: no node for this bone, its influence is dropped");

                    continue;
                }

                if (bone.Weights.Count == 0)
                    continue;

                List<(ushort Vertex, float Weight)> weightList = covered is null
                    ? bone.Weights
                    : [.. bone.Weights.Where(w => covered.Contains(w.Vertex))];

                if (weightList.Count == 0)
                    continue;

                FbxObject cluster = scene.AddObject("Deformer", bone.Name + "_cluster", "Cluster");
                FbxNode clusterNode = cluster.Node;

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
                InstanceType = skinObject.Properties.GetString(InstanceTypeProperty),
                SkinDataId = int.TryParse(
                    skinObject.Properties.GetString(DataIdProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int dataId)
                    ? dataId
                    : -1
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
            var boneAt = new Dictionary<string, int>(StringComparer.Ordinal);
            var seen = new HashSet<(int Bone, ushort Vertex)>();

            for (int p = 0; p < skinObjects.Count; p++)
            {
                var info = new SkinPartitionInfo();
                var covered = new SortedSet<ushort>();

                ReadOnePartition(scene, skinObjects[p], skin, boneAt, seen, info, covered);

                info.Vertices.AddRange(covered);

                // Only when the scene actually said it was split. One deformer is what
                // an unpartitioned skin has always looked like, and calling that a
                // partition would put the shape's own split where there was none.
                if (skinObjects.Count > 1)
                    skin.Partitions.Add(info);
            }

            return skin.IsEmpty ? null : skin;
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
        private static void ReadOnePartition(
            FbxScene scene,
            FbxObject skinObject,
            SkinData skin,
            Dictionary<string, int> boneAt,
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
                    SkinTransform = FromMatrixArray(cluster.Child("TransformLink"))
                };

                // The same bone in two partitions is one entry, found by name.
                if (!boneAt.TryGetValue(bone.Name, out int at))
                {
                    at = skin.Bones.Count;
                    boneAt[bone.Name] = at;
                    skin.Bones.Add(bone);
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
