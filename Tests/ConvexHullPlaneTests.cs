using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The plane equations a <c>bhkConvexVerticesShape</c> stores.
    /// </summary>
    /// <remarks>
    /// nif.xml states the convention exactly: the normal points to the exterior, and
    /// the fourth component is *minus* the dot product of the normal with any vertex on
    /// the plane. Havok then tests containment with <c>n.x + d &lt;= 0</c>.
    ///
    /// Flipping that sign leaves every plane in the right place and inverts which side
    /// of it is solid, so the hull is inside out — an object that collides everywhere
    /// except where it is. A box hides the mistake completely, because negating every
    /// distance maps a symmetric plane set onto itself, so these use a shape that is
    /// not symmetric about any axis.
    /// </remarks>
    public class ConvexHullPlaneTests
    {
        /// <summary>A box from the origin to (4, 2, 1): no symmetry to hide behind.</summary>
        private static List<NifVector3> LopsidedBox()
        {
            var points = new List<NifVector3>();

            foreach (float x in new[] { 0f, 4f })
            foreach (float y in new[] { 0f, 2f })
            foreach (float z in new[] { 0f, 1f })
                points.Add(new NifVector3(x, y, z));

            return points;
        }

        [Fact]
        public void EveryVertexIsInsideEveryPlane()
        {
            // The containment test Havok runs. If the sign convention is inverted this
            // fails for every point at once, which is what "inside out" means.
            (List<NifVector4> vertices, List<NifVector4> planes) = ShapeFitter.FitConvex(LopsidedBox());

            Assert.NotEmpty(planes);

            foreach (NifVector4 plane in planes)
            {
                foreach (NifVector4 vertex in vertices)
                {
                    float side = plane.X * vertex.X + plane.Y * vertex.Y + plane.Z * vertex.Z + plane.W;

                    Assert.True(
                        side <= 1e-4f,
                        $"vertex ({vertex.X}, {vertex.Y}, {vertex.Z}) is outside plane "
                        + $"({plane.X}, {plane.Y}, {plane.Z}, {plane.W}) by {side}");
                }
            }
        }

        [Fact]
        public void APointWellOutsideIsOutsideSomePlane()
        {
            // The other half: a test that passes everything would also pass the one
            // above.
            (_, List<NifVector4> planes) = ShapeFitter.FitConvex(LopsidedBox());

            var outside = new NifVector3(10f, 10f, 10f);

            Assert.Contains(
                planes,
                p => p.X * outside.X + p.Y * outside.Y + p.Z * outside.Z + p.W > 0f);
        }

        [Fact]
        public void TheDistanceIsMinusTheDotProduct()
        {
            // Stated directly, since the containment tests above would also pass if
            // both the normals and the distances were negated together.
            (_, List<NifVector4> planes) = ShapeFitter.FitConvex(LopsidedBox());

            // The face at x = 4 has the outward normal (1, 0, 0) and so stores -4.
            NifVector4 face = Assert.Single(
                planes, p => MathF.Abs(p.X - 1f) < 1e-4f && MathF.Abs(p.Y) < 1e-4f && MathF.Abs(p.Z) < 1e-4f);

            Assert.Equal(-4f, face.W, 3);

            // And the face at x = 0 has normal (-1, 0, 0) and stores 0.
            NifVector4 opposite = Assert.Single(
                planes, p => MathF.Abs(p.X + 1f) < 1e-4f && MathF.Abs(p.Y) < 1e-4f && MathF.Abs(p.Z) < 1e-4f);

            Assert.Equal(0f, opposite.W, 3);
        }

        [Fact]
        public void ABoxWouldHaveHiddenIt()
        {
            // Why the fixtures did not catch this. Every plane of a shape centred on
            // the origin has a mirror image, so negating all the distances gives back
            // the same set and the shape stays right by accident.
            var box = new List<NifVector3>();

            foreach (float x in new[] { -1f, 1f })
            foreach (float y in new[] { -1f, 1f })
            foreach (float z in new[] { -1f, 1f })
                box.Add(new NifVector3(x, y, z));

            (_, List<NifVector4> planes) = ShapeFitter.FitConvex(box);

            foreach (NifVector4 plane in planes)
            {
                Assert.Contains(
                    planes,
                    other => MathF.Abs(other.X + plane.X) < 1e-4f
                             && MathF.Abs(other.Y + plane.Y) < 1e-4f
                             && MathF.Abs(other.Z + plane.Z) < 1e-4f
                             && MathF.Abs(other.W - plane.W) < 1e-4f);
            }
        }

        [Fact]
        public void TheConventionMatchesWhatTheGameShips()
        {
            // The definitive check: a vanilla hull, read straight out of a file
            // Bethesda's exporter wrote, has to satisfy the same containment test.
            var db = NifXmlDatabase.LoadEmbedded();
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "generate_rb.nif"), db);

            NifItem shape = model.Blocks.First(b => b.Name == "bhkConvexVerticesShape");

            var vertices = model.FindItem(shape, "Vertices")!.Children
                .Select(c => c.Value.Get<NifVector4>()).ToList();

            var planes = model.FindItem(shape, "Normals")!.Children
                .Select(c => c.Value.Get<NifVector4>()).ToList();

            Assert.NotEmpty(planes);

            foreach (NifVector4 plane in planes)
            {
                foreach (NifVector4 vertex in vertices)
                {
                    float side = plane.X * vertex.X + plane.Y * vertex.Y + plane.Z * vertex.Z + plane.W;

                    Assert.True(side <= 1e-4f, $"the shipped hull fails its own containment test by {side}");
                }
            }
        }
    }
}
