using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The inertia tensors computed for Havok shapes.
    /// </summary>
    /// <remarks>
    /// ck-cmd asks Havok for these. There is no Havok here, so they are computed
    /// directly, and the check that this is the same computation is that it reproduces
    /// the tensors in the files ck-cmd generated — given only the mass and the shape
    /// those files also hold.
    ///
    /// The tensor decides how a body resists being spun. A wrong one is a crate that
    /// tumbles like a pencil, and it is not visible in anything but motion.
    /// </remarks>
    public class HavokInertiaTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        private static (float Mass, NifItem Shape, float[] Stored) Body(NifModel model)
        {
            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));

            float mass = model.FindItem(body, @"Rigid Body Info\Mass")!.Value.ToFloat();

            var stored = new float[3];

            for (int i = 0; i < 3; i++)
            {
                stored[i] = model
                    .FindItem(body, $@"Rigid Body Info\Inertia Tensor\m{i + 1}{i + 1}")!.Value.ToFloat();
            }

            return (mass, model.GetRef(body, "Shape")!, stored);
        }

        [Fact]
        public void ABoxMatchesTheFileItCameFrom()
        {
            NifModel model = Load("generate_rb_box.nif");
            (float mass, NifItem shape, float[] stored) = Body(model);

            NifVector3 half = model.FindItem(shape, "Dimensions")!.Value.Get<NifVector3>();

            NifMatrix33 computed = HavokInertia.Box(mass, half);

            Assert.Equal(stored[0], computed.M11, 9);
            Assert.Equal(stored[1], computed.M22, 9);
            Assert.Equal(stored[2], computed.M33, 9);
        }

        [Fact]
        public void ASphereMatchesTheFileItCameFrom()
        {
            NifModel model = Load("generate_rb_sphere.nif");
            (float mass, NifItem shape, float[] stored) = Body(model);

            float radius = model.FindItem(shape, "Radius")!.Value.ToFloat();

            NifMatrix33 computed = HavokInertia.Sphere(mass, radius);

            Assert.Equal(stored[0], computed.M11, 9);
            Assert.Equal(stored[1], computed.M22, 9);
            Assert.Equal(stored[2], computed.M33, 9);
        }

        [Fact]
        public void AConvexHullMatchesTheFileItCameFrom()
        {
            // The hull here is a cube, so the face integration has to land on the same
            // answer the closed form gives for a box.
            NifModel model = Load("generate_rb.nif");
            (float mass, NifItem shape, float[] stored) = Body(model);

            var points = model.FindItem(shape, "Vertices")!.Children
                .Select(c => c.Value.Get<NifVector4>())
                .Select(v => new NifVector3(v.X, v.Y, v.Z))
                .ToList();

            NifMatrix33 computed = HavokInertia.Convex(mass, ShapeTessellator.ConvexHull(points));

            Assert.Equal(stored[0], computed.M11, 9);
            Assert.Equal(stored[1], computed.M22, 9);
            Assert.Equal(stored[2], computed.M33, 9);
        }

        [Fact]
        public void AConvexCubeAgreesWithTheClosedFormBox()
        {
            // The two routes to the same shape, so a change to either is caught by the
            // other rather than by a number written down here.
            var points = new List<NifVector3>();

            foreach (float x in new[] { -2f, 2f })
            foreach (float y in new[] { -3f, 3f })
            foreach (float z in new[] { -5f, 5f })
                points.Add(new NifVector3(x, y, z));

            NifMatrix33 hull = HavokInertia.Convex(7f, ShapeTessellator.ConvexHull(points));
            NifMatrix33 box = HavokInertia.Box(7f, new NifVector3(2f, 3f, 5f));

            Assert.Equal(box.M11, hull.M11, 3);
            Assert.Equal(box.M22, hull.M22, 3);
            Assert.Equal(box.M33, hull.M33, 3);

            // A box centred on the origin has no products of inertia.
            Assert.Equal(0f, hull.M12, 3);
            Assert.Equal(0f, hull.M13, 3);
            Assert.Equal(0f, hull.M23, 3);
        }

        [Fact]
        public void ALongCapsuleResistsSpinningAboutItsAxisLeast()
        {
            // A capsule along x: turning it end over end moves mass a long way from
            // the axis, spinning it about its own length barely moves anything.
            NifMatrix33 tensor = HavokInertia.Capsule(
                10f, new NifVector3(-4f, 0f, 0f), new NifVector3(4f, 0f, 0f), 0.5f);

            Assert.True(tensor.M11 < tensor.M22, $"{tensor.M11} should be under {tensor.M22}");
            Assert.Equal(tensor.M22, tensor.M33, 4);
        }

        [Fact]
        public void ADegenerateCapsuleIsASphere()
        {
            NifVector3 point = new(1f, 2f, 3f);

            NifMatrix33 capsule = HavokInertia.Capsule(4f, point, point, 0.75f);
            NifMatrix33 sphere = HavokInertia.Sphere(4f, 0.75f);

            Assert.Equal(sphere.M11, capsule.M11, 6);
        }

        [Fact]
        public void ZeroMassGivesAZeroTensor()
        {
            // Statics get a zero mass, and their tensor has to follow it rather than
            // being left at whatever the shape would otherwise imply.
            NifMatrix33 tensor = HavokInertia.Box(0f, new NifVector3(1f, 2f, 3f));

            Assert.Equal(0f, tensor.M11);
            Assert.Equal(0f, tensor.M22);
            Assert.Equal(0f, tensor.M33);
        }
    }
}
