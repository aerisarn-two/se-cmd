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

            FbxObject skinObject = scene.AddObject("Deformer", geometry.Name + "_skin", "Skin");

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

            FbxNode node = skinObject.Node;

            node.Nodes.Add(new FbxNode("Version", SkinVersion));
            node.Nodes.Add(new FbxNode("Link_DeformAcuracy", 50.0));
            node.Nodes.Add(new FbxNode("SkinningType", "Linear"));

            scene.Connect(skinObject, geometry);

            foreach (SkinBone bone in skin.Bones)
            {
                if (!bones.TryGetValue(bone.Name, out FbxObject? boneModel))
                {
                    problems.Add($"{bone.Name}: no node for this bone, its influence is dropped");
                    continue;
                }

                if (bone.Weights.Count == 0)
                    continue;

                FbxObject cluster = scene.AddObject("Deformer", bone.Name + "_cluster", "Cluster");
                FbxNode clusterNode = cluster.Node;

                clusterNode.Nodes.Add(new FbxNode("Version", ClusterVersion));
                clusterNode.Nodes.Add(new FbxNode("UserData", string.Empty, string.Empty));

                var indices = new int[bone.Weights.Count];
                var weights = new double[bone.Weights.Count];

                for (int i = 0; i < bone.Weights.Count; i++)
                {
                    indices[i] = bone.Weights[i].Vertex;
                    weights[i] = bone.Weights[i].Weight;
                }

                clusterNode.Nodes.Add(new FbxNode("Indexes", indices));
                clusterNode.Nodes.Add(new FbxNode("Weights", weights));

                // Transform is the mesh at bind time, TransformLink the bone.
                clusterNode.Nodes.Add(new FbxNode("Transform", ToMatrixArray(meshTransform)));
                clusterNode.Nodes.Add(new FbxNode("TransformLink", ToMatrixArray(bone.SkinTransform)));

                scene.Connect(cluster, skinObject);
                scene.Connect(boneModel, cluster);
            }

            return problems;
        }

        /// <summary>
        /// Reads the skin attached to a geometry, or null when it has none.
        /// </summary>
        public static SkinData? ReadSkin(FbxScene scene, FbxObject geometry)
        {
            FbxObject? skinObject = scene.ChildrenOf(geometry.Id)
                .FirstOrDefault(o => o.Class == "Deformer" && o.SubClass == "Skin");

            if (skinObject is null)
                return null;

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

                var indices = cluster.Child("Indexes")?.Properties.FirstOrDefault() as int[];
                var weights = cluster.Child("Weights")?.Properties.FirstOrDefault() as double[];

                if (indices is not null && weights is not null)
                {
                    int count = Math.Min(indices.Length, weights.Length);

                    for (int i = 0; i < count; i++)
                    {
                        if (weights[i] > 0)
                            bone.Weights.Add(((ushort)indices[i], (float)weights[i]));
                    }
                }

                skin.Bones.Add(bone);
            }

            return skin.IsEmpty ? null : skin;
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
