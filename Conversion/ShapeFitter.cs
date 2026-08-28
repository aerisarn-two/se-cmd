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
        /// The axis is the cloud's own principal direction, not the longest side of its
        /// bounding box. A box axis is only right for a capsule that happens to lie
        /// along X, Y or Z, and 268 of the 1,966 capsules Skyrim ships do not: they run
        /// diagonally, mostly in the skeletons, where a limb's collision follows the
        /// bone. Snapping those to the nearest world axis made the box longer than the
        /// capsule is thick in no direction at all, `halfLength - radius` came out at
        /// or below zero, and both endpoints collapsed onto the centre -- a capsule of
        /// zero length where a limb used to be.
        ///
        /// The radius is the largest distance from that axis, and the endpoints are
        /// pulled in by one radius so the hemispherical caps land on the ends of the
        /// cloud rather than beyond them.
        ///
        /// Which end is "first" is not in the cloud either -- it is symmetric about its
        /// own middle -- so with no hint the fit has to choose, and the choice is
        /// Bethesda's: 1,865 of those 1,966 capsules put `First Point` at the
        /// *positive* end of the axis.
        ///
        /// <paramref name="axisHint"/> settles both questions when the shape came from
        /// a NIF, and is why it is passed. A principal axis is the best guess available
        /// from a bare cloud, and on a capsule shorter than it is wide it is the wrong
        /// guess: such a cloud spreads further *across* the axis than along it, so the
        /// fit comes back perpendicular to the truth. Skyrim's skeletons are full of
        /// them. The hint is a direction only -- the endpoints and radius are still
        /// measured from the cloud, so a capsule stretched or moved in a DCC tool comes
        /// back stretched or moved.
        /// </remarks>
        public static (NifVector3 First, NifVector3 Second, float Radius) FitCapsule(
            IReadOnlyList<NifVector3> points, NifVector3? axisHint = null)
        {
            if (points.Count == 0)
                return (new NifVector3(), new NifVector3(), 0f);

            NifVector3 center = Centroid(points);

            NifVector3? hint = Normalised(axisHint);
            NifVector3 direction = hint ?? PrincipalAxis(points, center);

            // Extent along the axis, measured from the centroid rather than assumed
            // symmetric: a cloud that has been edited need not be.
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            float radius = 0f;

            foreach (NifVector3 p in points)
            {
                float dx = p.X - center.X, dy = p.Y - center.Y, dz = p.Z - center.Z;
                float along = dx * direction.X + dy * direction.Y + dz * direction.Z;

                min = MathF.Min(min, along);
                max = MathF.Max(max, along);

                float px = dx - along * direction.X;
                float py = dy - along * direction.Y;
                float pz = dz - along * direction.Z;

                radius = MathF.Max(radius, MathF.Sqrt(px * px + py * py + pz * pz));
            }

            // Pull the endpoints in so the caps sit at the extremes of the cloud. A
            // cloud shorter than it is wide -- a capsule that is all cap -- gives a
            // negative span, which is clamped to a single point at the centre.
            float high = MathF.Max(max - radius, min + radius);
            float low = MathF.Min(max - radius, min + radius);

            if (high < low)
                high = low = 0.5f * (min + max);

            // The hint runs first-to-second, so with one the first point is the low end
            // along it. Without one there is nothing to follow and the convention takes
            // over: first at the positive end, which is where 94.9% of Skyrim's are.
            (float firstAlong, float secondAlong) = hint is null ? (high, low) : (low, high);

            var first = new NifVector3(
                center.X + direction.X * firstAlong,
                center.Y + direction.Y * firstAlong,
                center.Z + direction.Z * firstAlong);

            var second = new NifVector3(
                center.X + direction.X * secondAlong,
                center.Y + direction.Y * secondAlong,
                center.Z + direction.Z * secondAlong);

            return (first, second, radius);
        }

        /// <summary>A direction of unit length, or null when there is no direction in it.</summary>
        private static NifVector3? Normalised(NifVector3? v)
        {
            if (v is not { } axis)
                return null;

            float length = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);

            return length < 1e-12f
                ? null
                : new NifVector3(axis.X / length, axis.Y / length, axis.Z / length);
        }

        /// <summary>The mean of a point cloud.</summary>
        private static NifVector3 Centroid(IReadOnlyList<NifVector3> points)
        {
            double x = 0, y = 0, z = 0;

            foreach (NifVector3 p in points)
            {
                x += p.X;
                y += p.Y;
                z += p.Z;
            }

            return new NifVector3(
                (float)(x / points.Count), (float)(y / points.Count), (float)(z / points.Count));
        }

        /// <summary>
        /// The direction a point cloud is most spread along.
        /// </summary>
        /// <remarks>
        /// The dominant eigenvector of the covariance, by power iteration -- twenty
        /// passes, which is far past convergence for a 3x3 and costs nothing at these
        /// cloud sizes.
        ///
        /// Seeded from the widest covariance column rather than a fixed vector, because
        /// power iteration cannot leave a starting vector that is exactly orthogonal to
        /// the answer, and a fixed seed hits that on precisely the axis-aligned capsules
        /// that are the common case. The result is sign-normalised so the axis does not
        /// flip between two runs on the same cloud.
        ///
        /// Doubles throughout: the covariance of a cloud in Havok units squares numbers
        /// that are already small, and in floats the smallest capsules lose the axis in
        /// the rounding.
        /// </remarks>
        private static NifVector3 PrincipalAxis(IReadOnlyList<NifVector3> points, NifVector3 center)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;

            foreach (NifVector3 p in points)
            {
                double dx = p.X - center.X, dy = p.Y - center.Y, dz = p.Z - center.Z;

                xx += dx * dx; xy += dx * dy; xz += dx * dz;
                yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }

            // The widest column is the best-conditioned seed.
            double[][] columns =
            [
                [xx, xy, xz],
                [xy, yy, yz],
                [xz, yz, zz]
            ];

            double[] v = columns[0];

            foreach (double[] column in columns)
            {
                if (Norm(column) > Norm(v))
                    v = column;
            }

            if (Norm(v) < 1e-30)
                return new NifVector3(1f, 0f, 0f);

            v = [v[0], v[1], v[2]];

            for (int i = 0; i < 20; i++)
            {
                double[] next =
                [
                    xx * v[0] + xy * v[1] + xz * v[2],
                    xy * v[0] + yy * v[1] + yz * v[2],
                    xz * v[0] + yz * v[1] + zz * v[2]
                ];

                double norm = Math.Sqrt(Norm(next));

                if (norm < 1e-30)
                    break;

                v = [next[0] / norm, next[1] / norm, next[2] / norm];
            }

            double length = Math.Sqrt(Norm(v));

            if (length < 1e-30)
                return new NifVector3(1f, 0f, 0f);

            // A consistent sign, so the same cloud never fits two opposite axes.
            double sign = v[0] + v[1] + v[2] < 0 ? -1 : 1;

            return new NifVector3(
                (float)(sign * v[0] / length),
                (float)(sign * v[1] / length),
                (float)(sign * v[2] / length));
        }

        private static double Norm(double[] v) => v[0] * v[0] + v[1] * v[1] + v[2] * v[2];

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
            IReadOnlyList<NifVector3> points, NifVector3? axisHint = null)
        {
            (NifVector3 first, NifVector3 second, float radius) = FitCapsule(points, axisHint);

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
        /// The plane a flat point cloud lies in, and the box bounding it.
        /// </summary>
        /// <remarks>
        /// The normal comes from the widest triangle the cloud spans, which for the
        /// rectangle a plane shape tessellates to is exact. The constant is the
        /// plane's distance from the origin along that normal — nif.xml's own wording,
        /// and *not* the negated convention a convex hull's face planes use.
        /// </remarks>
        public static (NifVector3 Normal, float Constant, NifVector3 Center, NifVector3 HalfExtents)
            FitPlane(IReadOnlyList<NifVector3> points)
        {
            (NifVector3 center, NifVector3 half) = FitBox(points);

            var normal = new NifVector3(0f, 0f, 1f);
            float best = 0f;

            for (int i = 0; i + 2 < points.Count; i++)
            {
                NifVector3 a = points[i], b = points[i + 1], c = points[i + 2];

                var u = new NifVector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                var v = new NifVector3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);

                var cross = new NifVector3(
                    u.Y * v.Z - u.Z * v.Y,
                    u.Z * v.X - u.X * v.Z,
                    u.X * v.Y - u.Y * v.X);

                float size = MathF.Sqrt(cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z);

                if (size > best)
                {
                    best = size;
                    normal = new NifVector3(cross.X / size, cross.Y / size, cross.Z / size);
                }
            }

            float constant = center.X * normal.X + center.Y * normal.Y + center.Z * normal.Z;

            return (normal, constant, center, half);
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
        /// <summary>How near two face planes must be to count as the same one.</summary>
        private const float CoplanarTolerance = 1e-3f;

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

                // Coplanar faces share a plane; storing each one would bloat the shape
                // without changing it.
                //
                // A tenth of a degree, and a millimetre of offset. The tolerance was
                // 1e-4, which is finer than the hull's own arithmetic: a 326-corner hull
                // came out with 225 planes where the file it was rebuilt from has 165,
                // the extras being the same face found twice. At 1e-3 it produces 172,
                // and every one of the file's 165 planes is among them. Looser still is
                // worse rather than better -- at 3e-3 the count reaches 166 by merging
                // two faces that are genuinely different, and only 163 of the source's
                // planes survive.
                var plane = new NifVector4(nx, ny, nz, distance);

                if (!planes.Any(p => MathF.Abs(p.X - nx) < CoplanarTolerance
                                     && MathF.Abs(p.Y - ny) < CoplanarTolerance
                                     && MathF.Abs(p.Z - nz) < CoplanarTolerance
                                     && MathF.Abs(p.W - distance) < CoplanarTolerance))
                {
                    planes.Add(plane);
                }
            }

            return (vertices, planes);
        }
    }
}
