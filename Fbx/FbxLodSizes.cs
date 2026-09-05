using NIFSharp;
using System.Globalization;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a <c>BSLODTriShape</c>'s level-of-detail triangle counts.
    /// </summary>
    /// <remarks>
    /// A `BSLODTriShape` does not hold three meshes. It holds one triangle list,
    /// partitioned: the first `LOD0 Size` triangles are the nearest level, the next
    /// `LOD1 Size` the one after, and so on, and the engine draws a prefix of the list
    /// according to distance.
    ///
    /// So the counts are the whole of the mechanism, and FBX has nowhere to put them.
    /// Rebuilding the class without them gives a shape whose every level is zero
    /// triangles long — present, correct in every other respect, and invisible.
    ///
    /// Vanilla uses them for plants: all 34 of them, where a distant shrub drops to a
    /// handful of triangles.
    /// </remarks>
    public static class FbxLodSizes
    {
        /// <summary>The fields carried, in the order the shape stores them.</summary>
        private static readonly string[] Fields = ["LOD0 Size", "LOD1 Size", "LOD2 Size"];

        /// <summary>Prefix on the property each count travels in.</summary>
        public const string Prefix = "lod_size_";

        /// <summary>How many levels a shape can hold.</summary>
        public const int Levels = 3;

        /// <summary>The material name marking each level's triangles.</summary>
        public static string LevelMaterial(int level) => $"LOD{level}";

        /// <summary>
        /// Whether a material is a level marker rather than something to shade with.
        /// </summary>
        /// <remarks>
        /// A shape has one material, and the import takes the first one on the node.
        /// The export connects the shape's own material before the markers, so on a
        /// round trip the first is the right one — but a mesh marked up in a DCC tool
        /// has whatever order that tool wrote, and a shape whose shader came out named
        /// <c>LOD0</c> is the failure that follows.
        /// </remarks>
        public static bool IsLevelMaterial(string name)
        {
            for (int level = 0; level < Levels; level++)
            {
                if (name == LevelMaterial(level))
                    return true;
            }

            return false;
        }

        /// <summary>Which level each triangle belongs to, from the counts.</summary>
        /// <remarks>
        /// The counts are consecutive runs over one triangle list: the first
        /// <c>LOD0 Size</c> triangles are level 0, the next <c>LOD1 Size</c> level 1.
        /// A triangle past the end of the last run belongs to the last level that has
        /// any, which is what a shape whose counts do not cover its list means.
        /// </remarks>
        public static List<int> LevelPerTriangle(NifModel model, NifItem shape, int triangles)
        {
            var sizes = new int[Levels];

            for (int i = 0; i < Levels; i++)
                sizes[i] = (int)(model.FindItem(shape, Fields[i])?.Value.ToUInt() ?? 0);

            var levels = new List<int>(triangles);
            int level = 0, remaining = sizes[0];

            for (int i = 0; i < triangles; i++)
            {
                if (remaining == 0)
                {
                    int next = level;

                    while (++next < Levels && sizes[next] == 0)
                    {
                        // An empty level is skipped rather than entered: a shape that
                        // is 0/10/50 starts at level one, and one that runs out of
                        // counts before it runs out of triangles does not fall into a
                        // level it has none of.
                    }

                    if (next < Levels)
                    {
                        level = next;
                        remaining = sizes[level];
                    }
                }

                levels.Add(level);

                if (remaining > 0)
                    remaining--;
            }

            return levels;
        }

        /// <summary>
        /// The counts implied by a per-triangle level marking, and the order the
        /// triangles have to be in for them to mean it.
        /// </summary>
        /// <remarks>
        /// The counts are runs, so the triangles have to be grouped by level and the
        /// groups in order. An artist reassigning a face in a DCC tool changes which
        /// group it is in and nothing else; the reordering happens here.
        /// </remarks>
        public static (List<int> Order, int[] Sizes) GroupByLevel(IReadOnlyList<int> levels)
        {
            var order = new List<int>(levels.Count);
            var sizes = new int[Levels];

            for (int level = 0; level < Levels; level++)
            {
                for (int i = 0; i < levels.Count; i++)
                {
                    if (levels[i] != level)
                        continue;

                    order.Add(i);
                    sizes[level]++;
                }
            }

            // A triangle marked with a level this shape does not have keeps its place
            // at the end rather than disappearing.
            for (int i = 0; i < levels.Count; i++)
            {
                if (levels[i] < 0 || levels[i] >= Levels)
                    order.Add(i);
            }

            return (order, sizes);
        }

        /// <summary>Writes the counts a level marking implies.</summary>
        public static void WriteSizes(NifModel model, NifItem shape, IReadOnlyList<int> sizes)
        {
            for (int i = 0; i < Fields.Length && i < sizes.Count; i++)
                model.FindItem(shape, Fields[i])?.Value.SetCount((uint)sizes[i]);
        }

        /// <summary>Records the counts, if this shape has any.</summary>
        public static void Write(FbxObject geometry, NifModel model, NifItem shape)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (model.FindItem(shape, Fields[i]) is { } size)
                {
                    geometry.Properties.SetUserString(
                        $"{Prefix}{i}", size.Value.ToUInt().ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>Puts them back on a rebuilt shape.</summary>
        /// <remarks>
        /// Silent when nothing travelled: a shape authored in a DCC tool has no LOD
        /// groups to describe, and zero counts are what it should have.
        /// </remarks>
        public static void Read(FbxObject geometry, NifModel model, NifItem shape)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (model.FindItem(shape, Fields[i]) is { } size
                    && uint.TryParse(
                        geometry.Properties.GetString($"{Prefix}{i}"),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out uint value))
                {
                    size.Value.SetCount(value);
                }
            }
        }
    }
}
