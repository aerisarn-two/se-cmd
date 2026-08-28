using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// A triangle mesh with per-vertex attributes, in the form both sides of the
    /// conversion agree on.
    /// </summary>
    /// <remarks>
    /// Attributes are per vertex rather than per polygon-corner, which is how NIF
    /// stores them. FBX allows either, so the writer emits <c>ByControlPoint</c> and
    /// the reader collapses whatever mapping it finds down to this shape by
    /// de-duplicating corners — the same thing FBXWrangler does on import.
    ///
    /// Coordinates are in NIF space throughout. No axis conversion happens anywhere
    /// in the pipeline: the FBX declares Max axes (Z-up, right-handed), so the
    /// numbers are the same on both sides.
    /// </remarks>
    public sealed class MeshGeometry
    {
        public List<NifVector3> Vertices { get; } = [];

        public List<NifVector3> Normals { get; } = [];

        public List<NifVector3> Tangents { get; } = [];

        public List<NifVector3> Bitangents { get; } = [];

        /// <summary>UV set 0, in NIF convention. V is flipped when crossing to FBX.</summary>
        public List<NifVector2> Uvs { get; } = [];

        public List<NifColor4> Colors { get; } = [];

        public List<NifTriangle> Triangles { get; } = [];

        /// <summary>
        /// The polygon each triangle came from, when the mesh was read from FBX.
        /// </summary>
        /// <remarks>
        /// A NIF holds triangles and an FBX holds polygons, and an n-gon fans into
        /// several triangles, so the two lists are not the same length. Anything
        /// reading a per-polygon channel — a `BSLODTriShape`'s levels are the one that
        /// matters — has to come back through this to reach a triangle.
        ///
        /// Empty on a mesh built from a NIF, where there is nothing to come back from.
        /// </remarks>
        public List<int> TrianglePolygons { get; } = [];

        /// <summary>
        /// The material each triangle is made of, when the mesh came from a shape that
        /// records one.
        /// </summary>
        /// <remarks>
        /// An index into the shape's own material table, not a Havok material. Only a
        /// chunked collision mesh fills this: its chunks each name a material, and
        /// losing that on the way out is what made every rebuilt mesh collision a
        /// single substance.
        /// </remarks>
        public List<int> TriangleMaterials { get; } = [];

        /// <summary>
        /// The fourth word of an SSE vertex, where the shape has no tangents.
        /// </summary>
        /// <remarks>
        /// nif.xml gives `Bitangent X` and `Unused W` the same slot, chosen by the
        /// Tangents flag, so this is only live on a shape without them -- 1,019 of the
        /// 13,534 vanilla shapes sampled. The name says unused and the data disagrees:
        /// 15,708 of 21,215 slots are non-zero across 7,586 distinct values, the
        /// commonest being 0x3F800000, which is 1.0 -- the homogeneous w of a position.
        /// Typed uint because it is not always a meaningful float.
        /// </remarks>
        public List<uint> UnusedW { get; } = [];

        /// <summary>
        /// The per-vertex eye marker, where the shape carries one.
        /// </summary>
        /// <remarks>
        /// Live only under the Eye_Data flag, which 536 of the sampled shapes set. Every
        /// non-zero value in vanilla is exactly 1.0 -- 1,060 of 32,000 slots -- so it
        /// marks which vertices are the eye rather than measuring anything.
        /// </remarks>
        public List<float> EyeData { get; } = [];

        /// <summary>
        /// Which vertex each FBX control point became, when the mesh was read from FBX.
        /// </summary>
        /// <remarks>
        /// A skin cluster addresses **control points**, not vertices, and the two are
        /// not the same numbering: the reader emits vertices in the order the triangles
        /// first reach them, and merges any that are identical in every attribute. So a
        /// weight read straight off a cluster lands on whichever vertex happens to hold
        /// that index, which is very rarely the one the weight belongs to.
        ///
        /// Empty on a mesh built from a NIF, where there are no control points to come
        /// back from.
        /// </remarks>
        public Dictionary<int, ushort> VertexOfControlPoint { get; } = [];

        public bool HasNormals => Normals.Count > 0;

        public bool HasUvs => Uvs.Count > 0;

        public bool HasColors => Colors.Count > 0;

        /// <summary>Whether the vertex's fourth word travelled with it.</summary>
        public bool HasUnusedW => UnusedW.Count > 0;

        /// <summary>Whether the eye marker travelled with it.</summary>
        public bool HasEyeData => EyeData.Count > 0;

        public bool HasTangents => Tangents.Count > 0 && Bitangents.Count > 0;

        public bool IsEmpty => Vertices.Count == 0;

        /// <summary>
        /// True when every attribute array is either empty or exactly as long as the
        /// vertex list, and every triangle index is in range.
        /// </summary>
        public bool IsWellFormed(out string? problem)
        {
            int n = Vertices.Count;

            foreach ((string name, int count) in new[]
                     {
                         ("Normals", Normals.Count),
                         ("Tangents", Tangents.Count),
                         ("Bitangents", Bitangents.Count),
                         ("UVs", Uvs.Count),
                         ("Colors", Colors.Count)
                     })
            {
                if (count != 0 && count != n)
                {
                    problem = $"{name} has {count} entries but there are {n} vertices";
                    return false;
                }
            }

            foreach (NifTriangle t in Triangles)
            {
                if (t.V1 >= n || t.V2 >= n || t.V3 >= n)
                {
                    problem = $"triangle ({t.V1}, {t.V2}, {t.V3}) indexes past {n} vertices";
                    return false;
                }
            }

            problem = null;
            return true;
        }

        /// <summary>
        /// The smallest sphere containing every vertex, as NIF stores alongside the
        /// geometry.
        /// </summary>
        /// <remarks>
        /// Ritter's approximation rather than the exact Miniball FBXWrangler uses.
        /// It over-estimates by a few percent at worst, which is harmless here: the
        /// bound is used for culling, so being slightly generous costs nothing while
        /// being too small would pop geometry out of view.
        /// </remarks>
        public (NifVector3 Center, float Radius) ComputeBoundingSphere()
        {
            if (Vertices.Count == 0)
                return (new NifVector3(), 0f);

            // Start from the most separated pair along each axis.
            NifVector3 minX = Vertices[0], maxX = Vertices[0];
            NifVector3 minY = Vertices[0], maxY = Vertices[0];
            NifVector3 minZ = Vertices[0], maxZ = Vertices[0];

            foreach (NifVector3 v in Vertices)
            {
                if (v.X < minX.X) minX = v;
                if (v.X > maxX.X) maxX = v;
                if (v.Y < minY.Y) minY = v;
                if (v.Y > maxY.Y) maxY = v;
                if (v.Z < minZ.Z) minZ = v;
                if (v.Z > maxZ.Z) maxZ = v;
            }

            float spanX = DistanceSquared(minX, maxX);
            float spanY = DistanceSquared(minY, maxY);
            float spanZ = DistanceSquared(minZ, maxZ);

            NifVector3 a = minX, b = maxX;

            if (spanY > spanX && spanY >= spanZ)
            {
                a = minY;
                b = maxY;
            }
            else if (spanZ > spanX && spanZ > spanY)
            {
                a = minZ;
                b = maxZ;
            }

            var center = new NifVector3((a.X + b.X) / 2f, (a.Y + b.Y) / 2f, (a.Z + b.Z) / 2f);
            float radius = MathF.Sqrt(DistanceSquared(a, b)) / 2f;

            // Grow just enough to swallow any vertex still outside.
            foreach (NifVector3 v in Vertices)
            {
                float distance = MathF.Sqrt(DistanceSquared(center, v));

                if (distance <= radius)
                    continue;

                float newRadius = (radius + distance) / 2f;
                float k = (newRadius - radius) / distance;

                center = new NifVector3(
                    center.X + (v.X - center.X) * k,
                    center.Y + (v.Y - center.Y) * k,
                    center.Z + (v.Z - center.Z) * k);

                radius = newRadius;
            }

            return (center, radius);
        }

        private static float DistanceSquared(NifVector3 a, NifVector3 b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        /// <summary>
        /// Recomputes vertex normals by area-weighted averaging of face normals,
        /// for meshes that arrive without any.
        /// </summary>
        public void RecalculateNormals()
        {
            Normals.Clear();

            var accumulated = new NifVector3[Vertices.Count];

            foreach (NifTriangle t in Triangles)
            {
                if (t.V1 >= Vertices.Count || t.V2 >= Vertices.Count || t.V3 >= Vertices.Count)
                    continue;

                NifVector3 a = Vertices[t.V1];
                NifVector3 b = Vertices[t.V2];
                NifVector3 c = Vertices[t.V3];

                // The cross product's length is twice the triangle's area, which is
                // exactly the weighting we want, so it is deliberately not normalised.
                float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

                var face = new NifVector3(
                    uy * vz - uz * vy,
                    uz * vx - ux * vz,
                    ux * vy - uy * vx);

                foreach (ushort index in new[] { t.V1, t.V2, t.V3 })
                {
                    accumulated[index] = new NifVector3(
                        accumulated[index].X + face.X,
                        accumulated[index].Y + face.Y,
                        accumulated[index].Z + face.Z);
                }
            }

            foreach (NifVector3 n in accumulated)
            {
                float length = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);

                Normals.Add(length > 1e-12f
                    ? new NifVector3(n.X / length, n.Y / length, n.Z / length)
                    : new NifVector3(0f, 0f, 1f));
            }
        }
    }
}
