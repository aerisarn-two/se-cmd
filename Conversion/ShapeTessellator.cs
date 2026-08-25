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
        /// How far outside a face a point must be, relative to the shape, before the
        /// hull counts it as outside at all.
        /// </summary>
        /// <remarks>
        /// Small, because a `bhkConvexVerticesShape`'s corners are *already* a hull —
        /// NifSkope produces them with Qhull and the game ships what Qhull returned —
        /// so taking the hull of them again has to give every one of them back. A
        /// generous tolerance quietly shaves the shallowest corners off: at a
        /// thousandth of the shape, one convex shape in seven lost at least one corner.
        ///
        /// NifSkope's own default is far coarser (a roundoff of 0.25 game units), but
        /// it is doing the opposite job — reducing a dense mesh to a hull an author
        /// wants small — and deliberately sheds corners to do it.
        /// </remarks>
        private const float HullScale = 1e-9f;

        /// <summary>
        /// How close, relative to the shape, two corners must be to be one corner.
        /// </summary>
        /// <remarks>
        /// Merging exact duplicates alone is *worse* than merging near ones: a pair a
        /// hair apart makes a face with no useful normal, and the surface built on it
        /// comes out unclosed. Merging too eagerly is worse still — at a hundred
        /// thousandth of the shape this collapsed thin plates onto themselves, and a
        /// door 8 corners thick came back with 4.
        /// </remarks>
        private const float MergeScale = 1e-8f;

        /// <summary>
        /// Havok works in metres, NIF in Skyrim units. This is the conversion
        /// FBXWrangler applies when emitting collision geometry.
        /// </summary>
        public const float BhkScaleFactor = 69.99125f;

        /// <summary>Its reciprocal, for the journey back.</summary>
        /// <remarks>
        /// FBXWrangler spells this as a literal `0.01428f`, which is *not* the
        /// reciprocal of the factor it multiplied by. The pair loses 5.2e-4 of every
        /// collision coordinate per round trip — on a mill pond fifty units across,
        /// nearly three hundredths of a unit — so no convex shape in the game came back
        /// where it started, however faithful the hull was. It was the larger half of
        /// the error, and the harder one to see, because it scales with the shape and
        /// so never looks like a bug in any particular one.
        ///
        /// **A deliberate departure from ck-cmd**, and one the knowledge base's §4.1
        /// already asks for: multiply going out, divide coming back. A shape authored
        /// in a DCC tool now lands 0.05% from where ck-cmd would put it, which is well
        /// under the tolerance anything here is measured to; a shape that came out of a
        /// NIF lands back on itself.
        /// </remarks>
        public const float BhkScaleFactorInverse = 1f / BhkScaleFactor;

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
        /// Quickhull: seed a tetrahedron from the extremes, give every face the points
        /// that lie outside it, then repeatedly take the point furthest outside any
        /// face and pull the surface out to meet it.
        ///
        /// Two things hold it together where the plain incremental version did not.
        ///
        /// The **horizon is walked, not deduced**. The old version decided visibility
        /// for each face independently and recovered the boundary by cancelling shared
        /// edges, which is the horizon only if the visible faces form one connected
        /// patch. Near-coplanar input makes neighbouring faces disagree about a point
        /// lying almost exactly in their shared plane; the visible set comes apart into
        /// islands, the surviving edges are several loops rather than one, and fanning
        /// across all of them leaves a surface with holes in it. Here the region is
        /// grown outward from a single face through adjacency, so it is connected by
        /// construction and its boundary is one loop whatever the arithmetic says.
        /// Roughly a fifth of the game's convex shapes came out unclosed before.
        ///
        /// The **furthest point goes in first**, which keeps the arithmetic away from
        /// the margin: the further outside a point is, the less any sign involving it
        /// is in doubt. Together with doing the determinants in double — the points
        /// arrive as float, so the subtractions are exact — that is what a set of
        /// adaptive predicates would otherwise have been needed for.
        ///
        /// Faces carry their neighbours rather than the topology being rebuilt from an
        /// edge list each round, which is also what turns the inner loop from a scan of
        /// the whole surface into a walk over the part of it involved.
        ///
        /// A *flat* hull is not broken input. The game ships them — `byohwrdoorload01`
        /// draws its load door as four coplanar points — and a hull with no volume has
        /// no tetrahedron to start from, so it is tessellated as the polygon it is,
        /// wound both ways so it exists from either side.
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

            if (points.Count < 4 || !FindInitialTetrahedron(points, out int[] seed))
                return PlanarHull(points);

            var hull = new Hull(points);
            hull.Build(seed);

            // Keep only the vertices the hull actually uses, renumbered.
            var remap = new Dictionary<int, ushort>();

            foreach (Hull.Face face in hull.Live)
                mesh.Triangles.Add(new NifTriangle(Index(face.A), Index(face.B), Index(face.C)));

            mesh.RecalculateNormals();
            return mesh;

            ushort Index(int original)
            {
                if (remap.TryGetValue(original, out ushort mapped))
                    return mapped;

                mapped = (ushort)mesh.Vertices.Count;
                remap[original] = mapped;
                mesh.Vertices.Add(points[original]);
                return mapped;
            }
        }

        /// <summary>
        /// A hull under construction: triangles that know their neighbours, and the
        /// points still waiting outside each of them.
        /// </summary>
        private sealed class Hull
        {
            /// <summary>
            /// A triangle, and across each edge the triangle on the far side.
            /// </summary>
            /// <remarks>
            /// <c>Beyond[i]</c> is whoever shares the edge leaving vertex <c>i</c>: so
            /// <c>Beyond[0]</c> lies across A→B, <c>Beyond[1]</c> across B→C and
            /// <c>Beyond[2]</c> across C→A. Every edge is shared by exactly two
            /// triangles at every moment, which is the invariant the rest rests on.
            /// </remarks>
            internal sealed class Face
            {
                internal int Id;
                internal int A, B, C;
                internal readonly int[] Beyond = [-1, -1, -1];
                internal double Nx, Ny, Nz, D;
                internal bool Dead;

                /// <summary>The points outside this face, and the furthest of them.</summary>
                internal readonly List<int> Outside = [];
                internal int Furthest = -1;
                internal double Reach;

                internal int Vertex(int i) => i == 0 ? A : i == 1 ? B : C;
            }

            private readonly IReadOnlyList<NifVector3> _points;
            private readonly List<Face> _faces = [];
            private readonly double _tolerance;

            internal Hull(IReadOnlyList<NifVector3> points)
            {
                _points = points;

                // How far outside the surface a point must be to count as outside at
                // all. Relative to the shape, because a Havok shape may be a centimetre
                // across or ten metres, and an absolute tolerance is either meaningless
                // on one or ruinous on the other.
                _tolerance = Tolerance(points);
            }

            internal IEnumerable<Face> Live => _faces.Where(f => !f.Dead);

            internal void Build(int[] seed)
            {
                Seed(seed);

                while (Furthest() is { } face)
                {
                    int point = face.Furthest;

                    var visible = new List<Face>();
                    var horizon = new List<(Face Face, int Edge)>();

                    Spread(face, point, visible, horizon);
                    Replace(point, visible, horizon);
                }
            }

            /// <summary>Four faces of a tetrahedron, wound outward and linked up.</summary>
            private void Seed(int[] seed)
            {
                // The fourth point tells the first three which way round they go: it is
                // inside the hull, so it must be behind the face they form.
                if (Volume(seed[0], seed[1], seed[2], seed[3]) > 0)
                    (seed[1], seed[2]) = (seed[2], seed[1]);

                Add(seed[0], seed[1], seed[2]);
                Add(seed[0], seed[3], seed[1]);
                Add(seed[1], seed[3], seed[2]);
                Add(seed[2], seed[3], seed[0]);

                Link(_faces);

                var waiting = new List<int>();

                for (int i = 0; i < _points.Count; i++)
                {
                    if (i != seed[0] && i != seed[1] && i != seed[2] && i != seed[3])
                        waiting.Add(i);
                }

                Assign(waiting, _faces);
            }

            /// <summary>
            /// Grows the visible region outward from one face the point can see.
            /// </summary>
            /// <remarks>
            /// Only through adjacency, which is the whole point: a face the point can
            /// see but which does not touch the region is left where it is. Where the
            /// walk stops — a visible face whose neighbour is not visible — is the
            /// horizon, collected on the way rather than worked out afterwards.
            /// </remarks>
            private void Spread(
                Face from, int point, List<Face> visible, List<(Face, int)> horizon)
            {
                var stack = new Stack<Face>();

                from.Dead = true;
                visible.Add(from);
                stack.Push(from);

                while (stack.Count > 0)
                {
                    Face face = stack.Pop();

                    for (int edge = 0; edge < 3; edge++)
                    {
                        if (face.Beyond[edge] < 0)
                            continue;

                        Face neighbour = _faces[face.Beyond[edge]];

                        if (neighbour.Dead)
                            continue;

                        if (Height(neighbour, point) > _tolerance)
                        {
                            neighbour.Dead = true;
                            visible.Add(neighbour);
                            stack.Push(neighbour);
                        }
                        else
                        {
                            horizon.Add((face, edge));
                        }
                    }
                }
            }

            /// <summary>Fans the horizon out to the new point and rehouses the orphans.</summary>
            private void Replace(
                int point, List<Face> visible, List<(Face Face, int Edge)> horizon)
            {
                var made = new List<Face>(horizon.Count);

                foreach ((Face face, int edge) in horizon)
                {
                    // The horizon edge in the direction the dying face had it, so the
                    // triangle taking its place is wound the same way round.
                    Face fresh = Add(face.Vertex(edge), face.Vertex((edge + 1) % 3), point);
                    Face keeping = _faces[face.Beyond[edge]];

                    fresh.Beyond[0] = keeping.Id;

                    for (int back = 0; back < 3; back++)
                    {
                        if (keeping.Beyond[back] == face.Id)
                            keeping.Beyond[back] = fresh.Id;
                    }

                    made.Add(fresh);
                }

                // The two edges meeting at the new point are shared with the
                // neighbouring new faces; the horizon edge is already joined.
                Link(made);

                // A point outside a face that has gone is outside one of the faces
                // replacing it, or it is inside the hull now and stops being considered.
                var orphans = new List<int>();

                foreach (Face face in visible)
                {
                    foreach (int p in face.Outside)
                    {
                        if (p != point)
                            orphans.Add(p);
                    }
                }

                Assign(orphans, made);
            }

            /// <summary>Gives each point to a face it lies outside, if there is one.</summary>
            /// <remarks>
            /// One face is enough. A point is outside the hull if it is outside any
            /// face, and recording it against every face it can see would only mean
            /// finding it again later.
            /// </remarks>
            private void Assign(List<int> waiting, IReadOnlyList<Face> among)
            {
                foreach (int p in waiting)
                {
                    foreach (Face face in among)
                    {
                        if (face.Dead)
                            continue;

                        double height = Height(face, p);

                        if (height <= _tolerance)
                            continue;

                        face.Outside.Add(p);

                        if (height > face.Reach)
                        {
                            face.Reach = height;
                            face.Furthest = p;
                        }

                        break;
                    }
                }
            }

            /// <summary>The point furthest outside the surface, and the face holding it.</summary>
            private Face? Furthest()
            {
                Face? best = null;

                foreach (Face face in _faces)
                {
                    if (!face.Dead && face.Furthest >= 0 && (best is null || face.Reach > best.Reach))
                        best = face;
                }

                return best;
            }

            private Face Add(int a, int b, int c)
            {
                var face = new Face { Id = _faces.Count, A = a, B = b, C = c };

                NifVector3 p = _points[a], q = _points[b], r = _points[c];

                double ux = (double)q.X - p.X, uy = (double)q.Y - p.Y, uz = (double)q.Z - p.Z;
                double vx = (double)r.X - p.X, vy = (double)r.Y - p.Y, vz = (double)r.Z - p.Z;

                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;

                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);

                if (length > 0)
                {
                    nx /= length;
                    ny /= length;
                    nz /= length;
                }

                face.Nx = nx;
                face.Ny = ny;
                face.Nz = nz;
                face.D = -(nx * p.X + ny * p.Y + nz * p.Z);

                _faces.Add(face);

                return face;
            }

            /// <summary>How far a point reaches past a face's plane.</summary>
            private double Height(Face face, int point)
            {
                NifVector3 p = _points[point];

                return face.Nx * p.X + face.Ny * p.Y + face.Nz * p.Z + face.D;
            }

            /// <summary>Six times the signed volume of the tetrahedron on four points.</summary>
            private double Volume(int a, int b, int c, int d)
            {
                NifVector3 p = _points[a], q = _points[b], r = _points[c], s = _points[d];

                double ux = (double)q.X - p.X, uy = (double)q.Y - p.Y, uz = (double)q.Z - p.Z;
                double vx = (double)r.X - p.X, vy = (double)r.Y - p.Y, vz = (double)r.Z - p.Z;
                double wx = (double)s.X - p.X, wy = (double)s.Y - p.Y, wz = (double)s.Z - p.Z;

                return wx * (uy * vz - uz * vy)
                     + wy * (uz * vx - ux * vz)
                     + wz * (ux * vy - uy * vx);
            }

            /// <summary>
            /// Joins faces along the edges they share.
            /// </summary>
            /// <remarks>
            /// An edge belongs to exactly two triangles, and they traverse it in
            /// opposite directions — so a directed edge names one of the pair and its
            /// reverse names the other.
            /// </remarks>
            private void Link(IReadOnlyList<Face> among)
            {
                var seen = new Dictionary<(int, int), (Face Face, int Edge)>();

                foreach (Face face in among)
                {
                    if (face.Dead)
                        continue;

                    for (int edge = 0; edge < 3; edge++)
                    {
                        int from = face.Vertex(edge);
                        int to = face.Vertex((edge + 1) % 3);

                        if (seen.Remove((to, from), out (Face Face, int Edge) other))
                        {
                            face.Beyond[edge] = other.Face.Id;
                            other.Face.Beyond[other.Edge] = face.Id;
                        }
                        else
                        {
                            seen[(from, to)] = (face, edge);
                        }
                    }
                }
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

            return MathF.Max(extent * HullScale, 1e-9f);
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
            float tolerance = MathF.Max(extent * MergeScale, 1e-9f);

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
        /// Four points spanning a volume, or none when the set is flat.
        /// </summary>
        /// <remarks>
        /// Taken as far apart as the set allows — the two furthest from each other,
        /// then the one furthest off their line, then the one furthest off their
        /// plane. The seed's shape is what every later comparison is measured against,
        /// and a sliver makes every point look as though it is on the surface already.
        ///
        /// Every threshold here is relative to the shape, where they were absolute — a
        /// point had to be 1e-9 off the plane of the other three to count. An absolute
        /// figure means nothing against a shape whose size it does not know: it is
        /// below the hull's own tolerance on anything large, which lets a tetrahedron
        /// seed flatter than the surface it is seeding, and no point is ever outside
        /// such a thing. `dwecog01`, a disc 0.6 across and 2.4e-7 thick, came back as 4
        /// of its 48 corners that way.
        ///
        /// Most of that particular repair was the hull tolerance rather than this — see
        /// <see cref="HullScale"/> — and measured against the corpus this is worth a
        /// third of a percent of shapes on its own. It is here because a threshold that
        /// cannot see the shape it is judging is wrong whether or not it is currently
        /// costing anything.
        /// </remarks>
        private static bool FindInitialTetrahedron(IReadOnlyList<NifVector3> points, out int[] seed)
        {
            seed = [];

            float tolerance = Tolerance(points);

            // The two furthest apart, near enough: whatever is furthest from an
            // arbitrary point, then whatever is furthest from that.
            int a = FurthestFrom(points, 0);
            int b = FurthestFrom(points, a);

            float span = DistanceSquared(points[a], points[b]);

            if (span <= tolerance * tolerance)
                return false;

            span = MathF.Sqrt(span);

            // The third, furthest off the line through the first two. The cross
            // product's length over the base is that distance.
            int c = -1;
            float best = tolerance;

            for (int i = 0; i < points.Count; i++)
            {
                if (i == a || i == b)
                    continue;

                float height = MathF.Sqrt(TriangleAreaSquared(points[a], points[b], points[i])) / span;

                if (height > best)
                {
                    best = height;
                    c = i;
                }
            }

            if (c < 0)
                return false;

            // The fourth, furthest off the plane of the other three.
            int d = -1;
            best = tolerance;

            for (int i = 0; i < points.Count; i++)
            {
                if (i == a || i == b || i == c)
                    continue;

                float height = MathF.Abs(SignedDistance(points, (a, b, c), points[i]));

                if (height > best)
                {
                    best = height;
                    d = i;
                }
            }

            if (d < 0)
                return false;

            seed = [a, b, c, d];
            return true;
        }

        /// <summary>The index of the point furthest from the one given.</summary>
        private static int FurthestFrom(IReadOnlyList<NifVector3> points, int from)
        {
            int best = from;
            float far = -1f;

            for (int i = 0; i < points.Count; i++)
            {
                float distance = DistanceSquared(points[from], points[i]);

                if (distance > far)
                {
                    far = distance;
                    best = i;
                }
            }

            return best;
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
