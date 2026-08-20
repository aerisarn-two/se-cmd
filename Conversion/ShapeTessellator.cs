using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// Turns Havok collision primitives into triangle meshes.
    /// </summary>
    /// <remarks>
    /// FBX has no shape primitives, so a collision shape can only cross as geometry
    /// (spec §4.8). FBXWrangler gets these from the Havok SDK's
    /// <c>hkpShapeConverter</c>; we generate them directly, which is elementary for
    /// the primitives and a convex hull for the vertex shapes.
    ///
    /// The result is display and editing geometry. It never goes back to Havok as-is
    /// — the import side reads the node naming and rebuilds real shapes — so
    /// tessellation density is a readability choice, not a fidelity one.
    /// </remarks>
    public static class ShapeTessellator
    {
        /// <summary>
        /// Havok works in metres, NIF in Skyrim units. This is the conversion
        /// FBXWrangler applies when emitting collision geometry.
        /// </summary>
        public const float BhkScaleFactor = 69.99125f;

        /// <summary>Its inverse as FBXWrangler spells it, which is not exact.</summary>
        public const float BhkScaleFactorInverse = 0.01428f;

        /// <summary>A box centred on the origin, given its half extents.</summary>
        public static MeshGeometry Box(NifVector3 halfExtents)
        {
            var mesh = new MeshGeometry();
            float x = halfExtents.X, y = halfExtents.Y, z = halfExtents.Z;

            NifVector3[] corners =
            [
                new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z),
                new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z)
            ];

            mesh.Vertices.AddRange(corners);

            // Six faces, wound outwards.
            int[][] faces =
            [
                [0, 3, 2, 1], // -Z
                [4, 5, 6, 7], // +Z
                [0, 1, 5, 4], // -Y
                [2, 3, 7, 6], // +Y
                [0, 4, 7, 3], // -X
                [1, 2, 6, 5]  // +X
            ];

            foreach (int[] face in faces)
            {
                mesh.Triangles.Add(new NifTriangle((ushort)face[0], (ushort)face[1], (ushort)face[2]));
                mesh.Triangles.Add(new NifTriangle((ushort)face[0], (ushort)face[2], (ushort)face[3]));
            }

            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>A UV sphere centred on the origin.</summary>
        /// <remarks>
        /// Built topologically closed: the poles are single vertices rather than
        /// degenerate rings, and the last segment reuses the first instead of
        /// duplicating it at the seam. A collision hull whose edges are not shared
        /// has holes as far as a physics engine is concerned, even when it looks
        /// solid.
        /// </remarks>
        public static MeshGeometry Sphere(float radius, int segments = 16, int rings = 8)
        {
            var mesh = new MeshGeometry();

            segments = Math.Max(3, segments);
            rings = Math.Max(2, rings);

            var north = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, radius));

            // Interior rings only; the poles are separate.
            for (int ring = 1; ring < rings; ring++)
            {
                float phi = (float)ring / rings * MathF.PI;
                float z = MathF.Cos(phi) * radius;
                float ringRadius = MathF.Sin(phi) * radius;

                for (int segment = 0; segment < segments; segment++)
                {
                    float theta = (float)segment / segments * MathF.Tau;

                    mesh.Vertices.Add(new NifVector3(
                        MathF.Cos(theta) * ringRadius,
                        MathF.Sin(theta) * ringRadius,
                        z));
                }
            }

            var south = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, -radius));

            int First(int ring) => 1 + (ring - 1) * segments;

            // Cap fans.
            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;

                mesh.Triangles.Add(new NifTriangle(
                    north, (ushort)(First(1) + segment), (ushort)(First(1) + next)));

                mesh.Triangles.Add(new NifTriangle(
                    south, (ushort)(First(rings - 1) + next), (ushort)(First(rings - 1) + segment)));
            }

            // Bands between consecutive rings.
            for (int ring = 1; ring < rings - 1; ring++)
            {
                for (int segment = 0; segment < segments; segment++)
                {
                    int next = (segment + 1) % segments;

                    var a = (ushort)(First(ring) + segment);
                    var b = (ushort)(First(ring) + next);
                    var c = (ushort)(First(ring + 1) + segment);
                    var d = (ushort)(First(ring + 1) + next);

                    mesh.Triangles.Add(new NifTriangle(a, c, b));
                    mesh.Triangles.Add(new NifTriangle(b, c, d));
                }
            }

            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// A capsule: a cylinder between two points, capped with hemispheres.
        /// </summary>
        /// <summary>
        /// A cylinder between two points, with flat ends.
        /// </summary>
        /// <remarks>
        /// The same shape as a capsule but for the caps: a capsule's ends are
        /// hemispheres and reach a radius beyond each point, a cylinder's are discs
        /// through the points themselves. Getting that wrong makes a collision that is
        /// two radii too long, which is exactly the kind of error nothing reports.
        ///
        /// ck-cmd converts neither — its `recursive_convert` has no
        /// <c>bhkCylinderShape</c> case at all, so a body whose shape is one leaves
        /// with no geometry and the whole collision object is lost. The game ships
        /// them, so this port converts them.
        /// </remarks>
        /// <summary>
        /// A plane, bounded by the box it is given.
        /// </summary>
        /// <remarks>
        /// A `bhkPlaneShape` is an infinite plane with an AABB saying which part of it
        /// is real — the game uses them for water surfaces and for the invisible floor
        /// a fish egg cluster sits on. FBX has no infinite anything, so it travels as
        /// the rectangle the box cuts out of it.
        ///
        /// Wound both ways, as a flat hull is: the import refits from these triangles,
        /// and a one-sided quad gives the fit a surface rather than a solid.
        /// </remarks>
        public static MeshGeometry Plane(
            NifVector3 normal, float constant, NifVector3 centre, NifVector3 halfExtents)
        {
            var mesh = new MeshGeometry();

            float length = MathF.Sqrt(
                normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);

            if (length < 1e-9f)
                return mesh;

            var n = new NifVector3(normal.X / length, normal.Y / length, normal.Z / length);

            // Two axes in the plane. The cross with whichever world axis the normal
            // leans on least is the one that does not collapse.
            NifVector3 seed = MathF.Abs(n.X) <= MathF.Abs(n.Y) && MathF.Abs(n.X) <= MathF.Abs(n.Z)
                ? new NifVector3(1f, 0f, 0f)
                : MathF.Abs(n.Y) <= MathF.Abs(n.Z)
                    ? new NifVector3(0f, 1f, 0f)
                    : new NifVector3(0f, 0f, 1f);

            NifVector3 u = Normalize(Cross(n, seed));
            NifVector3 v = Cross(n, u);

            // The box's reach along each in-plane axis, and its centre pulled onto the
            // plane so the rectangle lies in it rather than beside it.
            float du = MathF.Abs(u.X * halfExtents.X) + MathF.Abs(u.Y * halfExtents.Y)
                       + MathF.Abs(u.Z * halfExtents.Z);

            float dv = MathF.Abs(v.X * halfExtents.X) + MathF.Abs(v.Y * halfExtents.Y)
                       + MathF.Abs(v.Z * halfExtents.Z);

            float off = centre.X * n.X + centre.Y * n.Y + centre.Z * n.Z - constant;

            var origin = new NifVector3(
                centre.X - n.X * off, centre.Y - n.Y * off, centre.Z - n.Z * off);

            foreach ((float su, float sv) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            {
                mesh.Vertices.Add(new NifVector3(
                    origin.X + u.X * du * su + v.X * dv * sv,
                    origin.Y + u.Y * du * su + v.Y * dv * sv,
                    origin.Z + u.Z * du * su + v.Z * dv * sv));
            }

            mesh.Triangles.Add(new NifTriangle(0, 1, 2));
            mesh.Triangles.Add(new NifTriangle(0, 2, 3));
            mesh.Triangles.Add(new NifTriangle(0, 2, 1));
            mesh.Triangles.Add(new NifTriangle(0, 3, 2));

            mesh.RecalculateNormals();

            return mesh;
        }

        private static NifVector3 Cross(NifVector3 a, NifVector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        private static NifVector3 Normalize(NifVector3 v)
        {
            float length = Length(v);

            return length < 1e-9f ? v : new NifVector3(v.X / length, v.Y / length, v.Z / length);
        }

        public static MeshGeometry Cylinder(NifVector3 first, NifVector3 second, float radius, int segments = 16)
        {
            segments = Math.Max(3, segments);

            var axis = new NifVector3(second.X - first.X, second.Y - first.Y, second.Z - first.Z);
            float length = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);

            if (length < 1e-6f)
                return Sphere(radius, segments);

            var mesh = new MeshGeometry();

            for (int end = 0; end < 2; end++)
            {
                float z = end == 0 ? 0f : length;

                for (int segment = 0; segment < segments; segment++)
                {
                    float theta = (float)segment / segments * MathF.Tau;
                    mesh.Vertices.Add(new NifVector3(MathF.Cos(theta) * radius, MathF.Sin(theta) * radius, z));
                }
            }

            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;

                var a = (ushort)segment;
                var b = (ushort)next;
                var c = (ushort)(segments + segment);
                var d = (ushort)(segments + next);

                mesh.Triangles.Add(new NifTriangle(a, c, b));
                mesh.Triangles.Add(new NifTriangle(b, c, d));
            }

            // Flat caps, fanned from a centre point on each end plane.
            var bottom = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, 0f));

            var top = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, length));

            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;

                mesh.Triangles.Add(new NifTriangle(bottom, (ushort)next, (ushort)segment));
                mesh.Triangles.Add(new NifTriangle(top, (ushort)(segments + segment), (ushort)(segments + next)));
            }

            AlignToAxis(mesh, first, axis, length);
            mesh.RecalculateNormals();
            return mesh;
        }

        public static MeshGeometry Capsule(NifVector3 first, NifVector3 second, float radius, int segments = 16)
        {
            segments = Math.Max(3, segments);

            // Build along the axis, then rotate the whole thing into place.
            var axis = new NifVector3(second.X - first.X, second.Y - first.Y, second.Z - first.Z);
            float length = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);

            if (length < 1e-6f)
                return Sphere(radius, segments);

            var mesh = new MeshGeometry();

            // Two rings for the cylinder body, seam shared so the surface stays
            // topologically closed.
            for (int end = 0; end < 2; end++)
            {
                float z = end == 0 ? 0f : length;

                for (int segment = 0; segment < segments; segment++)
                {
                    float theta = (float)segment / segments * MathF.Tau;
                    mesh.Vertices.Add(new NifVector3(MathF.Cos(theta) * radius, MathF.Sin(theta) * radius, z));
                }
            }

            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;

                var a = (ushort)segment;
                var b = (ushort)next;
                var c = (ushort)(segments + segment);
                var d = (ushort)(segments + next);

                mesh.Triangles.Add(new NifTriangle(a, c, b));
                mesh.Triangles.Add(new NifTriangle(b, c, d));
            }

            // Caps as single fan points: enough to close the volume without doubling
            // the vertex count, since this is display geometry.
            var bottom = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, -radius));

            var top = (ushort)mesh.Vertices.Count;
            mesh.Vertices.Add(new NifVector3(0f, 0f, length + radius));

            for (int segment = 0; segment < segments; segment++)
            {
                int next = (segment + 1) % segments;

                mesh.Triangles.Add(new NifTriangle(bottom, (ushort)next, (ushort)segment));
                mesh.Triangles.Add(new NifTriangle(top, (ushort)(segments + segment), (ushort)(segments + next)));
            }

            AlignToAxis(mesh, first, axis, length);
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>
        /// Rotates a mesh built along +Z so that +Z lies along <paramref name="axis"/>,
        /// then moves it to <paramref name="origin"/>.
        /// </summary>
        private static void AlignToAxis(MeshGeometry mesh, NifVector3 origin, NifVector3 axis, float length)
        {
            var d = new NifVector3(axis.X / length, axis.Y / length, axis.Z / length);

            // Rotation taking +Z onto d, via the axis-angle between them.
            var z = new NifVector3(0f, 0f, 1f);
            float dot = d.Z;

            NifMatrix33 rotation;

            if (dot > 0.99999f)
            {
                rotation = NifMatrix33.Identity;
            }
            else if (dot < -0.99999f)
            {
                // Antiparallel: a half turn about any perpendicular axis.
                rotation = new NifMatrix33 { M11 = 1f, M22 = -1f, M33 = -1f };
            }
            else
            {
                var v = new NifVector3(
                    z.Y * d.Z - z.Z * d.Y,
                    z.Z * d.X - z.X * d.Z,
                    z.X * d.Y - z.Y * d.X);

                float c = dot;
                float k = 1f / (1f + c);

                // Rodrigues' formula, expanded and transposed: it is usually written
                // for column vectors, while NifTransform.Apply multiplies a row
                // vector, so the rows here have to be the images of the basis
                // vectors. Written the other way round, +Z lands on (-d.x, -d.y, d.z)
                // and the shape points the wrong way.
                rotation = new NifMatrix33
                {
                    M11 = v.X * v.X * k + c,
                    M12 = v.X * v.Y * k + v.Z,
                    M13 = v.X * v.Z * k - v.Y,
                    M21 = v.Y * v.X * k - v.Z,
                    M22 = v.Y * v.Y * k + c,
                    M23 = v.Y * v.Z * k + v.X,
                    M31 = v.Z * v.X * k + v.Y,
                    M32 = v.Z * v.Y * k - v.X,
                    M33 = v.Z * v.Z * k + c
                };
            }

            var transform = new NifTransform(origin, rotation, 1f);

            for (int i = 0; i < mesh.Vertices.Count; i++)
                mesh.Vertices[i] = transform.Apply(mesh.Vertices[i]);
        }

        /// <summary>
        /// The convex hull of a point set, as a triangle mesh.
        /// </summary>
        /// <remarks>
        /// An incremental hull: start from a tetrahedron, then for each remaining
        /// point delete every face it can see and stitch the resulting boundary back
        /// to it.
        ///
        /// A *flat* hull is not broken input. The game ships them — `byohwrdoorload01`
        /// draws its load door as four coplanar points — and a hull with no volume has
        /// no tetrahedron to start from, so it used to yield an empty mesh, which lost
        /// the shape, the body and the collision object above it. It is tessellated as
        /// the polygon it is instead, wound both ways so it exists from either side.
        ///
        /// Fewer than three points is genuinely nothing, and stays nothing.
        /// </remarks>
        public static MeshGeometry ConvexHull(IReadOnlyList<NifVector3> raw)
        {
            var mesh = new MeshGeometry();

            // Points that sit on top of one another make faces with no area, and the
            // hull cannot use them for anything. Collision hulls arrive with them: the
            // game's own shapes are quantised, so what were distinct corners in the
            // authoring tool arrive equal.
            List<NifVector3> points = Distinct(raw);

            // Merging can leave too few points to be a surface, and that is not the
            // same as there being no shape. A daedric mace's haft collision is half a
            // metre long and two *microns* thick, so every point merges onto one of
            // two ends -- and the sliver it describes is still the collision the game
            // ships. The unmerged points are what the flat case then works from.
            if (points.Count < 3 && raw.Count >= 3)
                points = [.. raw];

            if (points.Count < 3)
                return mesh;

            // How far outside the surface a point has to be before it is treated as
            // outside at all. Relative to the shape, because a Havok shape may be a
            // centimetre across or ten metres, and an absolute tolerance is either
            // meaningless on one or ruinous on the other.
            float tolerance = Tolerance(points);


            if (points.Count < 4 || !FindInitialTetrahedron(points, out int[] seed))
                return PlanarHull(points);


            var vertices = new List<NifVector3>(points);
            var faces = new List<(int A, int B, int C)>
            {
                (seed[0], seed[1], seed[2]),
                (seed[0], seed[2], seed[3]),
                (seed[0], seed[3], seed[1]),
                (seed[1], seed[3], seed[2])
            };

            // Wind every seed face away from the centroid.
            NifVector3 inside = Average([vertices[seed[0]], vertices[seed[1]], vertices[seed[2]], vertices[seed[3]]]);

            for (int i = 0; i < faces.Count; i++)
            {
                if (SignedDistance(vertices, faces[i], inside) > 0)
                    faces[i] = (faces[i].A, faces[i].C, faces[i].B);
            }

            for (int p = 0; p < vertices.Count; p++)
            {

                if (seed.Contains(p))
                    continue;

                NifVector3 point = vertices[p];

                var visible = new List<(int A, int B, int C)>();

                foreach ((int A, int B, int C) face in faces)
                {
                    float distance = SignedDistance(vertices, face, point);

                    // A face with no area is removed rather than kept. It cannot be
                    // part of a hull, and leaving it in is what breaks the surface:
                    // nothing can see it, so nothing ever takes it away, and every
                    // later point stitches its boundary edges onto a hole that never
                    // closes. `snowdriftm01int` turned 222 points into 309,394 faces
                    // that way, where a hull of 222 points has at most 440, and the
                    // sweep ran for hours on one mesh.
                    if (float.IsNaN(distance) || distance > tolerance)
                        visible.Add(face);
                }

                if (visible.Count == 0)
                    continue;

                // Edges on the boundary of the visible region appear exactly once
                // across it; interior edges appear twice and cancel.
                var boundary = new List<(int From, int To)>();

                foreach ((int A, int B, int C) face in visible)
                {
                    foreach ((int From, int To) edge in new[] { (face.A, face.B), (face.B, face.C), (face.C, face.A) })
                    {
                        int at = boundary.FindIndex(e => e.From == edge.To && e.To == edge.From);

                        if (at >= 0)
                            boundary.RemoveAt(at);
                        else
                            boundary.Add(edge);
                    }
                }

                faces.RemoveAll(visible.Contains);

                foreach ((int from, int to) in boundary)
                    faces.Add((from, to, p));

                // A hull of n points has at most 2n - 4 faces. Past that the surface
                // has stopped being closed and every further point makes it worse, so
                // there is nothing to be gained by going on -- and a great deal to
                // lose: this is what ran for hours on one mesh rather than failing.
                if (faces.Count > 2 * points.Count)
                {
                    break;
                }
            }

            // Keep only the vertices the hull actually uses, renumbered.
            var remap = new Dictionary<int, ushort>();

            foreach ((int A, int B, int C) face in faces)
            {
                mesh.Triangles.Add(new NifTriangle(Index(face.A), Index(face.B), Index(face.C)));
            }

            mesh.RecalculateNormals();
            return mesh;

            ushort Index(int original)
            {
                if (remap.TryGetValue(original, out ushort mapped))
                    return mapped;

                mapped = (ushort)mesh.Vertices.Count;
                remap[original] = mapped;
                mesh.Vertices.Add(vertices[original]);
                return mapped;
            }
        }

        /// <summary>
        /// A hull with no volume, tessellated as the polygon it is.
        /// </summary>
        /// <remarks>
        /// The points are already the hull's own vertices — Havok stores a
        /// `bhkConvexVerticesShape` as its corners, not as a cloud to be reduced — so
        /// the job is to order them around their common plane and fan them.
        ///
        /// Wound both ways. A single-sided quad is invisible from behind in a DCC tool
        /// and, more to the point, the import refits from these triangles: a fan in one
        /// direction only would give the fit a surface rather than a solid to work from.
        /// </remarks>
        private static MeshGeometry PlanarHull(IReadOnlyList<NifVector3> points)
        {
            var mesh = new MeshGeometry();

            NifVector3 centre = Average(points);

            // Two axes spanning the points' plane, from the longest spread and
            // whatever is most perpendicular to it.
            NifVector3 u = Farthest(points, centre);
            float length = Length(u);

            if (length < 1e-9f)
                return mesh;

            u = new NifVector3(u.X / length, u.Y / length, u.Z / length);

            NifVector3 v = new(0f, 0f, 0f);
            float best = 0f;

            foreach (NifVector3 p in points)
            {
                var d = new NifVector3(p.X - centre.X, p.Y - centre.Y, p.Z - centre.Z);
                float along = d.X * u.X + d.Y * u.Y + d.Z * u.Z;
                var perp = new NifVector3(d.X - along * u.X, d.Y - along * u.Y, d.Z - along * u.Z);
                float size = Length(perp);

                if (size > best)
                {
                    best = size;
                    v = new NifVector3(perp.X / size, perp.Y / size, perp.Z / size);
                }
            }

            if (best < 1e-9f)
                return mesh;

            // Order the corners around the plane, so the fan does not self-cross.
            var ordered = points
                .Select((p, i) => (Index: i, Angle: MathF.Atan2(
                    (p.X - centre.X) * v.X + (p.Y - centre.Y) * v.Y + (p.Z - centre.Z) * v.Z,
                    (p.X - centre.X) * u.X + (p.Y - centre.Y) * u.Y + (p.Z - centre.Z) * u.Z)))
                .OrderBy(e => e.Angle)
                .Select(e => e.Index)
                .ToList();

            foreach (NifVector3 p in points)
                mesh.Vertices.Add(p);

            for (int i = 1; i + 1 < ordered.Count; i++)
            {
                var a = (ushort)ordered[0];
                var b = (ushort)ordered[i];
                var c = (ushort)ordered[i + 1];

                mesh.Triangles.Add(new NifTriangle(a, b, c));
                mesh.Triangles.Add(new NifTriangle(a, c, b));
            }

            mesh.RecalculateNormals();

            return mesh;
        }

        private static float Length(NifVector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

        /// <summary>The offset from the centre to the point furthest from it.</summary>
        private static NifVector3 Farthest(IReadOnlyList<NifVector3> points, NifVector3 centre)
        {
            var best = new NifVector3(0f, 0f, 0f);
            float far = -1f;

            foreach (NifVector3 p in points)
            {
                var d = new NifVector3(p.X - centre.X, p.Y - centre.Y, p.Z - centre.Z);
                float size = Length(d);

                if (size > far)
                {
                    far = size;
                    best = d;
                }
            }

            return best;
        }

        /// <summary>The scale below which two points are the same point.</summary>
        private static float Tolerance(IReadOnlyList<NifVector3> points)
        {
            if (points.Count == 0)
                return 1e-9f;

            float minX = points[0].X, minY = points[0].Y, minZ = points[0].Z;
            float maxX = minX, maxY = minY, maxZ = minZ;

            foreach (NifVector3 p in points)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
            }

            float extent = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));

            return MathF.Max(extent * 1e-5f, 1e-9f);
        }

        /// <summary>
        /// The points with near-duplicates merged.
        /// </summary>
        /// <remarks>
        /// Two points closer together than the shape's own scale can distinguish are
        /// one point as far as a hull is concerned, and keeping both makes faces with
        /// no area. The tolerance is relative to the extent, since a Havok shape may
        /// be a centimetre across or ten metres.
        /// </remarks>
        private static List<NifVector3> Distinct(IReadOnlyList<NifVector3> points)
        {
            if (points.Count == 0)
                return [];

            float minX = points[0].X, minY = points[0].Y, minZ = points[0].Z;
            float maxX = minX, maxY = minY, maxZ = minZ;

            foreach (NifVector3 p in points)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
            }

            float extent = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
            float tolerance = MathF.Max(extent * 1e-5f, 1e-9f);

            var seen = new HashSet<(int, int, int)>();
            var kept = new List<NifVector3>(points.Count);

            foreach (NifVector3 p in points)
            {
                var cell = (
                    (int)MathF.Round(p.X / tolerance),
                    (int)MathF.Round(p.Y / tolerance),
                    (int)MathF.Round(p.Z / tolerance));

                if (seen.Add(cell))
                    kept.Add(p);
            }

            return kept;
        }

        /// <summary>
        /// Finds four points that are not coplanar, to seed the hull.
        /// </summary>
        private static bool FindInitialTetrahedron(IReadOnlyList<NifVector3> points, out int[] seed)
        {
            seed = [];

            // Two points that are actually distinct.
            int a = 0;
            int b = -1;

            for (int i = 1; i < points.Count; i++)
            {
                if (DistanceSquared(points[a], points[i]) > 1e-12f)
                {
                    b = i;
                    break;
                }
            }

            if (b < 0)
                return false;

            // A third that is off that line.
            int c = -1;
            float best = 1e-12f;

            for (int i = 0; i < points.Count; i++)
            {
                if (i == a || i == b)
                    continue;

                float area = TriangleAreaSquared(points[a], points[b], points[i]);

                if (area > best)
                {
                    best = area;
                    c = i;
                }
            }

            if (c < 0)
                return false;

            // A fourth off that plane.
            int d = -1;
            best = 1e-9f;

            for (int i = 0; i < points.Count; i++)
            {
                if (i == a || i == b || i == c)
                    continue;

                float volume = MathF.Abs(SignedDistance(points, (a, b, c), points[i]));

                if (volume > best)
                {
                    best = volume;
                    d = i;
                }
            }

            if (d < 0)
                return false;

            seed = [a, b, c, d];
            return true;
        }

        /// <summary>How far a point lies on the outward side of a face's plane.</summary>
        private static float SignedDistance(IReadOnlyList<NifVector3> points, (int A, int B, int C) face, NifVector3 point)
        {
            NifVector3 a = points[face.A], b = points[face.B], c = points[face.C];

            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;

            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

            // A face with no area has no normal, so there is no answer -- not "zero",
            // which would read as "the point is exactly on it". The difference is the
            // whole of the bug below: a sliver that reads as zero is visible from
            // nowhere, so nothing ever removes it.
            if (length < 1e-12f)
                return float.NaN;

            return ((point.X - a.X) * nx + (point.Y - a.Y) * ny + (point.Z - a.Z) * nz) / length;
        }

        private static NifVector3 Average(IReadOnlyList<NifVector3> points)
        {
            float x = 0, y = 0, z = 0;

            foreach (NifVector3 p in points)
            {
                x += p.X;
                y += p.Y;
                z += p.Z;
            }

            return new NifVector3(x / points.Count, y / points.Count, z / points.Count);
        }

        private static float DistanceSquared(NifVector3 a, NifVector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static float TriangleAreaSquared(NifVector3 a, NifVector3 b, NifVector3 c)
        {
            float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
            float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;

            return nx * nx + ny * ny + nz * nz;
        }

        /// <summary>Scales every vertex, for the metres-to-units conversion.</summary>
        public static void Scale(MeshGeometry mesh, float factor)
        {
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                NifVector3 v = mesh.Vertices[i];
                mesh.Vertices[i] = new NifVector3(v.X * factor, v.Y * factor, v.Z * factor);
            }
        }
    }
}
