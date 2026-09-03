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

            // **The partition is where the weights are read from, and `NiSkinData` is
            // the fallback.** It used to be the other way round, on the grounds that
            // "the bone list is exact" -- and it is not the one the game draws.
            //
            // A Skyrim SE mesh states its weights three times: `NiSkinData`'s per-bone
            // lists, the partition's per-vertex rows, and the vertex buffer the GPU
            // samples. The last two agree exactly -- 0 differences over 1,070,617 rows
            // of a 3,000-mesh sample -- and `NiSkinData` disagrees with both on about
            // 0.25% of vertices. Most of that is derivable (a vertex authored with more
            // than the four a row holds is trimmed to the four heaviest and
            // renormalised, which reproduces the shipped row for 97% of them), but
            // roughly 2,500 vertices per 3,000 meshes are a flat contradiction: the same
            // bones, both sides summing to one, different numbers. `horse.nif`, four
            // `_byoh` children's torsos and some 70 FaceGen heads carry most of them.
            //
            // Nothing derives those from `NiSkinData`, so reading it there converts a
            // mesh to weights the engine does not use. ck-cmd reads the partition
            // (`FBXWrangler.cpp:1093`, taking `part_data.vertexWeights` and using the
            // bone list only for `skinTransform`), and this now follows it.
            //
            // The cost is the other direction: `NiSkinData` is not bound by four
            // influences -- it stores per bone, so a vertex may appear in any number of
            // bone lists, and about 2,000 vertices per 3,000 meshes are authored with
            // five to eight. Those come through with the four the partition kept, which
            // is what the file ships and all a rebuilt NIF could hold anyway.
            bool fromPartition = ReadWeightsFromPartition(model, skin, result);

            // Always read, whichever copy the weights came from: a bone's own skin
            // transform lives here and nowhere else, as does the count a file left
            // beside an array it switched off.
            //
            // Whether `NiSkinData` carries weights at all is a fact about the source
            // rather than about where this chose to read them, and the writer needs it:
            // a few dozen files keep their weights out of the bone list on purpose, and
            // a shape that kept them in one copy must not come back with them in both.
            result.WeightsInBoneList = ReadBoneList(model, data, result, takeWeights: !fromPartition);

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
                var info = new SkinPartitionInfo
                {
                    LodLevel = model.FindItem(block, "LOD Level")?.Value.ToUInt() ?? 0,
                };

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

        /// <summary>
        /// The `NiSkinData` bone list: every bone's skin transform, and its weights when
        /// they are wanted.
        /// </summary>
        /// <remarks>
        /// The transforms are read whatever happens -- they are stated here and nowhere
        /// else. The weights are read only when the partition had none, since the
        /// partition is the copy the game draws from.
        /// </remarks>
        /// <returns>Whether the bone list carries weights, read or not.</returns>
        private static bool ReadBoneList(
            NifModel model, NifItem? data, SkinData skin, bool takeWeights)
        {
            if (data is null || model.FindItem(data, "Bone List") is not { } boneList)
                return false;

            bool any = false;

            for (int i = 0; i < boneList.Children.Count && i < skin.Bones.Count; i++)
            {
                NifItem entry = boneList.Children[i];

                skin.Bones[i].SkinTransform = ReadSkinTransform(model, entry, "Skin Transform");

                if (model.FindItem(entry, "Vertex Weights") is not { } weights)
                {
                    // The array is switched off, so the count beside it is whatever the
                    // file chose to leave there and cannot be worked out again.
                    skin.Bones[i].DeclaredWeightCount = model.GetUInt(entry, "Num Vertices");
                    continue;
                }

                foreach (NifItem weight in weights.Children)
                {
                    var vertex = (ushort)model.GetUInt(weight, "Index");
                    float value = model.FindItem(weight, "Weight")?.Value.ToFloat() ?? 0f;

                    if (value <= 0f)
                        continue;

                    any = true;

                    if (takeWeights)
                        skin.Bones[i].Weights.Add((vertex, value));
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
        /// <returns>Whether the partition carried any weights.</returns>
        private static bool ReadWeightsFromPartition(NifModel model, NifItem skin, SkinData result)
        {
            NifItem? partition = model.GetRef(skin, "Skin Partition");

            if (partition is null && model.GetRef(skin, "Data") is { } data)
                partition = model.GetRef(data, "Skin Partition");

            if (partition is null || model.FindItem(partition, "Partitions") is not { } partitions)
                return false;

            // A vertex on a seam is drawn by more than one partition, and each states its
            // weights in full. Added once per partition the vertex comes back weighted
            // twice over, which is the same trap `FbxSkinIO` guards on the way in.
            var seen = new HashSet<(int Bone, ushort Vertex)>();
            bool any = false;

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

                        if (bone >= 0 && bone < result.Bones.Count && seen.Add((bone, vertex)))
                        {
                            result.Bones[bone].Weights.Add((vertex, weight));
                            any = true;
                        }
                    }
                }
            }

            return any;
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
