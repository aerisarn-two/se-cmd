using MeshIO.Formats.Fbx;
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
        }

        /// <summary>
        /// The de-duplication key: every attribute a corner carries.
        /// </summary>
        /// <remarks>
        /// Compared exactly rather than with a tolerance, matching FBXWrangler.
        /// Values that came from the same source corner are bit-identical, so exact
        /// comparison merges what should merge; a tolerance would risk welding a
        /// genuine seam shut.
        /// </remarks>
        private readonly record struct VertexKey(
            double PX, double PY, double PZ,
            double NX, double NY, double NZ,
            double TX, double TY, double TZ,
            double BX, double BY, double BZ,
            double U, double V,
            double R, double G, double B, double A,
            string Skin);

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
            var colors = LayerElement.Find(geometry, "LayerElementColor", "Colors");

            var mesh = new MeshGeometry();
            var seen = new Dictionary<VertexKey, ushort>();
            bool overflowed = false;

            int corner = 0;
            int polygon = 0;
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

                    if (a == b || b == c || a == c)
                        continue;

                    mesh.Triangles.Add(new NifTriangle(a, b, c));
                    mesh.TrianglePolygons.Add(polygon);
                }

                polygonCorners.Clear();
                polygon++;
            }

            // Control points no triangle reaches are still vertices of the mesh.
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
                    position.X, position.Y, position.Z,
                    normal.X, normal.Y, normal.Z,
                    tangent.X, tangent.Y, tangent.Z,
                    bitangent.X, bitangent.Y, bitangent.Z,
                    uv.X, uv.Y,
                    color.R, color.G, color.B, color.A,
                    skin);

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

            public static LayerElement Find(FbxObject geometry, string elementName, string arrayName)
            {
                FbxNode? element = geometry.Node.Nodes.FirstOrDefault(n => n.Name == elementName);

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
