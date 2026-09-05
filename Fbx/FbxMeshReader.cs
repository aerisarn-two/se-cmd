using LeanMeshIO.Formats.Fbx;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>How a layer element's values line up with the mesh.</summary>
    public enum FbxMappingMode
    {
        None,
        /// <summary>One value per control point.</summary>
        ByControlPoint,
        /// <summary>One value per polygon.</summary>
        ByPolygon,
        /// <summary>One value per polygon corner.</summary>
        ByPolygonVertex,
        /// <summary>A single value for the whole mesh.</summary>
        AllSame
    }

    /// <summary>Whether a layer element's values are read straight or through indices.</summary>
    public enum FbxReferenceMode
    {
        Direct,
        IndexToDirect
    }

    /// <summary>
    /// Reads an FBX <c>Geometry</c> object into the neutral mesh form.
    /// </summary>
    /// <remarks>
    /// Two things make this more than a copy, both from spec §5.3:
    ///
    /// FBX lets every attribute choose its own mapping and reference mode, so a
    /// normal may be stored per control point, per polygon or per corner, and either
    /// directly or through an index array. All of it collapses to per-vertex here.
    ///
    /// That collapse only works if vertices are split wherever attributes disagree,
    /// so corners are de-duplicated on the exact tuple of every attribute they
    /// carry. This is what produces the extra vertices NIF needs at UV and normal
    /// seams, and skipping it silently merges them.
    /// </remarks>
    public static class FbxMeshReader
    {
        /// <summary>Options matching FBXWrangler's import defaults.</summary>
        public sealed class Options
        {
            /// <summary>Mirror U. Off by default, as in FBXWrangler.</summary>
            public bool InvertU { get; set; }

            /// <summary>Mirror V. On by default: NIF's V axis points the other way.</summary>
            public bool InvertV { get; set; } = true;

            /// <summary>
            /// What each control point's skinning looks like, when the mesh has any.
            /// </summary>
            /// <remarks>
            /// Keyed by control point, since that is how FBX indexes a skin cluster.
            /// Only the identity of the string matters: two control points weighted the
            /// same way must produce the same text, and two weighted differently must
            /// not. Null for an unskinned mesh, where there is nothing to tell apart.
            /// </remarks>
            public IReadOnlyDictionary<int, string>? Influences { get; set; }

            /// <summary>
            /// Which skin partitions draw each control point, when the mesh is split.
            /// </summary>
            /// <remarks>
            /// A set per point rather than a number, because a vertex on the seam
            /// between two body parts is drawn by both and vanilla is full of them.
            /// Only the identity of the string matters, as with
            /// <see cref="Influences"/>. Null when the scene carried a single
            /// undivided skin, where there is nothing to tell apart.
            /// </remarks>
            public IReadOnlyDictionary<int, string>? Partitions { get; set; }
        }

        /// <summary>
        /// The de-duplication key: which control point a corner belongs to, and every
        /// attribute it carries.
        /// </summary>
        /// <remarks>
        /// **The control point comes first, and is what stops the merge going too far.**
        /// De-duplication exists for one direction only: FBX keeps a normal, a texture
        /// coordinate and a colour per polygon *corner*, so the several corners meeting
        /// at one control point have to become one NIF vertex where they agree and
        /// several where they do not. That is a question about corners of the same
        /// point, and it is answered by the attributes below.
        ///
        /// Merging across *different* control points answers no question. It is loss.
        /// The exporter writes one control point per NIF vertex with every layer element
        /// `ByControlPoint`, so a NIF that made the trip has its vertices and the FBX's
        /// points in step, one for one -- and two vertices the file kept apart were kept
        /// apart for a reason the file did not have to explain. Vanilla is full of rows
        /// identical in all twenty-three factors: over the 81,188 shapes the game ships,
        /// 458,920 vertices of 36,106,394 on 11,947 shapes. Every one of them used to be
        /// welded away, taking the triangle numbering with it.
        ///
        /// It costs nothing in the other direction either. Two coincident control points
        /// in a mesh authored in a DCC are two vertices in that file, and keeping them is
        /// what the author wrote. ck-cmd merges them -- and separately splits vertices a
        /// NIF held together, so it is not the standard to match here.
        ///
        /// Compared exactly rather than with a tolerance, matching FBXWrangler.
        /// Values that came from the same source corner are bit-identical, so exact
        /// comparison merges what should merge; a tolerance would risk welding a
        /// genuine seam shut.
        ///
        /// Twenty-three factors: FBXWrangler's eighteen, four bone-and-weight pairs
        /// carried in `Skin`, `Unused W`, `Eye Data`, and the set of skin partitions
        /// the vertex belongs to. The last is a set rather than a partition number
        /// because vanilla shares vertices between partitions, so a single number would
        /// have to choose one for a seam vertex and would split what the file holds
        /// once -- see `FbxToNif.PartitionSignatures`.
        ///
        /// Nineteen through twenty-three come off the skin rather than out of a layer
        /// element, because that is where FBX keeps them: a cluster per bone for the
        /// influences, and a deformer per partition for the split.
        ///
        /// FBXWrangler keys on eighteen numbers (`FBXWrangler.cpp:3254`, filled at
        /// `:3329`): position, normal, tangent, bitangent, UV and colour. This keys on
        /// those plus three -- the skin, `Unused W` and `Eye Data` -- and only one of
        /// the three earns its place. Measured one at a time over all 81,188 shapes the
        /// game ships, against the 458,920 vertices of 36,106,394 that the eighteen
        /// merge, on 11,947 shapes:
        ///
        /// - `Unused W` saves 5,632 vertices across 545 shapes. Only 2,862 shapes carry
        ///   the field at all, so it is distinguishing on nearly a fifth of the shapes
        ///   that have it -- which stands to reason, since it is a position's fourth
        ///   word and not padding.
        /// - The skin saves 981 vertices, but on 9 shapes out of the 26,940 carrying
        ///   bone weights. Two vertices that already agree on position, normal,
        ///   tangent, bitangent, UV and colour almost never disagree on their bones;
        ///   where they do, they do it in bulk.
        /// - `Eye Data` saves nothing at all, on any of the 3,233 shapes carrying it.
        ///
        /// Compared on the exact values. An earlier pass compared them as `NifValue`
        /// prints them, which is `G6` -- six significant digits -- and called two
        /// vertices identical when they agreed to six digits and differed in the
        /// seventh. That inflated the duplicate count by two fifths, and the numbers
        /// above replace it.
        ///
        /// The last two stay because a key is a claim about what makes a vertex itself,
        /// and a field that could differ belongs in it whether or not the shipped game
        /// happens to exercise it. But the number that matters here is `Unused W`: it
        /// is the one whose absence from FBXWrangler's eighteen actually loses data.
        ///
        /// Nothing else in a Skyrim SE file indexes a vertex by position in the array.
        /// The same sample ships two geometry classes and no others -- 15,860
        /// `BSTriShape` and 3,515 `BSDynamicTriShape` -- with no `BSSubIndexTriShape`,
        /// no segments and no morph controller anywhere in it. A dynamic shape's second
        /// `Vertices` buffer is parallel to the vertex list and is rebuilt from it, so
        /// it follows the merge rather than being broken by it. That is why merging
        /// genuinely identical rows is safe here, and why the three extra factors are
        /// the whole of what makes it safe.
        /// </remarks>
        private readonly record struct VertexKey(
            int ControlPoint,
            double PX, double PY, double PZ,
            double NX, double NY, double NZ,
            double TX, double TY, double TZ,
            double BX, double BY, double BZ,
            double U, double V,
            double R, double G, double B, double A,
            string Skin,
            double UnusedW, double EyeData,
            string Partition);

        /// <summary>Reads a geometry object, or null when it holds no mesh.</summary>
        /// <summary>
        /// The material index of each polygon, or null when the mesh has one material.
        /// </summary>
        /// <remarks>
        /// The only per-polygon channel every DCC tool exposes and lets an artist
        /// reassign, which is what a `BSLODTriShape` needs: its triangles are
        /// partitioned by level of detail and FBX has no notion of a LOD group.
        /// </remarks>
        public static List<int>? ReadPolygonMaterials(FbxObject geometry)
        {
            FbxNode? element = geometry.Node.Nodes.FirstOrDefault(n => n.Name == "LayerElementMaterial");

            if (element is null)
                return null;

            string mapping = element.Nodes.FirstOrDefault(n => n.Name == "MappingInformationType")
                ?.Properties.FirstOrDefault() as string ?? string.Empty;

            if (mapping != "ByPolygon")
                return null;

            return element.Nodes.FirstOrDefault(n => n.Name == "Materials")?.Properties.FirstOrDefault()
                   is int[] indices
                ? [.. indices]
                : null;
        }

        public static MeshGeometry? Read(FbxObject geometry, Options? options = null)
        {
            options ??= new Options();

            if (geometry.Child("Vertices")?.Properties.FirstOrDefault() is not double[] rawVertices)
                return null;

            if (geometry.Child("PolygonVertexIndex")?.Properties.FirstOrDefault() is not int[] rawIndices)
                return null;

            var controlPoints = new NifVector3[rawVertices.Length / 3];

            for (int i = 0; i < controlPoints.Length; i++)
            {
                controlPoints[i] = new NifVector3(
                    (float)rawVertices[i * 3],
                    (float)rawVertices[i * 3 + 1],
                    (float)rawVertices[i * 3 + 2]);
            }

            var normals = LayerElement.Find(geometry, "LayerElementNormal", "Normals");
            var tangents = LayerElement.Find(geometry, "LayerElementTangent", "Tangents");
            var bitangents = LayerElement.Find(geometry, "LayerElementBinormal", "Binormals");
            var uvs = LayerElement.Find(geometry, "LayerElementUV", "UV");

            // The second UV set, if the exporter wrote one: the vertex's fourth word in
            // U and the eye marker in V. Found by its name, since Find takes the first
            // element of a kind and that is the real UVs.
            var extra = LayerElement.FindNamed(
                geometry, "LayerElementUV", "UV", FbxMeshWriter.VertexExtraElementName);

            string extraChannels =
                geometry.Properties.GetString(FbxMeshWriter.VertexExtraChannelsProperty);

            var colors = LayerElement.Find(geometry, "LayerElementColor", "Colors");

            // Which partition draws each face, when the scene says the skin is split.
            var polygonGroups = geometry.Node.Nodes
                .FirstOrDefault(n => n.Name == "LayerElementPolygonGroup")
                ?.Nodes.FirstOrDefault(n => n.Name == "PolygonGroup")
                ?.Properties.FirstOrDefault() as int[];

            var mesh = new MeshGeometry();
            var seen = new Dictionary<VertexKey, ushort>();
            bool overflowed = false;

            // Vertices in control-point order, before any triangle is looked at.
            //
            // The reader used to number them in the order the triangles first reached
            // them, so a mesh came back with the same vertices in a different order --
            // and every per-vertex field with it, shifted along by a place or two. A NIF
            // written by this port therefore never matched the one it came from, however
            // faithful each individual value was.
            //
            // Only when every attribute depends on the control point alone, which is
            // what this port writes and what the check below asks. A DCC mesh can give
            // one control point several normals or texture coordinates, and there the
            // splitting has to happen while the corners are walked -- there is no single
            // vertex to emit up front.
            int corner = 0;
            int polygon = 0;

            bool perControlPoint = normals.PerControlPoint && tangents.PerControlPoint
                && bitangents.PerControlPoint && uvs.PerControlPoint && colors.PerControlPoint;

            if (perControlPoint)
            {
                for (int point = 0; point < controlPoints.Length; point++)
                    Emit((-1, point));
            }

            var polygonCorners = new List<(int Corner, int ControlPoint)>();

            foreach (int raw in rawIndices)
            {
                // A negative index is the bitwise complement of the real one and
                // marks the last corner of a polygon.
                bool last = raw < 0;
                int controlPoint = last ? ~raw : raw;

                polygonCorners.Add((corner, controlPoint));
                corner++;

                if (!last)
                    continue;

                // Fan-triangulate. FBXWrangler relies on the FBX SDK having
                // triangulated already and skips anything that is not a triangle;
                // we have no SDK, so an n-gon is fanned rather than dropped.
                for (int i = 1; i + 1 < polygonCorners.Count; i++)
                {
                    ushort a = Emit(polygonCorners[0]);
                    ushort b = Emit(polygonCorners[i]);
                    ushort c = Emit(polygonCorners[i + 1]);

                    // A collapsed triangle is dropped only when it came out of fanning
                    // something larger, where two corners meeting on one vertex is an
                    // artifact of the fan rather than a face anybody drew.
                    //
                    // A polygon that arrived as a triangle is kept as it is, degenerate
                    // or not. A NIF may hold such a triangle deliberately -- it draws
                    // nothing, and Bethesda leaves them in -- and dropping them lost two
                    // of `arnhall3way01`'s 100, with `Num Triangles` and `Data Size`
                    // following. Thirteen meshes differed for that reason, twelve of them
                    // Creation Club Ayleid ruins.
                    if ((a == b || b == c || a == c) && polygonCorners.Count > 3)
                        continue;

                    mesh.Triangles.Add(new NifTriangle(a, b, c));
                    mesh.TrianglePolygons.Add(polygon);

                    // Every triangle a fanned polygon becomes is drawn by the partition
                    // the polygon was in.
                    if (polygonGroups is not null && polygon < polygonGroups.Length)
                        mesh.TrianglePartitions.Add(polygonGroups[polygon]);
                }

                polygonCorners.Clear();
                polygon++;
            }

            // Control points no triangle reaches are still vertices of the mesh. Already
            // done above when the attributes allowed it; this is the case where they did
            // not, and the corners had to be walked first.
            //
            // The loop above only ever arrives at a control point through a polygon
            // corner, so a vertex that nothing indexes would be dropped in silence.
            // Vertices are the mesh's own data — a position, a normal, a texture
            // coordinate, its own bone weights — and whether some triangle happens to
            // name one does not make it ours to discard.
            //
            // This was found while chasing a shape that appeared to have 219 such
            // vertices, and that turned out to be a different bug entirely: the export
            // was throwing away the triangles that named them (see
            // `NifToFbx.ReadTriShape`). So this is a guard rather than a fix for that,
            // and on the game's meshes it now finds nothing to do. It stays because
            // the alternative is losing data silently the day something does.
            //
            // Emitted after the triangles rather than in place, because the ones the
            // triangles use have already fixed the numbering and moving them would
            // renumber every triangle for nothing.
            for (int point = 0; point < controlPoints.Length; point++)
            {
                if (!mesh.VertexOfControlPoint.ContainsKey(point))
                    Emit((-1, point));
            }

            // More corners than a NIF triangle can name. Everything built above indexes
            // the wrong vertices from the wrap onward, so none of it is worth keeping.
            return overflowed ? null : mesh;

            ushort Emit((int Corner, int ControlPoint) at)
            {
                NifVector3 position = at.ControlPoint < controlPoints.Length
                    ? controlPoints[at.ControlPoint]
                    : new NifVector3();

                NifVector3 normal = normals.ReadVector3(at.ControlPoint, polygon, at.Corner);
                NifVector3 tangent = tangents.ReadVector3(at.ControlPoint, polygon, at.Corner);
                NifVector3 bitangent = bitangents.ReadVector3(at.ControlPoint, polygon, at.Corner);
                NifVector2 uv = uvs.ReadVector2(at.ControlPoint, polygon, at.Corner);
                NifColor4 color = colors.ReadColor4(at.ControlPoint, polygon, at.Corner);
                NifVector2 spare = extra.ReadVector2(at.ControlPoint, polygon, at.Corner);
                // Which partitions draw this point. Not a layer element: FBX says this
                // with the skin deformers themselves, one per partition, so membership
                // is read off them rather than carried alongside.
                string member = options.Partitions is not null
                                && options.Partitions.TryGetValue(at.ControlPoint, out string? m)
                    ? m
                    : string.Empty;

                // What a vertex *is* includes which bones move it. That is not in any
                // layer element — FBX keeps it on the skin deformer, indexed by control
                // point — so without it two vertices in the same place, facing the same
                // way, weighted to different bones are indistinguishable here and merge.
                // A prisoner's rags lost 219 of 1,303 vertices that way.
                string skin = options.Influences is not null
                              && options.Influences.TryGetValue(at.ControlPoint, out string? s)
                    ? s
                    : string.Empty;

                var key = new VertexKey(
                    at.ControlPoint,
                    position.X, position.Y, position.Z,
                    normal.X, normal.Y, normal.Z,
                    tangent.X, tangent.Y, tangent.Z,
                    bitangent.X, bitangent.Y, bitangent.Z,
                    uv.X, uv.Y,
                    color.R, color.G, color.B, color.A,
                    skin,
                    spare.X, spare.Y,
                    member);

                if (seen.TryGetValue(key, out ushort existing))
                {
                    // Two control points that merged both answer to the vertex they
                    // became, so a cluster weighting either one still lands right.
                    mesh.VertexOfControlPoint[at.ControlPoint] = existing;
                    return existing;
                }

                // A NIF triangle indexes its corners with a `ushort`. Past 65,535 this
                // cast wraps, `seen` starts handing out indices that already belong to
                // other vertices, and every later triangle silently names the wrong
                // corner — geometry that imports cleanly and is wrong. A mesh that
                // large cannot be one NIF shape at all, so the honest answer is to stop
                // rather than to produce a scrambled one.
                if (mesh.Vertices.Count > ushort.MaxValue)
                {
                    overflowed = true;
                    return 0;
                }

                var index = (ushort)mesh.Vertices.Count;
                seen[key] = index;
                mesh.VertexOfControlPoint[at.ControlPoint] = index;

                mesh.Vertices.Add(position);

                if (extra.Exists)
                {
                    // Only the channels the shape said it has. Both when it said
                    // nothing, which is what an older export looks like.
                    //
                    // Back to a uint exactly: it went out as one and a double holds it
                    // whole. Rounding first, because a double that came through a file
                    // may be a hair off the integer it stands for.
                    if (extraChannels.Length == 0 || extraChannels.Contains('w'))
                        mesh.UnusedW.Add((uint)Math.Round(spare.X));

                    if (extraChannels.Length == 0 || extraChannels.Contains('e'))
                        mesh.EyeData.Add(spare.Y);
                }


                if (normals.Exists)
                    mesh.Normals.Add(normal);

                if (tangents.Exists)
                    mesh.Tangents.Add(tangent);

                if (bitangents.Exists)
                    mesh.Bitangents.Add(bitangent);

                if (uvs.Exists)
                {
                    mesh.Uvs.Add(new NifVector2(
                        options.InvertU ? 1f - uv.X : uv.X,
                        options.InvertV ? 1f - uv.Y : uv.Y));
                }

                if (colors.Exists)
                    mesh.Colors.Add(color);

                return index;
            }
        }

        /// <summary>
        /// One layer element, resolved down to "give me the value for this corner".
        /// </summary>
        private readonly struct LayerElement
        {
            private readonly double[]? _values;
            private readonly int[]? _indices;
            private readonly FbxMappingMode _mapping;
            private readonly FbxReferenceMode _reference;

            private LayerElement(double[]? values, int[]? indices, FbxMappingMode mapping, FbxReferenceMode reference)
            {
                _values = values;
                _indices = indices;
                _mapping = mapping;
                _reference = reference;
            }

            public bool Exists => _values is { Length: > 0 } && _mapping != FbxMappingMode.None;

            /// <summary>
            /// Whether this element's value depends only on which control point it is.
            /// </summary>
            /// <remarks>
            /// True for an element that is absent, one value for the whole mesh, or one
            /// value per control point. False when a corner can differ from its
            /// neighbour at the same control point, which is when a control point has to
            /// become more than one vertex.
            /// </remarks>
            public bool PerControlPoint =>
                _mapping is FbxMappingMode.None or FbxMappingMode.AllSame or FbxMappingMode.ByControlPoint;

            /// <summary>
            /// The first element of a kind, skipping the one the exporter reserves.
            /// </summary>
            /// <remarks>
            /// The vertex-extra channel is a second `LayerElementUV`, because FBX has no
            /// per-vertex scalar of its own. Taking simply the first element of a kind
            /// picked it up as the real texture coordinates on any mesh that has none,
            /// and 31,118 UVs came back holding a packed vertex word.
            /// </remarks>
            public static LayerElement Find(FbxObject geometry, string elementName, string arrayName) =>
                Read(
                    geometry.Node.Nodes.FirstOrDefault(
                        n => n.Name == elementName && !IsReserved(n)),
                    arrayName);

            private static bool IsReserved(FbxNode element) =>
                element.Nodes.FirstOrDefault(c => c.Name == "Name")?.Properties.FirstOrDefault()
                    as string == FbxMeshWriter.VertexExtraElementName;

            /// <summary>The element of a kind carrying a particular layer name.</summary>
            public static LayerElement FindNamed(
                FbxObject geometry, string elementName, string arrayName, string layerName) =>
                Read(
                    geometry.Node.Nodes.FirstOrDefault(
                        n => n.Name == elementName
                             && n.Nodes.FirstOrDefault(c => c.Name == "Name")?.Properties.FirstOrDefault()
                                as string == layerName),
                    arrayName);

            private static LayerElement Read(FbxNode? element, string arrayName)
            {

                if (element is null)
                    return new LayerElement(null, null, FbxMappingMode.None, FbxReferenceMode.Direct);

                double[]? values = element.Nodes
                    .FirstOrDefault(n => n.Name == arrayName)?.Properties.FirstOrDefault() as double[];

                // The index array is named after the element, e.g. UV -> UVIndex.
                int[]? indices = element.Nodes
                    .FirstOrDefault(n => n.Name.EndsWith("Index", StringComparison.Ordinal))
                    ?.Properties.FirstOrDefault() as int[];

                string mapping = element.Nodes
                    .FirstOrDefault(n => n.Name == "MappingInformationType")?.Properties.FirstOrDefault() as string
                    ?? string.Empty;

                string reference = element.Nodes
                    .FirstOrDefault(n => n.Name == "ReferenceInformationType")?.Properties.FirstOrDefault() as string
                    ?? "Direct";

                return new LayerElement(
                    values,
                    indices,
                    mapping switch
                    {
                        "ByControlPoint" or "ByVertice" or "ByVertex" => FbxMappingMode.ByControlPoint,
                        "ByPolygon" => FbxMappingMode.ByPolygon,
                        "ByPolygonVertex" => FbxMappingMode.ByPolygonVertex,
                        "AllSame" => FbxMappingMode.AllSame,
                        _ => FbxMappingMode.None
                    },
                    reference == "Direct" ? FbxReferenceMode.Direct : FbxReferenceMode.IndexToDirect);
            }

            /// <summary>
            /// Resolves the slot in the value array for a corner, or -1 when the
            /// element does not cover it.
            /// </summary>
            private int Resolve(int controlPoint, int polygon, int corner)
            {
                if (!Exists)
                    return -1;

                int index = _mapping switch
                {
                    FbxMappingMode.ByControlPoint => controlPoint,
                    FbxMappingMode.ByPolygon => polygon,
                    FbxMappingMode.ByPolygonVertex => corner,
                    FbxMappingMode.AllSame => 0,
                    _ => -1
                };

                if (index < 0)
                    return -1;

                if (_reference == FbxReferenceMode.IndexToDirect)
                {
                    if (_indices is null || index >= _indices.Length)
                        return -1;

                    index = _indices[index];
                }

                return index;
            }

            public NifVector3 ReadVector3(int controlPoint, int polygon, int corner)
            {
                int at = Resolve(controlPoint, polygon, corner);

                if (at < 0 || _values is null || (at + 1) * 3 > _values.Length)
                    return new NifVector3();

                return new NifVector3(
                    (float)_values[at * 3],
                    (float)_values[at * 3 + 1],
                    (float)_values[at * 3 + 2]);
            }

            public NifVector2 ReadVector2(int controlPoint, int polygon, int corner)
            {
                int at = Resolve(controlPoint, polygon, corner);

                if (at < 0 || _values is null || (at + 1) * 2 > _values.Length)
                    return new NifVector2();

                return new NifVector2((float)_values[at * 2], (float)_values[at * 2 + 1]);
            }

            public NifColor4 ReadColor4(int controlPoint, int polygon, int corner)
            {
                int at = Resolve(controlPoint, polygon, corner);

                if (at < 0 || _values is null || (at + 1) * 4 > _values.Length)
                    return new NifColor4(1f, 1f, 1f, 1f);

                return new NifColor4(
                    (float)_values[at * 4],
                    (float)_values[at * 4 + 1],
                    (float)_values[at * 4 + 2],
                    (float)_values[at * 4 + 3]);
            }
        }
    }
}
