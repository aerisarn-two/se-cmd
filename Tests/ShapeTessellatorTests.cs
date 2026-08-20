using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class ShapeTessellatorTests
    {
        /// <summary>
        /// A closed surface has every edge shared by exactly two triangles. This is
        /// the property that actually matters: an unclosed collision hull leaks.
        /// </summary>
        private static void AssertClosed(MeshGeometry mesh)
        {
            var edges = new Dictionary<(int, int), int>();

            foreach (NifTriangle t in mesh.Triangles)
            {
                foreach ((int a, int b) in new[] { (t.V1, t.V2), (t.V2, t.V3), (t.V3, t.V1) })
                {
                    var key = a < b ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }

            var unshared = edges.Where(e => e.Value != 2).ToList();

            Assert.True(unshared.Count == 0,
                $"{unshared.Count} edges are not shared by exactly two triangles");
        }

        private static float MaxRadius(MeshGeometry mesh) =>
            mesh.Vertices.Max(v => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z));

        // --- box --------------------------------------------------------------

        [Fact]
        public void BoxHasEightCornersAndTwelveTriangles()
        {
            MeshGeometry box = ShapeTessellator.Box(new NifVector3(1f, 2f, 3f));

            Assert.Equal(8, box.Vertices.Count);
            Assert.Equal(12, box.Triangles.Count);
            AssertClosed(box);
        }

        [Fact]
        public void BoxSpansItsHalfExtents()
        {
            MeshGeometry box = ShapeTessellator.Box(new NifVector3(1f, 2f, 3f));

            Assert.Equal(1f, box.Vertices.Max(v => v.X), 4);
            Assert.Equal(-1f, box.Vertices.Min(v => v.X), 4);
            Assert.Equal(2f, box.Vertices.Max(v => v.Y), 4);
            Assert.Equal(3f, box.Vertices.Max(v => v.Z), 4);
        }

        [Fact]
        public void BoxNormalsPointOutwards()
        {
            MeshGeometry box = ShapeTessellator.Box(new NifVector3(1f, 1f, 1f));

            // For a box centred on the origin, an outward normal agrees with the
            // direction from the centre to its vertex.
            for (int i = 0; i < box.Vertices.Count; i++)
            {
                NifVector3 v = box.Vertices[i];
                NifVector3 n = box.Normals[i];

                Assert.True(v.X * n.X + v.Y * n.Y + v.Z * n.Z > 0,
                    $"normal at vertex {i} points inwards");
            }
        }

        // --- sphere -----------------------------------------------------------

        [Fact]
        public void SphereIsClosedAndOnRadius()
        {
            MeshGeometry sphere = ShapeTessellator.Sphere(2.5f);

            AssertClosed(sphere);
            Assert.All(sphere.Vertices, v =>
                Assert.Equal(2.5f, MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z), 3));
        }

        [Fact]
        public void SphereDensityFollowsItsParameters()
        {
            MeshGeometry coarse = ShapeTessellator.Sphere(1f, segments: 4, rings: 2);
            MeshGeometry fine = ShapeTessellator.Sphere(1f, segments: 32, rings: 16);

            Assert.True(fine.Triangles.Count > coarse.Triangles.Count);
            AssertClosed(coarse);
            AssertClosed(fine);
        }

        // --- capsule ----------------------------------------------------------

        [Fact]
        public void CapsuleSpansBetweenItsPoints()
        {
            var first = new NifVector3(0f, 0f, 0f);
            var second = new NifVector3(0f, 0f, 10f);

            MeshGeometry capsule = ShapeTessellator.Capsule(first, second, 1f);

            Assert.NotEmpty(capsule.Triangles);

            // Caps extend a radius past each end.
            Assert.Equal(-1f, capsule.Vertices.Min(v => v.Z), 3);
            Assert.Equal(11f, capsule.Vertices.Max(v => v.Z), 3);
        }

        [Fact]
        public void CapsuleFollowsAnArbitraryAxis()
        {
            // Along +X rather than the +Z it is built on, so the rotation is used.
            MeshGeometry capsule = ShapeTessellator.Capsule(
                new NifVector3(0f, 0f, 0f), new NifVector3(10f, 0f, 0f), 1f);

            Assert.Equal(11f, capsule.Vertices.Max(v => v.X), 3);
            Assert.Equal(-1f, capsule.Vertices.Min(v => v.X), 3);

            // Nothing should stray far off the axis.
            Assert.All(capsule.Vertices, v => Assert.True(MathF.Abs(v.Y) <= 1.001f));
        }

        [Fact]
        public void ADegenerateCapsuleBecomesASphere()
        {
            MeshGeometry capsule = ShapeTessellator.Capsule(
                new NifVector3(1f, 1f, 1f), new NifVector3(1f, 1f, 1f), 2f);

            AssertClosed(capsule);
            Assert.Equal(2f, MaxRadius(capsule), 3);
        }

        // --- convex hull ------------------------------------------------------

        [Fact]
        public void HullOfACubesCornersIsThatCube()
        {
            NifVector3[] corners =
            [
                new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
                new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)
            ];

            MeshGeometry hull = ShapeTessellator.ConvexHull(corners);

            Assert.Equal(8, hull.Vertices.Count);
            Assert.Equal(12, hull.Triangles.Count);
            AssertClosed(hull);
        }

        [Fact]
        public void HullIgnoresInteriorPoints()
        {
            var points = new List<NifVector3>
            {
                new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
                new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
                // Well inside, so they must not appear on the hull.
                new(0, 0, 0), new(0.1f, -0.2f, 0.3f)
            };

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            Assert.Equal(8, hull.Vertices.Count);
            AssertClosed(hull);
        }

        [Fact]
        public void HullContainsEveryInputPoint()
        {
            var random = new Random(1234);
            var points = new List<NifVector3>();

            for (int i = 0; i < 60; i++)
            {
                points.Add(new NifVector3(
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1)));
            }

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            AssertClosed(hull);

            // Every original point must lie on or inside every hull face.
            foreach (NifTriangle t in hull.Triangles)
            {
                NifVector3 a = hull.Vertices[t.V1], b = hull.Vertices[t.V2], c = hull.Vertices[t.V3];

                float ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                float vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);

                if (length < 1e-9f)
                    continue;

                foreach (NifVector3 p in points)
                {
                    float d = ((p.X - a.X) * nx + (p.Y - a.Y) * ny + (p.Z - a.Z) * nz) / length;
                    Assert.True(d < 1e-3f, $"point lies {d:G4} outside a hull face");
                }
            }
        }

        [Fact]
        public void AFlatHullIsTessellatedAsThePolygonItIs()
        {
            // The game ships these: byohwrdoorload01 draws its load door as four
            // coplanar points. A hull with no volume has no tetrahedron to start from,
            // and yielding nothing lost the shape, its body and the collision object
            // above it. Wound both ways, so it exists from either side.
            MeshGeometry quad = ShapeTessellator.ConvexHull(
                [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)]);

            Assert.Equal(4, quad.Triangles.Count);
            Assert.Equal(4, quad.Vertices.Count);

            // Every corner is still a corner, and every triangle lies in the plane.
            Assert.All(quad.Vertices, v => Assert.Equal(0f, v.Z, 5));

            MeshGeometry triangle = ShapeTessellator.ConvexHull(
                [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);

            Assert.Equal(2, triangle.Triangles.Count);
        }

        [Fact]
        public void TooFewPointsAreStillNothing()
        {
            // Two points span no polygon, however it is wound.
            Assert.Empty(ShapeTessellator.ConvexHull([new(0, 0, 0), new(1, 0, 0)]).Triangles);
            Assert.Empty(ShapeTessellator.ConvexHull([]).Triangles);

            // Nor do three points that are all the same point.
            Assert.Empty(ShapeTessellator.ConvexHull(
                [new(2, 2, 2), new(2, 2, 2), new(2, 2, 2)]).Triangles);
        }

        // --- scaling ----------------------------------------------------------

        [Fact]
        public void ScalingConvertsHavokMetresToSkyrimUnits()
        {
            MeshGeometry box = ShapeTessellator.Box(new NifVector3(1f, 1f, 1f));
            ShapeTessellator.Scale(box, ShapeTessellator.BhkScaleFactor);

            Assert.Equal(ShapeTessellator.BhkScaleFactor, box.Vertices.Max(v => v.X), 3);
        }
    }
}
