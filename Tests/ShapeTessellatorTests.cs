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
        public void TheHavokScaleFactorsAreReciprocals()
        {
            // Havok works in metres and the rest of a NIF in Skyrim units, so collision
            // geometry is scaled on the way out and back. ck-cmd writes the return trip
            // as a literal 0.01428, which is not the reciprocal of the 69.99125 it
            // multiplied by: the pair keeps only 99.9475% of every coordinate.
            //
            // That is invisible in any one shape and fatal across the corpus -- it
            // moved every corner of every convex shape in the game, by three hundredths
            // of a unit on the largest, so nothing round-tripped unchanged however
            // faithful the hull was. It is the one number here that scales with the
            // shape, which is why no fixture caught it.
            float there = ShapeTessellator.BhkScaleFactor;
            float back = ShapeTessellator.BhkScaleFactorInverse;

            Assert.Equal(1.0, there * (double)back, 6);
        }

        [Fact]
        public void AShapeIsTheSameShapeWhereverItStands()
        {
            // Merging near-duplicate corners buckets them into cells of the shape's
            // own scale, and the cell index used to be measured from the *origin*. A
            // float that does not fit an int saturates at int.MaxValue instead of
            // throwing, so every corner past int.MaxValue x tolerance shared one cell
            // and merged into a single point.
            //
            // With the tolerance a hundred-millionth of the extent, that ceiling is
            // about twenty-one times the shape's own size -- so a one-metre box
            // twenty-two metres from the body origin came back as a flat quad, and a
            // collision solid that is a plane does not collide. The corpus never saw
            // it because a hull's corners are stored close to the body they hang from.
            //
            // Measured from the shape's own corner it cannot happen, and the merge is
            // independent of where the shape stands -- which it always should have been.
            foreach (float distance in new[] { 0f, 20f, 22f, 100f, 5000f })
            {
                var points = new List<NifVector3>();

                foreach (float x in new[] { 0f, 1f })
                foreach (float y in new[] { 0f, 1f })
                foreach (float z in new[] { 0f, 1f })
                    points.Add(new NifVector3(distance + x, y, z));

                MeshGeometry hull = ShapeTessellator.ConvexHull(points);

                Assert.True(
                    hull.Vertices.Count == 8,
                    $"a unit box {distance} from the origin kept {hull.Vertices.Count} of its 8 corners");

                Assert.Equal(12, hull.Triangles.Count);
                AssertClosed(hull);
            }
        }

        [Fact]
        public void ADenseHullDoesNotTakeQuadraticTime()
        {
            // Every round used to pick its next point by scanning every face ever
            // created, dead ones included, which is quadratic in the input. A vanilla
            // collision hull is a couple of hundred corners and never noticed, but
            // ShapeFitter.FitConvex is handed every vertex of whatever mesh an author
            // drew: twenty thousand points on a sphere took seven seconds, all of it
            // in that scan.
            var random = new Random(4242);
            var points = new List<NifVector3>();

            for (int i = 0; i < 20000; i++)
            {
                double theta = random.NextDouble() * Math.Tau;
                double phi = Math.Acos(2 * random.NextDouble() - 1);

                // On a sphere, so the hull keeps every one of them and the work is real.
                points.Add(new NifVector3(
                    (float)(Math.Sin(phi) * Math.Cos(theta)),
                    (float)(Math.Sin(phi) * Math.Sin(theta)),
                    (float)Math.Cos(phi)));
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            MeshGeometry hull = ShapeTessellator.ConvexHull(points);
            watch.Stop();

            Assert.NotEmpty(hull.Triangles);

            // A wall clock in a suite that runs its classes in parallel is a blunt
            // instrument: this same input measures 0.8s alone and 4s under the full
            // suite. So the budget is nowhere near either figure -- it is here to catch
            // a return to quadratic, where the scan version took seven seconds alone
            // and correspondingly worse beside everything else, not to police a number.
            // If this ever fails, look at the complexity rather than the threshold.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        }

        [Fact]
        public void TheHullOfAHullIsThatHull()
        {
            // The corners a `bhkConvexVerticesShape` stores are already a hull -- Qhull
            // produced them when the shape was authored -- so hulling them again must
            // return all of them. This is the property the corpus is measured against,
            // and the one that says a shape survives a round trip.
            var random = new Random(86420);
            var points = new List<NifVector3>();

            for (int i = 0; i < 120; i++)
            {
                points.Add(new NifVector3(
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1),
                    (float)(random.NextDouble() * 2 - 1)));
            }

            MeshGeometry once = ShapeTessellator.ConvexHull(points);
            MeshGeometry twice = ShapeTessellator.ConvexHull(once.Vertices);

            AssertClosed(once);
            AssertClosed(twice);

            Assert.Equal(once.Vertices.Count, twice.Vertices.Count);

            foreach (NifVector3 v in once.Vertices)
            {
                Assert.Contains(twice.Vertices, u =>
                    MathF.Abs(u.X - v.X) < 1e-6f
                    && MathF.Abs(u.Y - v.Y) < 1e-6f
                    && MathF.Abs(u.Z - v.Z) < 1e-6f);
            }
        }

        [Fact]
        public void AWideThinShapeIsFlatRatherThanASliver()
        {
            // `dwecog01` is a cog: six tenths of a unit across and 2.4e-7 thick -- so
            // thin that the hull's tolerance, then a hundred-thousandth of the shape,
            // was twenty times its thickness. The seed tetrahedron came out flatter
            // than the surface it was seeding, nothing was ever outside it, and the
            // loop stopped at once: the cog's 48 corners came back as the seed's 4.
            //
            // Five hundred of the game's convex shapes were losing corners this way.
            // What fixed it was the tolerance; the seed thresholds became relative at
            // the same time and are worth a further third of a percent.
            var points = new List<NifVector3>();

            for (int i = 0; i < 24; i++)
            {
                double angle = i / 24.0 * Math.Tau;

                // Two rings a quarter of a micron apart, which is the cog.
                points.Add(new NifVector3((float)(Math.Cos(angle) * 0.3), (float)(Math.Sin(angle) * 0.3), 0.00126061f));
                points.Add(new NifVector3((float)(Math.Cos(angle) * 0.3), (float)(Math.Sin(angle) * 0.3), 0.00126085f));
            }

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            Assert.NotEmpty(hull.Triangles);

            // Every corner of the ring, not four of them. The two rings merge into one
            // -- they are a quarter of a micron apart on a shape a third of a unit
            // across -- so what must come back is the ring, not the pair.
            Assert.True(hull.Vertices.Count >= 24,
                $"a 48-corner disc came back with {hull.Vertices.Count} corners");
        }

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
        public void AFlatHullTooBigToIndexIsRefusedRatherThanWrapped()
        {
            // A NIF triangle names its corners with a ushort, and the fan casts to one.
            // The volume case checks this before renumbering; the flat case returns
            // before that check is reached, so it wrapped in silence and every corner
            // past the 65,535th was named by the wrong index.
            //
            // Flat is the easier case to reach it with, not the harder one: every point
            // of a polygon is a corner of it, where a solid hull keeps only its surface.
            // And nothing downstream could see the result — a wrapped index is still in
            // range, so `IsWellFormed` reports a healthy mesh.
            var points = new List<NifVector3>(70000);

            for (int i = 0; i < 70000; i++)
            {
                double angle = i / 70000.0 * Math.Tau;

                // All at z = 0, so there is no tetrahedron and this takes the flat path.
                points.Add(new NifVector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0f));
            }

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            // Nothing, rather than a mesh whose triangles name the wrong corners.
            Assert.Empty(hull.Triangles);

            foreach (NifTriangle t in hull.Triangles)
            {
                Assert.True(t.V1 < hull.Vertices.Count);
                Assert.True(t.V2 < hull.Vertices.Count);
                Assert.True(t.V3 < hull.Vertices.Count);
            }
        }

        [Fact]
        public void AFlatHullJustInsideTheLimitIsStillTessellated()
        {
            // And the guard does not swallow anything it should keep: the largest flat
            // hull a NIF can actually index still comes back.
            var points = new List<NifVector3>(1000);

            for (int i = 0; i < 1000; i++)
            {
                double angle = i / 1000.0 * Math.Tau;
                points.Add(new NifVector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0f));
            }

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            Assert.NotEmpty(hull.Triangles);
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
        public void AHullNeverGrowsBeyondWhatAHullCanBe()
        {
            // A hull of n points has at most 2n - 4 faces. snowdriftm01int has one
            // collision shape, a 222-point hull, and it produced 309,394 faces -- the
            // surface stopped being closed and every further point stitched more onto
            // it. A sweep of the corpus ran for three and a half hours on that one
            // mesh before anyone noticed it was not going to finish.
            //
            // The input that does it is quantised: the game's collision points are
            // stored coarsely, so corners that were distinct in the authoring tool
            // arrive equal or nearly so. This reproduces that shape of input.
            var random = new Random(20260821);
            var points = new List<NifVector3>();

            for (int i = 0; i < 400; i++)
            {
                double theta = random.NextDouble() * Math.Tau;
                double phi = Math.Acos(2 * random.NextDouble() - 1);

                // Snapped to a coarse grid, which is what makes near-duplicates and
                // slivers rather than a clean cloud.
                points.Add(new NifVector3(
                    Snap((float)(Math.Sin(phi) * Math.Cos(theta))),
                    Snap((float)(Math.Sin(phi) * Math.Sin(theta))),
                    Snap((float)Math.Cos(phi))));
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            // Seconds, not hours. The number is loose on purpose: what is being
            // asserted is that it terminates at all.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");

            Assert.NotEmpty(hull.Triangles);
            Assert.True(
                hull.Triangles.Count <= 2 * points.Count,
                $"{hull.Triangles.Count} faces for {points.Count} points");

            static float Snap(float value) => MathF.Round(value * 64f) / 64f;
        }

        [Fact]
        public void ANearDuplicatePointIsOnePoint()
        {
            // Two points the shape's own scale cannot tell apart make a face with no
            // area, and a face with no area has no normal -- so no point can see it,
            // and nothing ever removes it. That is how the surface opens up.
            MeshGeometry hull = ShapeTessellator.ConvexHull(
            [
                new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
                new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
                new(1, 1, 1.0000001f), new(1, 1, 1.0000002f)
            ]);

            Assert.Equal(8, hull.Vertices.Count);
            Assert.Equal(12, hull.Triangles.Count);
            AssertClosed(hull);
        }

        [Fact]
        public void ASliverIsStillAShape()
        {
            // A daedric mace's haft collision is eight points describing something
            // half a metre long and two microns thick. Merging near-duplicates leaves
            // two of them, which is not enough for a surface -- but the sliver is
            // still the collision the game ships, so the unmerged points are what the
            // flat case works from, and the shape comes back rather than the body
            // losing it.
            const float Length = 0.245f;
            const float Hair = 0.000001f;

            var points = new List<NifVector3>();

            foreach (float y in new[] { -Length, Length })
            {
                points.Add(new NifVector3(0f, y, 0f));
                points.Add(new NifVector3(Hair, y, 0f));
                points.Add(new NifVector3(0f, y, Hair));
                points.Add(new NifVector3(Hair, y, Hair));
            }

            MeshGeometry hull = ShapeTessellator.ConvexHull(points);

            Assert.NotEmpty(hull.Triangles);
            Assert.NotEmpty(hull.Vertices);

            // It still spans the length it had: the sliver is thin, not short.
            Assert.Equal(
                2 * Length,
                hull.Vertices.Max(v => v.Y) - hull.Vertices.Min(v => v.Y),
                3);
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
