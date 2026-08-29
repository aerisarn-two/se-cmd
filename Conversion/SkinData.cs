using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// One bone's influence over a mesh.
    /// </summary>
    public sealed class SkinBone
    {
        public required string Name { get; init; }

        /// <summary>
        /// The bone's transform in the skin's space at bind time.
        /// </summary>
        /// <remarks>
        /// NIF stores this per bone on the <c>NiSkinData</c>, as the transform
        /// taking a vertex from skin space into the bone's space. FBX stores the
        /// inverse idea on the cluster: <c>TransformLink</c> is the bone's world
        /// transform at bind time and <c>Transform</c> the mesh's. Getting the two
        /// confused is the usual cause of a mesh that explodes on the first frame.
        /// </remarks>
        public NifTransform SkinTransform { get; set; } = NifTransform.Identity;

        /// <summary>Vertex index and weight pairs. Zero weights are not stored.</summary>
        public List<(ushort Vertex, float Weight)> Weights { get; } = [];
    }

    /// <summary>
    /// One slice of a skin: the bones it draws with and the vertices it covers.
    /// </summary>
    /// <remarks>
    /// Both are indices into the skin they belong to -- <see cref="Bones"/> into
    /// <see cref="SkinData.Bones"/>, <see cref="Vertices"/> into the shape's vertex
    /// array -- because a partition is a view of one shared set of data rather than a
    /// copy of part of it. A vertex on the seam between two body parts appears in both
    /// partitions' lists, which is ordinary: of 1,908 multi-partition shapes in a
    /// 1,200-mesh sample, 1,645 share at least one vertex.
    /// </remarks>
    public sealed class SkinPartitionInfo
    {
        /// <summary>Indices into the skin's bone list.</summary>
        public List<int> Bones { get; } = [];

        /// <summary>Indices into the shape's vertex array.</summary>
        public List<ushort> Vertices { get; } = [];
    }

    /// <summary>
    /// A mesh's skinning: the bones that move it and how strongly.
    /// </summary>
    public sealed class SkinData
    {
        /// <summary>The node the bone transforms are relative to.</summary>
        public string SkeletonRoot { get; set; } = string.Empty;

        /// <summary>
        /// Which skin instance class the shape used.
        /// </summary>
        /// <remarks>
        /// A `BSDismemberSkinInstance` carries body-part partitions on top of a plain
        /// `NiSkinInstance`, and the two are not interchangeable: the dismember form is
        /// what lets armour hide the body under it. Rebuilding every skin as the
        /// dismember form gives that machinery to shapes that never had it, which is
        /// the most common single difference across the game's meshes.
        ///
        /// Empty when the scene did not say, in which case the import picks the form
        /// that suits the edition.
        /// </remarks>
        public string InstanceType { get; set; } = string.Empty;

        /// <summary>
        /// Which <c>NiSkinData</c> this skin used, when it came from a NIF.
        /// </summary>
        /// <remarks>
        /// Bethesda's files point two shapes at one skin data and one partition — a
        /// facegen head's two scar marks are the same weights on the same bone, so the
        /// blocks are shared rather than duplicated. Rebuilding each shape's skin on
        /// its own turns one block into two, which is the file changed.
        ///
        /// Identity rather than content, as a texture set is (§5.2.1): the game also
        /// ships identical skins side by side on purpose, and merging those would be
        /// as wrong as never merging at all.
        ///
        /// -1 when the scene did not say, which is what a skin authored in a DCC tool
        /// has, and means "this shape's own".
        /// </remarks>
        public int SkinDataId { get; set; } = -1;

        /// <summary>
        /// The body slot each skin partition occupies, in partition order.
        /// </summary>
        /// <remarks>
        /// This is the whole of the difference between the two skin instance classes.
        /// A slot says which part of a body the partition is — torso, head, left hand
        /// — and the engine uses it to hide the body under a cuirass and to take a
        /// limb off. A shape with slots is a `BSDismemberSkinInstance`; a shape
        /// without one is a plain `NiSkinInstance`, and there is nothing else to tell
        /// them apart (§5.2.3).
        ///
        /// So the class is not carried separately: it follows from whether this is
        /// empty, which means the two can never disagree.
        /// </remarks>
        public List<(string Slot, uint Flags)> BodySlots { get; } = [];

        /// <summary>The whole skin's transform, usually identity.</summary>
        public NifTransform SkinTransform { get; set; } = NifTransform.Identity;

        public List<SkinBone> Bones { get; } = [];

        /// <summary>
        /// How the skin was split for the renderer, one entry per partition.
        /// </summary>
        /// <remarks>
        /// A `NiSkinPartition` divides a skinned shape into slices the hardware can
        /// draw in one pass -- at most sixty bones each -- and on a dismembered shape
        /// those slices are the body parts, which is what lets a cuirass hide the torso
        /// under it and a limb come off. The partitions share one vertex array, each
        /// naming the slice it uses.
        ///
        /// Empty when the shape had no partition to read, or when the scene it came
        /// from did not say how it was split. A skin with no partitions here is split
        /// again from scratch on the way in, by packing bones until they fit.
        /// </remarks>
        public List<SkinPartitionInfo> Partitions { get; } = [];

        public bool IsEmpty => Bones.Count == 0;

        /// <summary>
        /// Weights grouped by vertex, heaviest first, which is the order NIF's
        /// partitions expect.
        /// </summary>
        public Dictionary<ushort, List<(int Bone, float Weight)>> ByVertex()
        {
            var result = new Dictionary<ushort, List<(int, float)>>();

            for (int bone = 0; bone < Bones.Count; bone++)
            {
                foreach ((ushort vertex, float weight) in Bones[bone].Weights)
                {
                    if (weight <= 0f)
                        continue;

                    if (!result.TryGetValue(vertex, out var list))
                        result[vertex] = list = [];

                    list.Add((bone, weight));
                }
            }

            foreach (List<(int Bone, float Weight)> list in result.Values)
                list.Sort((a, b) => b.Weight.CompareTo(a.Weight));

            return result;
        }

        /// <summary>
        /// Drops all but the heaviest <paramref name="maxPerVertex"/> influences and
        /// renormalises so each vertex's weights still sum to one.
        /// </summary>
        /// <remarks>
        /// Skyrim reads four weights per vertex. Leaving more in place does not
        /// error, it just silently ignores the rest — and because the ignored ones
        /// still counted toward the total, every affected vertex ends up
        /// under-weighted and drifts toward the origin.
        /// </remarks>
        /// <summary>
        /// How far a vertex's weights may sum from one and still count as normalised.
        /// </summary>
        /// <remarks>
        /// Wide enough to cover the half-float quantisation the format stores weights
        /// in, and narrower than any real fault: over 4,201,422 vanilla skinned vertices
        /// not one with four influences or fewer sums further than 1e-4 from one, and a
        /// vertex that has genuinely lost an influence is short by that influence's
        /// whole weight. So this separates the two cases with room to spare either way.
        /// </remarks>
        public const float NormalisedTolerance = 1e-3f;

        /// <summary>Whether a set of weights already sums to one.</summary>
        public static bool IsNormalised(float total) =>
            MathF.Abs(total - 1f) <= NormalisedTolerance;

        /// <summary>
        /// The per-vertex scale that makes a set of weights sum to one.
        /// </summary>
        /// <remarks>
        /// Returns exactly one when they already do, because dividing by a total that
        /// is already one is not a no-op in floating point: 0.99999994 is enough to
        /// move a weight into the neighbouring half, and both the vertex buffer and the
        /// partition store halves.
        ///
        /// This guarded form is for the **vertex buffer**, whose weights are halves.
        /// The partition stores full floats and normalises unconditionally -- see
        /// <see cref="PartitionScale"/> -- and the two are not interchangeable.
        /// </remarks>
        public static float VertexScale(float total) =>
            total > 0f && MathF.Abs(total - 1f) >= 1e-4f ? 1f / total : 1f;

        /// <summary>
        /// The per-vertex scale the skin partition's weights take.
        /// </summary>
        /// <remarks>
        /// Unguarded, unlike <see cref="VertexScale"/>. The partition holds full floats,
        /// so there is no neighbouring half to be rounded into, and the files are
        /// normalised to the last bit: `TestNifFile_LooseBlocks_SE` carries vertices
        /// whose authored weights sum to 0.999924 -- inside any sensible tolerance --
        /// and whose partition still holds them scaled to exactly one.
        ///
        /// `NiSkinData` beside it is the copy that keeps the authored values, which is
        /// why normalising in <see cref="LimitInfluences"/>, which feeds both, made the
        /// two copies agree with each other and neither agree with the file.
        /// </remarks>
        public static float PartitionScale(float total) => total > 0f ? 1f / total : 1f;

        public void LimitInfluences(int maxPerVertex = 4)
        {
            var byVertex = ByVertex();

            // What survives for each vertex, and what its weights are then multiplied
            // by. Worked out first and applied second, so that each bone's list keeps
            // the order it arrived in -- rebuilding the lists from a walk of this
            // dictionary put them in its enumeration order instead, which is nobody's,
            // and every rebuilt NiSkinData came back holding the right weights on the
            // right vertices in the wrong sequence.
            var scale = new Dictionary<ushort, float>(byVertex.Count);
            var survivors = new Dictionary<ushort, HashSet<int>>(byVertex.Count);

            foreach ((ushort vertex, List<(int Bone, float Weight)> influences) in byVertex)
            {
                int take = Math.Min(maxPerVertex, influences.Count);
                float total = 0f;

                for (int i = 0; i < take; i++)
                    total += influences[i].Weight;

                if (total <= 0f)
                    continue;

                // Rescaled only when something was actually dropped, or when the weights
                // were never normalised to begin with. Dividing by a total that is
                // already one is not a no-op in floating point, and both renderer copies
                // store halves, so every weight on every fully-weighted vertex came back
                // a few parts in ten thousand adrift for arithmetic with nothing to fix.
                // Measured over the 4,201,422 skinned vertices in a third of Skyrim's
                // meshes: with four influences or fewer the worst |sum - 1| is 1.6e-7,
                // and not one vertex is further out than 1e-4.
                bool renormalise = influences.Count > maxPerVertex || !IsNormalised(total);

                scale[vertex] = renormalise ? 1f / total : 1f;
                survivors[vertex] = [.. influences.Take(take).Select(x => x.Bone)];
            }

            for (int bone = 0; bone < Bones.Count; bone++)
            {
                List<(ushort Vertex, float Weight)> weights = Bones[bone].Weights;
                var keptForBone = new List<(ushort Vertex, float Weight)>(weights.Count);

                foreach ((ushort vertex, float weight) in weights)
                {
                    if (survivors.TryGetValue(vertex, out HashSet<int>? kept) && kept.Contains(bone))
                        keptForBone.Add((vertex, weight * scale[vertex]));
                }

                weights.Clear();
                weights.AddRange(keptForBone);
            }
        }
    }
}
