using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// Fits Havok collision primitives to a point cloud.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="ShapeTessellator"/>, and the reason collision
    /// survives a round trip at all: an FBX carries a collision shape only as
    /// geometry, so the import side has to recover the primitive from the vertices.
    /// FBXWrangler does this with Havok's <c>hkpCreateShapeUtility</c>; the maths is
    /// elementary, so we do it directly.
    ///
    /// Which primitive to fit is decided by the node's name suffix, never guessed
    /// from the geometry (spec §3.1). A sphere and a box tessellate to point clouds
    /// that are not reliably distinguishable, so guessing would silently swap them.
    /// </remarks>
    public static class ShapeFitter
    {
        /// <summary>An axis-aligned box, as half extents about a centre.</summary>
        public static (NifVector3 Center, NifVector3 HalfExtents) FitBox(IReadOnlyList<NifVector3> points)
        {
            if (points.Count == 0)
                return (new NifVector3(), new NifVector3());

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (NifVector3 p in points)
            {
                minX = MathF.Min(minX, p.X);
                minY = MathF.Min(minY, p.Y);
                minZ = MathF.Min(minZ, p.Z);
                maxX = MathF.Max(maxX, p.X);
                maxY = MathF.Max(maxY, p.Y);
                maxZ = MathF.Max(maxZ, p.Z);
            }

            var center = new NifVector3((minX + maxX) / 2f, (minY + maxY) / 2f, (minZ + maxZ) / 2f);
            var half = new NifVector3((maxX - minX) / 2f, (maxY - minY) / 2f, (maxZ - minZ) / 2f);

            return (center, half);
        }

        /// <summary>
        /// A sphere containing every point, using the same approximation as the
        /// geometry bounds.
        /// </summary>
        public static (NifVector3 Center, float Radius) FitSphere(IReadOnlyList<NifVector3> points)
        {
            var mesh = new MeshGeometry();
            mesh.Vertices.AddRange(points);
            return mesh.ComputeBoundingSphere();
        }

        /// <summary>
        /// A capsule: the two endpoints of its axis, and its radius.
        /// </summary>
        /// <remarks>
        /// The axis is taken as the longest extent of the bounding box, which is how
        /// a capsule is always modelled in practice. The radius is the largest
        /// distance from that axis, and the endpoints are pulled in by one radius so
        /// the hemispherical caps land on the ends of the cloud rather than beyond
        /// them.
        /// </remarks>
        public static (NifVector3 First, NifVector3 Second, float Radius) FitCapsule(IReadOnlyList<NifVector3> points)
        {
            if (points.Count == 0)
                return (new NifVector3(), new NifVector3(), 0f);

            (NifVector3 center, NifVector3 half) = FitBox(points);

            // Longest box axis becomes the capsule axis.
            NifVector3 direction;
            float halfLength;

            if (half.X >= half.Y && half.X >= half.Z)
            {
                direction = new NifVector3(1f, 0f, 0f);
                halfLength = half.X;
            }
            else if (half.Y >= half.Z)
            {
                direction = new NifVector3(0f, 1f, 0f);
                halfLength = half.Y;
            }
            else
            {
                direction = new NifVector3(0f, 0f, 1f);
                halfLength = half.Z;
            }

            // Radius is the widest reach perpendicular to that axis.
            float radius = 0f;

            foreach (NifVector3 p in points)
            {
                float dx = p.X - center.X, dy = p.Y - center.Y, dz = p.Z - center.Z;
                float along = dx * direction.X + dy * direction.Y + dz * direction.Z;

                float px = dx - along * direction.X;
                float py = dy - along * direction.Y;
                float pz = dz - along * direction.Z;

                radius = MathF.Max(radius, MathF.Sqrt(px * px + py * py + pz * pz));
            }

            // Pull the endpoints in so the caps sit at the extremes of the cloud.
            float axisHalf = MathF.Max(0f, halfLength - radius);

            var first = new NifVector3(
                center.X - direction.X * axisHalf,
                center.Y - direction.Y * axisHalf,
                center.Z - direction.Z * axisHalf);

            var second = new NifVector3(
                center.X + direction.X * axisHalf,
                center.Y + direction.Y * axisHalf,
                center.Z + direction.Z * axisHalf);

            return (first, second, radius);
        }

        /// <summary>
        /// The two end points and radius of a cylinder fitted to a point cloud.
        /// </summary>
        /// <remarks>
        /// The same fit as a capsule but for where the ends go. A capsule's points are
        /// the centres of its hemispherical caps, so they sit a radius *inside* the
        /// cloud; a cylinder's are on the flat end discs themselves, at the extremes.
        /// Reusing the capsule fit here would shorten every cylinder by two radii.
        /// </remarks>
        public static (NifVector3 First, NifVector3 Second, float Radius) FitCylinder(
            IReadOnlyList<NifVector3> points)
        {
            (NifVector3 first, NifVector3 second, float radius) = FitCapsule(points);

            // Push the ends back out to where the cloud actually reaches.
            var axis = new NifVector3(second.X - first.X, second.Y - first.Y, second.Z - first.Z);
            float length = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);

            if (length < 1e-6f)
                return (first, second, radius);

            float ux = axis.X / length, uy = axis.Y / length, uz = axis.Z / length;

            return (
                new NifVector3(first.X - ux * radius, first.Y - uy * radius, first.Z - uz * radius),
                new NifVector3(second.X + ux * radius, second.Y + uy * radius, second.Z + uz * radius),
                radius);
        }

        /// <summary>
        /// The vertices and outward face planes of a convex hull, which is what
        /// <c>bhkConvexVerticesShape</c> stores.
        /// </summary>
        /// <remarks>
        /// Each plane is a unit normal plus a distance packed into the fourth
        /// component. Havok needs the planes as well as the points: it does not
        /// derive them.
        ///
        /// nif.xml states the convention exactly, and it is not the obvious one: the
        /// normal points to the *exterior*, and the fourth component is **minus** the
        /// dot product of the normal with any vertex on the plane. So a face at
        /// x = +r with normal (1,0,0) stores -r, not +r.
        ///
        /// Getting that sign wrong does not make the shape wrong in a way anything
        /// shows. The planes still sit in the right places; what inverts is which side
        /// of them counts as inside, and Havok tests containment with n.x + d &lt;= 0.
        /// A hull built with the sign flipped is inside out -- solid everywhere except
        /// where the object is. A symmetric shape such as a box hides it completely,
        /// because negating every distance maps the plane set onto itself.
        /// </remarks>
        public static (List<NifVector4> Vertices, List<NifVector4> Planes) FitConvex(IReadOnlyList<NifVector3> points)
        {
            var vertices = new List<NifVector4>();
            var planes = new List<NifVector4>();

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            if (hull.Vertices.Count == 0)
                return (vertices, planes);

            foreach (NifVector3 v in hull.Vertices)
                vertices.Add(new NifVector4(v.X, v.Y, v.Z, 0f));

            foreach (NifTriangle t in hull.Triangles)
            {
                NifVector3 a = hull.Vertices[t.V1];
                NifVector3 b = hull.Vertices[t.V2];
                NifVector3 c = hull.Vertices[t.V3];

                float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

                float nx = uy * vz - uz * vy;
                float ny = uz * vx - ux * vz;
                float nz = ux * vy - uy * vx;

                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                if (length < 1e-9f)
                    continue;

                nx /= length;
                ny /= length;
                nz /= length;

                // Minus the dot product, per the format: see the note above.
                float distance = -(nx * a.X + ny * a.Y + nz * a.Z);

                // Coplanar faces share a plane; storing each one would bloat the
                // shape without changing it.
                var plane = new NifVector4(nx, ny, nz, distance);

                if (!planes.Any(p => MathF.Abs(p.X - nx) < 1e-4f
                                     && MathF.Abs(p.Y - ny) < 1e-4f
                                     && MathF.Abs(p.Z - nz) < 1e-4f
                                     && MathF.Abs(p.W - distance) < 1e-4f))
                {
                    planes.Add(plane);
                }
            }

            return (vertices, planes);
        }
    }
}
