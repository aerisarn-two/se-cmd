using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries how a <c>bhkNiTriStripsShape</c>'s geometry was divided.
    /// </summary>
    /// <remarks>
    /// One strips shape can reference several <c>NiTriStripsData</c> blocks, and
    /// `whprison02` does: two shapes with two blocks each. FBX has one mesh per node,
    /// so the export merges them — and with nothing to say where the seams were, the
    /// import rebuilt one block where there were two.
    ///
    /// The division is an authoring artifact rather than anything the engine reads
    /// differently, so this carries it for the same reason the rest of the port carries
    /// such things: a file that nobody edited should come back as the file it was.
    /// A mesh with no seams recorded — one authored in a DCC tool — becomes a single
    /// block, which is what it is.
    /// </remarks>
    public static class FbxStripsParts
    {
        /// <summary>The property counting the data blocks.</summary>
        public const string CountProperty = "strips_parts";

        /// <summary>Prefix on one part's vertex and triangle counts.</summary>
        public const string Prefix = "strips_part_";

        /// <summary>Records where the seams were, when there was more than one.</summary>
        public static void Write(
            FbxObject node, IReadOnlyList<(int Vertices, int Triangles)> parts)
        {
            if (parts.Count < 2)
                return;

            node.Properties.SetUserString(
                CountProperty,
                parts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < parts.Count; i++)
            {
                node.Properties.SetUserString(
                    $"{Prefix}{i}_vertices",
                    parts[i].Vertices.ToString(System.Globalization.CultureInfo.InvariantCulture));

                node.Properties.SetUserString(
                    $"{Prefix}{i}_triangles",
                    parts[i].Triangles.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// The parts a merged mesh should be split back into.
        /// </summary>
        /// <remarks>
        /// Empty when the node records no seams, which means one part. A recorded
        /// division that does not add up to the mesh in hand — an artist having edited
        /// it — is ignored rather than applied to the wrong triangles.
        ///
        /// Counted in triangles only, though vertex counts are recorded too. The mesh
        /// reader welds corners that agree, so the vertex count that comes back is not
        /// the one that went out and never matches; triangles survive one for one. A
        /// nordic coffin's two data blocks were being merged into one for exactly that
        /// reason — the division was recorded, checked against a vertex total that
        /// could not agree, and thrown away.
        /// </remarks>
        public static List<int> Read(FbxObject node, int triangles)
        {
            if (!int.TryParse(
                    node.Properties.GetString(CountProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int count)
                || count < 2)
            {
                return [];
            }

            var parts = new List<int>(count);
            int total = 0;

            for (int i = 0; i < count; i++)
            {
                if (!int.TryParse(
                        node.Properties.GetString($"{Prefix}{i}_triangles"),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int part))
                {
                    return [];
                }

                parts.Add(part);
                total += part;
            }

            return total == triangles ? parts : [];
        }
    }
}
