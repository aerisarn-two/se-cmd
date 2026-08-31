using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Reads a shape's skinning, whichever way the file stores it.
    /// </summary>
    /// <remarks>
    /// There are two places the weights can live, and which one is used depends on
    /// the edition (see the LE/SE differences in nif.xml):
    ///
    /// <list type="bullet">
    /// <item><c>NiSkinData</c>'s bone list holds, per bone, the vertices it moves
    /// and by how much. This is how Skyrim LE stores it, and it also carries the
    /// bind-pose transform for each bone.</item>
    /// <item>The <c>NiSkinPartition</c>'s vertex data holds, per vertex, up to four
    /// bone indices and weights. Skyrim SE stores it here — the partition owns the
    /// geometry too — and some files carry only this.</item>
    /// </list>
    ///
    /// Both are read, the bone list preferred because it is exact rather than
    /// limited to four influences, falling back to the partition when a file has
    /// weights only there.
    /// </remarks>
    public static class NifSkinAccess
    {
        /// <summary>The skin instance attached to a shape, or null when unskinned.</summary>
        public static NifItem? GetSkinInstance(this NifModel model, NifItem shape)
        {
            // BSTriShape calls the field Skin; NiGeometry calls it Skin Instance.
            NifItem? skin = model.GetRef(shape, "Skin Instance") ?? model.GetRef(shape, "Skin");

            return skin is not null && model.BlockInherits(skin, "NiSkinInstance") ? skin : null;
        }

        /// <summary>Reads a shape's skinning, or null when it has none.</summary>
        public static SkinData? ReadSkin(this NifModel model, NifItem shape)
        {
            NifItem? skin = model.GetSkinInstance(shape);

            if (skin is null)
                return null;

            var bones = model.GetRefArray(skin, "Bones").ToList();

            if (bones.Count == 0)
                return null;

            var result = new SkinData
            {
                InstanceType = skin.Name,

                // Which data block this skin used, so shapes that shared one still
                // share it after the trip. The game's facegen heads do: two scar
                // marks are the same weights on the same bone.
                SkinDataId = model.GetRef(skin, "Data") is { } shared ? model.IndexOf(shared) : -1
            };

            // The slots, by name rather than by number: the enum is what a reader can
            // check, and the numbers differ between creature skeletons.
            if (model.FindItem(skin, "Partitions") is { } slots)
            {
                foreach (NifItem slot in slots.Children)
                {
                    uint part = model.FindItem(slot, "Body Part")?.Value.ToUInt() ?? 0;
                    uint flags = model.FindItem(slot, "Part Flag")?.Value.ToUInt() ?? 0;

                    string name = model.Database.TryGetEnumOptionName("BSDismemberBodyPartType", part, out string n)
                        ? n
                        : part.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    result.BodySlots.Add((name, flags));
                }
            }

            if (model.GetRef(skin, "Skeleton Root") is { } root)
                result.SkeletonRoot = model.GetName(root);

            NifItem? data = model.GetRef(skin, "Data");

            if (data is not null)
                result.SkinTransform = ReadSkinTransform(model, data, "Skin Transform");

            foreach (NifItem bone in bones)
                result.Bones.Add(new SkinBone { Name = model.GetName(bone) });

            // The bone list is exact; the partition caps a vertex at four
            // influences, so only fall back to it.
            //
            // Falling back is also the only sign that the file left NiSkinData empty on
            // purpose, which a few dozen do, so it is recorded rather than merely acted
            // on -- otherwise a shape that kept its weights in one copy comes back with
            // them in both.
            if (!ReadWeightsFromBoneList(model, data, result))
            {
                result.WeightsInBoneList = false;
                ReadWeightsFromPartition(model, skin, result);
            }

            ReadPartitions(model, skin, result);

            return result.Bones.Any(b => b.Weights.Count > 0) ? result : null;
        }

        /// <summary>
        /// How the skin was split, one entry per partition.
        /// </summary>
        /// <remarks>
        /// Read even though the weights have already been taken from the bone list,
        /// because this is a different fact about the same skin: not who moves a vertex
        /// but which slice draws it. On a dismembered shape the slices are the body
        /// parts, and a shape rebuilt without them is one that cannot lose a limb.
        ///
        /// A partition's bone list is its own, holding indices into the skin's, so it
        /// is mapped through on the way out. Its vertex map is in the shape's own
        /// numbering already.
        /// </remarks>
        private static void ReadPartitions(NifModel model, NifItem skin, SkinData result)
        {
            NifItem? partition = model.GetRef(skin, "Skin Partition");

            if (partition is null && model.GetRef(skin, "Data") is { } data)
                partition = model.GetRef(data, "Skin Partition");

            if (partition is null || model.FindItem(partition, "Partitions") is not { } blocks)
                return;

            foreach (NifItem block in blocks.Children)
            {
                var info = new SkinPartitionInfo();

                if (model.FindItem(block, "Bones") is { } bones)
                {
                    foreach (NifItem bone in bones.Children)
                    {
                        var index = (int)bone.Value.ToUInt();

                        // A partition names bones by index into the skin's list, and a
                        // file that names one past the end is naming nothing.
                        if (index >= 0 && index < result.Bones.Count)
                            info.Bones.Add(index);
                    }
                }

                if (model.FindItem(block, "Vertex Map") is { } map)
                {
                    foreach (NifItem vertex in map.Children)
                        info.Vertices.Add((ushort)vertex.Value.ToUInt());
                }

                result.Partitions.Add(info);
            }
        }

        /// <summary>Weights as <c>NiSkinData</c> stores them, per bone.</summary>
        private static bool ReadWeightsFromBoneList(NifModel model, NifItem? data, SkinData skin)
        {
            if (data is null || model.FindItem(data, "Bone List") is not { } boneList)
                return false;

            bool any = false;

            for (int i = 0; i < boneList.Children.Count && i < skin.Bones.Count; i++)
            {
                NifItem entry = boneList.Children[i];

                skin.Bones[i].SkinTransform = ReadSkinTransform(model, entry, "Skin Transform");

                if (model.FindItem(entry, "Vertex Weights") is not { } weights)
                    continue;

                foreach (NifItem weight in weights.Children)
                {
                    var vertex = (ushort)model.GetUInt(weight, "Index");
                    float value = model.FindItem(weight, "Weight")?.Value.ToFloat() ?? 0f;

                    if (value <= 0f)
                        continue;

                    skin.Bones[i].Weights.Add((vertex, value));
                    any = true;
                }
            }

            return any;
        }

        /// <summary>
        /// Weights as the partition stores them, per vertex.
        /// </summary>
        /// <remarks>
        /// Bone indices here are partition-local, so they go through the partition's
        /// own bone list to reach the skin's. Vertices are partition-local too when
        /// a vertex map is present.
        /// </remarks>
        private static void ReadWeightsFromPartition(NifModel model, NifItem skin, SkinData result)
        {
            NifItem? partition = model.GetRef(skin, "Skin Partition");

            if (partition is null && model.GetRef(skin, "Data") is { } data)
                partition = model.GetRef(data, "Skin Partition");

            if (partition is null || model.FindItem(partition, "Partitions") is not { } partitions)
                return;

            foreach (NifItem entry in partitions.Children)
            {
                var localBones = new List<int>();

                if (model.FindItem(entry, "Bones") is { } bones)
                {
                    foreach (NifItem bone in bones.Children)
                        localBones.Add((int)bone.Value.ToUInt());
                }

                var vertexMap = new List<ushort>();

                if (model.FindItem(entry, "Vertex Map") is { } map)
                {
                    foreach (NifItem vertex in map.Children)
                        vertexMap.Add((ushort)vertex.Value.ToUInt());
                }

                NifItem? weights = model.FindItem(entry, "Vertex Weights");
                NifItem? indices = model.FindItem(entry, "Bone Indices");

                if (weights is null || indices is null)
                    continue;

                int count = Math.Min(weights.Children.Count, indices.Children.Count);

                for (int v = 0; v < count; v++)
                {
                    ushort vertex = v < vertexMap.Count ? vertexMap[v] : (ushort)v;

                    NifItem vertexWeights = weights.Children[v];
                    NifItem vertexIndices = indices.Children[v];

                    int influences = Math.Min(vertexWeights.Children.Count, vertexIndices.Children.Count);

                    for (int i = 0; i < influences; i++)
                    {
                        float weight = vertexWeights.Children[i].Value.ToFloat();

                        if (weight <= 0f)
                            continue;

                        int local = (int)vertexIndices.Children[i].Value.ToUInt();

                        if (local >= localBones.Count)
                            continue;

                        int bone = localBones[local];

                        if (bone >= 0 && bone < result.Bones.Count)
                            result.Bones[bone].Weights.Add((vertex, weight));
                    }
                }
            }
        }

        /// <summary>
        /// Reads an <c>NiTransform</c>, which stores rotation and scale separately
        /// from the translation rather than as a matrix.
        /// </summary>
        private static NifTransform ReadSkinTransform(NifModel model, NifItem parent, string field)
        {
            NifVector3 translation = model.FindItem(parent, $@"{field}\Translation")?.Value.Get<NifVector3>()
                                     ?? new NifVector3();

            NifMatrix33 rotation = model.FindItem(parent, $@"{field}\Rotation")?.Value.Get<NifMatrix33>()
                                   ?? NifMatrix33.Identity;

            NifItem? scale = model.FindItem(parent, $@"{field}\Scale");

            return new NifTransform(translation, rotation, scale?.Value.ToFloat() ?? 1f);
        }
    }
}
