using NIFSharp;
using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// Generates per-vertex tangents and bitangents from positions, normals and UVs.
    /// </summary>
    /// <remarks>
    /// Ported from NifSkope's <c>spTangentSpace</c> (`src/spells/tangentspace.cpp`),
    /// which is the right source for two reasons: it both writes these and renders
    /// from them, so its pairing of the two vectors is self-consistent, and it is the
    /// same lineage as the rest of this port. ck-cmd instead asks the FBX SDK for
    /// tangents and then swaps its tangent and binormal on the way in, with the note
    /// "switched to uniform with nifskope" — generating them the NifSkope way makes
    /// that swap unnecessary rather than something to reproduce.
    ///
    /// Two departures from the textbook algorithm are deliberate there, and are kept:
    ///
    /// <list type="bullet">
    /// <item>The UV determinant is used for its <b>sign only</b>. The usual method
    /// divides by it, weighting each triangle by UV area; NifSkope replaces that with
    /// ±1 — the division is commented out in the original with the note that this
    /// "seems to produce better results". A degenerate UV triangle therefore cannot
    /// blow up the accumulation.</item>
    /// <item>Each triangle's contribution is <b>normalised before accumulating</b>, so
    /// a large triangle counts for no more than a small one.</item>
    /// </list>
    ///
    /// The bitangent is orthogonalised against both the normal and the tangent rather
    /// than taken as their cross product — that line exists in the original and is
    /// commented out — so its handedness comes from the UV layout instead of being
    /// imposed.
    /// </remarks>
    public static class TangentSpace
    {
        /// <summary>
        /// Fills in the mesh's tangents and bitangents, replacing any it already has.
        /// </summary>
        /// <returns>
        /// Whether they could be generated. Positions, normals, UVs and triangles all
        /// have to be present and agree in length, which is the same condition
        /// NifSkope reports and declines on.
        /// </returns>
        public static bool Generate(MeshGeometry mesh)
        {
            int count = mesh.Vertices.Count;

            if (count == 0
                || mesh.Normals.Count != count
                || mesh.Uvs.Count != count
                || mesh.Triangles.Count == 0)
            {
                return false;
            }

            var tangents = new NifVector3[count];
            var bitangents = new NifVector3[count];

            foreach (NifTriangle triangle in mesh.Triangles)
            {
                int i1 = triangle.V1, i2 = triangle.V2, i3 = triangle.V3;

                if (!InRange(i1, count) || !InRange(i2, count) || !InRange(i3, count))
                    continue;

                NifVector3 v1 = mesh.Vertices[i1];
                NifVector2 w1 = mesh.Uvs[i1];

                NifVector3 e2 = Subtract(mesh.Vertices[i2], v1);
                NifVector3 e3 = Subtract(mesh.Vertices[i3], v1);

                float s2 = mesh.Uvs[i2].X - w1.X, t2 = mesh.Uvs[i2].Y - w1.Y;
                float s3 = mesh.Uvs[i3].X - w1.X, t3 = mesh.Uvs[i3].Y - w1.Y;

                // Sign only: see the note above.
                float r = s2 * t3 - s3 * t2 >= 0f ? 1f : -1f;

                var along = new NifVector3(
                    (t3 * e2.X - t2 * e3.X) * r,
                    (t3 * e2.Y - t2 * e3.Y) * r,
                    (t3 * e2.Z - t2 * e3.Z) * r);

                var across = new NifVector3(
                    (s2 * e3.X - s3 * e2.X) * r,
                    (s2 * e3.Y - s3 * e2.Y) * r,
                    (s2 * e3.Z - s3 * e2.Z) * r);

                along = Normalize(along);
                across = Normalize(across);

                foreach (int i in new[] { i1, i2, i3 })
                {
                    tangents[i] = Add(tangents[i], across);
                    bitangents[i] = Add(bitangents[i], along);
                }
            }

            mesh.Tangents.Clear();
            mesh.Bitangents.Clear();

            for (int i = 0; i < count; i++)
            {
                NifVector3 normal = mesh.Normals[i];
                NifVector3 t = tangents[i], b = bitangents[i];

                if (IsZero(t) || IsZero(b))
                {
                    // A vertex no triangle contributed to still needs a frame, and an
                    // arbitrary stable one beats leaving a zero vector for a shader to
                    // divide by.
                    t = new NifVector3(normal.Y, normal.Z, normal.X);
                    b = Cross(normal, t);
                }
                else
                {
                    t = Normalize(t);
                    t = Normalize(Subtract(t, Scale(normal, Dot(normal, t))));

                    b = Normalize(b);
                    b = Subtract(b, Scale(normal, Dot(normal, b)));
                    b = Normalize(Subtract(b, Scale(t, Dot(t, b))));
                }

                mesh.Tangents.Add(t);
                mesh.Bitangents.Add(b);
            }

            return true;
        }

        private static bool InRange(int index, int count) => index >= 0 && index < count;

        private static bool IsZero(NifVector3 v) => v.X == 0f && v.Y == 0f && v.Z == 0f;

        private static NifVector3 Add(NifVector3 a, NifVector3 b) =>
            new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        private static NifVector3 Subtract(NifVector3 a, NifVector3 b) =>
            new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        private static NifVector3 Scale(NifVector3 v, float by) =>
            new(v.X * by, v.Y * by, v.Z * by);

        private static float Dot(NifVector3 a, NifVector3 b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static NifVector3 Cross(NifVector3 a, NifVector3 b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        private static NifVector3 Normalize(NifVector3 v)
        {
            float length = MathF.Sqrt(Dot(v, v));

            return length < 1e-12f ? v : Scale(v, 1f / length);
        }
    }
}
