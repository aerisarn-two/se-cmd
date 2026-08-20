using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Builds the blocks that describe a skin.
    /// </summary>
    /// <remarks>
    /// A skinned shape needs three blocks: a <c>BSDismemberSkinInstance</c> naming
    /// the bones, a <c>NiSkinData</c> holding the bind pose and the per-bone
    /// weights, and a <c>NiSkinPartition</c> holding the same weights arranged per
    /// vertex for the renderer.
    ///
    /// The partition is not optional. Skyrim renders skinned geometry from it, so a
    /// shape with weights only in the skin data draws unskinned — which looks like
    /// the mesh ignoring its skeleton entirely.
    /// </remarks>
    public static class NifSkinWriter
    {
        /// <summary>Skyrim reads at most four bone influences per vertex.</summary>
        public const int MaxInfluences = 4;

        /// <summary>
        /// The most bones a single partition may reference.
        /// </summary>
        /// <remarks>
        /// The skinning shader addresses bones through a fixed-size palette, so a
        /// partition naming more than this cannot be drawn. Splitting the mesh until
        /// each piece fits is the only way to skin something with more bones than
        /// the palette holds, which is why body-covering armour arrives already
        /// split across several partitions.
        /// </remarks>
        public const int MaxBonesPerPartition = 60;

        /// <summary>
        /// Writes a skin for a shape, given the bone nodes it refers to.
        /// </summary>
        /// <param name="boneNodes">The skeleton nodes, by name.</param>
        /// <param name="triangles">
        /// The shape's triangles. The partition carries its own copy, remapped to
        /// its local vertices, because that is what the renderer draws.
        /// </param>
        /// <returns>Names of bones that had no node, whose influence was dropped.</returns>
        public static List<string> WriteSkin(
            this NifModel model,
            NifItem shape,
            SkinData skin,
            IReadOnlyDictionary<string, NifItem> boneNodes,
            NifItem skeletonRoot,
            int vertexCount,
            IReadOnlyList<NifTriangle> triangles,
            string fallbackInstanceType = "BSDismemberSkinInstance",
            Dictionary<int, (NifItem Data, NifItem Partition)>? shared = null)
        {
            var missing = new List<string>();

            // Drop influences Skyrim cannot read, renormalising what is left.
            skin.LimitInfluences(MaxInfluences);

            var bones = new List<SkinBone>();
            var nodes = new List<NifItem>();

            foreach (SkinBone bone in skin.Bones)
            {
                if (!boneNodes.TryGetValue(bone.Name, out NifItem? node))
                {
                    if (bone.Weights.Count > 0)
                        missing.Add(bone.Name);

                    continue;
                }

                bones.Add(bone);
                nodes.Add(node);
            }

            if (bones.Count == 0)
                return missing;

            // The class the shape had, when the scene knows; otherwise the slots
            // decide, and failing both the caller's default. An empty slot list is not
            // enough on its own: a plain NiSkinInstance has none because its class has
            // none, and a mesh from a DCC tool has none because nothing put them
            // there, and those want different answers.
            string type =
                skin.InstanceType.Length > 0
                && model.KnowsBlock(skin.InstanceType)
                && model.Database.Inherits(skin.InstanceType, "NiSkinInstance")
                    ? skin.InstanceType
                    : skin.BodySlots.Count > 0
                        ? "BSDismemberSkinInstance"
                        : fallbackInstanceType;

            NifItem instance = model.InsertBlock(type);

            // Bethesda's files point two shapes at one skin data and one partition --
            // a facegen head's two scar marks are the same weights on the same bone --
            // so a shape whose skin named one already built gets that one rather than
            // a copy of it. Keyed on which block it was, not on what is in it: the
            // game also ships identical skins side by side on purpose (§5.2.1).
            (NifItem Data, NifItem Partition) found = default;

            bool reused = skin.SkinDataId >= 0
                          && shared is not null
                          && shared.TryGetValue(skin.SkinDataId, out found);

            NifItem data = reused ? found.Data : model.InsertBlock("NiSkinData");
            NifItem partition = reused ? found.Partition : model.InsertBlock("NiSkinPartition");

            if (!reused && skin.SkinDataId >= 0 && shared is not null)
                shared[skin.SkinDataId] = (data, partition);

            model.SetRef(instance, "Data", data);
            model.SetRef(instance, "Skin Partition", partition);
            model.SetRef(instance, "Skeleton Root", skeletonRoot);

            if (model.SetArraySize(instance, "Num Bones", "Bones", nodes.Count) is { } boneRefs)
            {
                for (int i = 0; i < nodes.Count && i < boneRefs.Children.Count; i++)
                    boneRefs.Children[i].Value.SetLink(model.IndexOf(nodes[i]));
            }

            var groups = SplitIntoPartitions(skin, bones.Count, vertexCount, triangles);

            // A reused block is already filled, by the shape that got there first.
            // Writing it again would be writing the same thing twice, and the second
            // shape's partitions are the first's -- that is what sharing means.
            if (!reused)
            {
                WriteSkinData(model, data, skin, bones);
                WriteSkinPartitions(model, partition, skin, bones, groups);
            }

            // One body-part entry per partition. The slots are what make this a
            // dismember instance rather than a plain skin, so they are written here,
            // once the partitions they describe are known.
            WriteBodySlots(model, instance, skin, groups.Count);

            // BSTriShape names the field Skin, NiGeometry names it Skin Instance.
            if (model.FindItem(shape, "Skin Instance") is not null)
                model.SetRef(shape, "Skin Instance", instance);
            else
                model.SetRef(shape, "Skin", instance);

            return missing;
        }

        /// <summary>One partition's share of the mesh.</summary>
        private sealed class PartitionGroup
        {
            /// <summary>Global vertex indices, in the order the partition lists them.</summary>
            public List<ushort> Vertices { get; } = [];

            /// <summary>Skin bone indices this partition references.</summary>
            public List<int> Bones { get; } = [];

            /// <summary>Triangles in global vertex indices; remapped when written.</summary>
            public List<NifTriangle> Triangles { get; } = [];
        }

        /// <summary>
        /// Divides a mesh into partitions each referencing no more bones than the
        /// shader palette holds.
        /// </summary>
        /// <remarks>
        /// Triangles are the unit of division, since a triangle cannot be drawn by
        /// two partitions. Each is placed in the first partition whose bone set can
        /// still absorb its bones, which keeps the count low without the cost of
        /// searching for an optimal packing — the aim is only to fit the palette,
        /// not to minimise partitions.
        ///
        /// A mesh whose bones already fit is left whole, both because splitting it
        /// would gain nothing and because that keeps the common case identical to
        /// what it was before splitting existed.
        /// </remarks>
        private static List<PartitionGroup> SplitIntoPartitions(
            SkinData skin, int boneCount, int vertexCount, IReadOnlyList<NifTriangle> triangles)
        {
            var byVertex = skin.ByVertex();

            // The common case: everything fits, so the partition is the whole mesh
            // and the vertex map is the identity.
            if (boneCount <= MaxBonesPerPartition)
            {
                var whole = new PartitionGroup();

                for (int i = 0; i < vertexCount; i++)
                    whole.Vertices.Add((ushort)i);

                for (int i = 0; i < boneCount; i++)
                    whole.Bones.Add(i);

                whole.Triangles.AddRange(triangles);

                return [whole];
            }

            var groups = new List<PartitionGroup>();
            var boneSets = new List<HashSet<int>>();

            foreach (NifTriangle triangle in triangles)
            {
                var needed = new HashSet<int>();

                foreach (ushort vertex in new[] { triangle.V1, triangle.V2, triangle.V3 })
                {
                    if (byVertex.TryGetValue(vertex, out var influences))
                    {
                        foreach ((int bone, float _) in influences)
                            needed.Add(bone);
                    }
                }

                int at = -1;

                for (int i = 0; i < groups.Count; i++)
                {
                    // Counting the union rather than adding first, so a triangle
                    // that would overflow does not corrupt the set it was tested
                    // against.
                    int union = boneSets[i].Count + needed.Count(b => !boneSets[i].Contains(b));

                    if (union <= MaxBonesPerPartition)
                    {
                        at = i;
                        break;
                    }
                }

                if (at < 0)
                {
                    groups.Add(new PartitionGroup());
                    boneSets.Add([]);
                    at = groups.Count - 1;
                }

                groups[at].Triangles.Add(triangle);
                boneSets[at].UnionWith(needed);
            }

            // Each partition lists only the vertices and bones it actually uses.
            for (int i = 0; i < groups.Count; i++)
            {
                var used = new SortedSet<ushort>();

                foreach (NifTriangle triangle in groups[i].Triangles)
                {
                    used.Add(triangle.V1);
                    used.Add(triangle.V2);
                    used.Add(triangle.V3);
                }

                groups[i].Vertices.AddRange(used);
                groups[i].Bones.AddRange(boneSets[i].Order());
            }

            return groups.Count > 0 ? groups : [new PartitionGroup()];
        }

        /// <summary>Writes the bind pose and the per-bone weights.</summary>
        /// <summary>
        /// Writes the body slots a dismember instance carries.
        /// </summary>
        /// <remarks>
        /// One entry per skin partition, saying which part of a body that partition
        /// is. The engine reads them to hide the body under a cuirass and to take a
        /// limb off, so a shape that has them and a shape that does not are different
        /// things rather than the same thing written two ways.
        ///
        /// Slots arrive by name, since the numbers differ between creature skeletons
        /// and a name is what a reader can check. One that is not in the schema's enum
        /// is parsed as a number, so a slot from a skeleton this build does not know
        /// still survives.
        /// </remarks>
        private static void WriteBodySlots(NifModel model, NifItem instance, SkinData skin, int partitions)
        {
            if (skin.BodySlots.Count == 0 || partitions == 0)
                return;

            // One entry per partition, not per carried slot: the two agree when the
            // file came from a NIF whose partitions were rebuilt the same way, and
            // when they do not, the array has to match the partitions it describes.
            if (model.SetArraySize(instance, "Num Partitions", "Partitions", partitions)
                is not { } slots)
            {
                return;
            }

            // A freshly sized array holds elements that have not been expanded into
            // their own fields yet, and writing into one of those writes nowhere.
            slots.InvalidateConditionsRecursive();
            model.UpdateArraySize(slots);

            for (int i = 0; i < slots.Children.Count; i++)
            {
                // A partition past the end of the carried list takes the last slot,
                // which is better than the torso every one of them used to get.
                (string name, uint flags) = skin.BodySlots[Math.Min(i, skin.BodySlots.Count - 1)];

                uint part =
                    model.Database.TryGetEnumOptionValue("BSDismemberBodyPartType", name, out uint value)
                        ? value
                        : uint.TryParse(name, System.Globalization.NumberStyles.Integer,
                                        System.Globalization.CultureInfo.InvariantCulture, out uint raw)
                            ? raw
                            : 0;

                Field(slots.Children[i], "Body Part")?.Value.SetCount(part);
                Field(slots.Children[i], "Part Flag")?.Value.SetCount(flags);
            }
        }

        /// <summary>A compound's own field, by name.</summary>
        private static NifItem? Field(NifItem item, string name) =>
            item.Children.FirstOrDefault(c => c.Name == name);

        private static void WriteSkinData(NifModel model, NifItem data, SkinData skin, List<SkinBone> bones)
        {
            WriteTransform(model, data, "Skin Transform", skin.SkinTransform);

            model.FindItem(data, "Has Vertex Weights")?.Value.SetCount(1);

            if (model.SetArraySize(data, "Num Bones", "Bone List", bones.Count) is not { } boneList)
                return;

            for (int i = 0; i < bones.Count && i < boneList.Children.Count; i++)
            {
                NifItem entry = boneList.Children[i];
                SkinBone bone = bones[i];

                WriteTransform(model, entry, "Skin Transform", bone.SkinTransform);

                if (model.SetArraySize(entry, "Num Vertices", "Vertex Weights", bone.Weights.Count)
                    is not { } weights)
                {
                    continue;
                }

                for (int w = 0; w < bone.Weights.Count && w < weights.Children.Count; w++)
                {
                    NifItem slot = weights.Children[w];
                    model.FindItem(slot, "Index")?.Value.SetCount(bone.Weights[w].Vertex);
                    model.FindItem(slot, "Weight")?.Value.SetFloat(bone.Weights[w].Weight);
                }
            }
        }

        /// <summary>
        /// Writes the partitions the renderer draws: the same weights, arranged per
        /// vertex with a fixed four slots each.
        /// </summary>
        private static void WriteSkinPartitions(
            NifModel model, NifItem partition, SkinData skin, List<SkinBone> bones, List<PartitionGroup> groups)
        {
            if (model.SetArraySize(partition, "Num Partitions", "Partitions", groups.Count)
                is not { } partitions)
            {
                return;
            }

            var byVertex = skin.ByVertex();

            for (int p = 0; p < groups.Count && p < partitions.Children.Count; p++)
                WriteOnePartition(model, partitions.Children[p], groups[p], byVertex);
        }

        private static void WriteOnePartition(
            NifModel model,
            NifItem entry,
            PartitionGroup group,
            Dictionary<ushort, List<(int Bone, float Weight)>> byVertex)
        {
            // Everything inside a partition is addressed locally, so build the two
            // translations from global indices first.
            var localVertex = new Dictionary<ushort, ushort>();

            for (int i = 0; i < group.Vertices.Count; i++)
                localVertex[group.Vertices[i]] = (ushort)i;

            var localBone = new Dictionary<int, int>();

            for (int i = 0; i < group.Bones.Count; i++)
                localBone[group.Bones[i]] = i;

            model.FindItem(entry, "Num Vertices")?.Value.SetCount((uint)group.Vertices.Count);
            model.FindItem(entry, "Num Triangles")?.Value.SetCount((uint)group.Triangles.Count);
            model.FindItem(entry, "Num Bones")?.Value.SetCount((uint)group.Bones.Count);
            model.FindItem(entry, "Num Weights Per Vertex")?.Value.SetCount(MaxInfluences);
            model.FindItem(entry, "Num Strips")?.Value.SetCount(0);
            model.FindItem(entry, "Has Vertex Map")?.Value.SetCount(1);
            model.FindItem(entry, "Has Vertex Weights")?.Value.SetCount(1);
            model.FindItem(entry, "Has Bone Indices")?.Value.SetCount(1);
            model.FindItem(entry, "Has Faces")?.Value.SetCount(1);

            // The partition reaches the skin's bones through this list.
            if (model.SetArraySize(entry, "Num Bones", "Bones", group.Bones.Count) is { } boneList)
            {
                for (int i = 0; i < group.Bones.Count && i < boneList.Children.Count; i++)
                    boneList.Children[i].Value.SetCount((uint)group.Bones[i]);
            }

            // ...and the shape's vertices through this one, which is also what
            // translates the triangle indices back on the way in.
            if (model.SetArraySize(entry, "Num Vertices", "Vertex Map", group.Vertices.Count) is { } map)
            {
                for (int i = 0; i < group.Vertices.Count && i < map.Children.Count; i++)
                    map.Children[i].Value.SetCount(group.Vertices[i]);
            }

            var local = group.Triangles.Select(t => new NifTriangle(
                localVertex.GetValueOrDefault(t.V1),
                localVertex.GetValueOrDefault(t.V2),
                localVertex.GetValueOrDefault(t.V3))).ToList();

            WriteTriangles(model, entry, "Triangles", local);

            // Special Edition repeats the triangles at the end of the partition,
            // both counted by the same Num Triangles. Filling only the first leaves
            // the copy a run of degenerate triangles rather than the mesh.
            WriteTriangles(model, entry, "Triangles Copy", local);

            // Both of these are two-dimensional: one row per vertex, each holding
            // Num Weights Per Vertex slots. Sizing the outer array creates the rows
            // but leaves them empty, and a weight cannot be written into a slot that
            // does not exist yet, so each row has to be sized too.
            NifItem? weights = SizeGrid(model, entry, "Vertex Weights");
            NifItem? indices = SizeGrid(model, entry, "Bone Indices");

            for (int v = 0; v < group.Vertices.Count; v++)
            {
                byVertex.TryGetValue(group.Vertices[v], out List<(int Bone, float Weight)>? influences);

                for (int slot = 0; slot < MaxInfluences; slot++)
                {
                    bool present = influences is not null && slot < influences.Count;

                    float weight = present ? influences![slot].Weight : 0f;

                    // Bone indices are local to the partition, not to the skin.
                    uint bone = present ? (uint)localBone.GetValueOrDefault(influences![slot].Bone) : 0u;

                    if (weights is not null && v < weights.Children.Count
                        && slot < weights.Children[v].Children.Count)
                    {
                        weights.Children[v].Children[slot].Value.SetFloat(weight);
                    }

                    if (indices is not null && v < indices.Children.Count
                        && slot < indices.Children[v].Children.Count)
                    {
                        indices.Children[v].Children[slot].Value.SetCount(bone);
                    }
                }
            }
        }

        /// <summary>
        /// Fills one of a partition's triangle arrays, if the version has it.
        /// </summary>
        private static void WriteTriangles(
            NifModel model, NifItem entry, string field, List<NifTriangle> triangles)
        {
            if (model.SetArraySize(entry, "Num Triangles", field, triangles.Count) is not { } array)
                return;

            for (int i = 0; i < triangles.Count && i < array.Children.Count; i++)
                array.Children[i].Value.Set(triangles[i]);
        }

        /// <summary>
        /// Sizes a two-dimensional array and every row inside it.
        /// </summary>
        private static NifItem? SizeGrid(NifModel model, NifItem parent, string field)
        {
            if (model.FindItem(parent, field) is not { } array)
                return null;

            array.InvalidateConditionsRecursive();
            model.UpdateArraySize(array);

            foreach (NifItem row in array.Children)
                model.UpdateArraySize(row);

            return array;
        }

        /// <summary>Writes an <c>NiTransform</c>, whose parts are stored separately.</summary>
        private static void WriteTransform(NifModel model, NifItem parent, string field, NifTransform transform)
        {
            model.FindItem(parent, $@"{field}\Translation")?.Value.Set(transform.Translation);
            model.FindItem(parent, $@"{field}\Rotation")?.Value.Set(transform.Rotation);
            model.FindItem(parent, $@"{field}\Scale")?.Value.SetFloat(transform.Scale);
        }
    }
}
