using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Ragdoll and limited-hinge constraints, against a real skeleton.
    /// </summary>
    /// <remarks>
    /// These are the two types ck-cmd implements and the spec's table describes, and
    /// until this fixture arrived neither reached the export path: se-cmd's own
    /// fixtures contain a stiff spring and a ball-and-socket chain, and neither of
    /// those has an orientation at all. The frame packing was therefore exercised
    /// only from the import side, against scenes built by hand.
    ///
    /// A cow skeleton from XPMSSE, 24 rigid bodies joined by 11 ragdolls and 12
    /// hinges. See `Tests/Resources/xpmsse/README.md` for where it came from.
    /// </remarks>
    public class RagdollConstraintTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load() =>
            NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "xpmsse", "skeleton_cow.nif"), Db);

        private static FbxDocument? _exported;

        private static FbxDocument Export() => _exported ??= new NifToFbx(Load()).Convert();

        private static IEnumerable<FbxObject> AttachPoints(FbxScene scene) =>
            scene.OfClass("Model").Where(
                o => o.Name.EndsWith(FbxConstraintWriter.NameSuffix, StringComparison.Ordinal));

        private static (NifModel Model, List<string> Warnings) RoundTrip()
        {
            var converter = new FbxToNif(
                new FbxScene(Export()),
                new FbxToNifOptions { RootName = "cow" });

            return (converter.Convert(Db), converter.Warnings);
        }

        private static IEnumerable<NifItem> ConstraintsOf(NifModel model, string type) =>
            model.Blocks.Where(b => b.Name == type);

        // --- the fixture -------------------------------------------------------

        [Fact]
        public void TheSkeletonHasBothConstraintTypes()
        {
            NifModel model = Load();

            Assert.Equal(11, ConstraintsOf(model, "bhkRagdollConstraint").Count());
            Assert.Equal(12, ConstraintsOf(model, "bhkLimitedHingeConstraint").Count());
            Assert.Equal(100u, model.BSVersion);
        }

        // --- exporting ---------------------------------------------------------

        [Fact]
        public void EveryConstraintBecomesAnAttachmentPoint()
        {
            var converter = new NifToFbx(Load());
            var scene = new FbxScene(converter.Convert());

            // Eleven ragdolls and twelve hinges, none of them reported as a
            // constraint whose bodies could not be found.
            Assert.Equal(23, AttachPoints(scene).Count());
            Assert.Empty(converter.Warnings);
        }

        [Theory]
        [InlineData("Ragdoll", 11)]
        [InlineData("LimitedHinge", 12)]
        public void EachTypeIsTaggedAsItself(string type, int count)
        {
            var scene = new FbxScene(Export());

            // ck-cmd collapses everything but a ragdoll to a limited hinge on the way
            // back in, which would make these two indistinguishable.
            Assert.Equal(
                count,
                AttachPoints(scene).Count(
                    o => o.Properties.GetString(FbxConstraintWriter.TypeProperty) == type));
        }

        [Fact]
        public void RagdollFramesAreWrittenAsColumns()
        {
            NifModel model = Load();

            NifItem constraint = ConstraintsOf(model, "bhkRagdollConstraint").First();
            NifItem descriptor = model.ConstraintDescriptor(constraint);

            NifVector4 twist = model.FindItem(descriptor, "Twist B")!.Value.Get<NifVector4>();
            NifVector4 plane = model.FindItem(descriptor, "Plane B")!.Value.Get<NifVector4>();
            NifVector4 motor = model.FindItem(descriptor, "Motor B")!.Value.Get<NifVector4>();

            FbxObject point = FindPoint(model, constraint);

            (double rx, double ry, double rz) = point.Properties.GetVector3("Lcl Rotation");
            NifMatrix33 written = NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz);

            // ck-cmd packs the axes as the matrix's columns and inverts the rotation
            // when reading it back, so a row-vector matrix like this one holds the
            // transpose of the joint frame (constraint spec §1.2, §3.2). This is the
            // first fixture with axes to check that against.
            AssertAxis(twist, written.M11, written.M21, written.M31);
            AssertAxis(plane, written.M12, written.M22, written.M32);
            AssertAxis(motor, written.M13, written.M23, written.M33);
        }

        [Fact]
        public void HingeFramesAreWrittenTheSameWay()
        {
            NifModel model = Load();

            NifItem constraint = ConstraintsOf(model, "bhkLimitedHingeConstraint").First();
            NifItem descriptor = model.ConstraintDescriptor(constraint);

            NifVector4 axle = model.FindItem(descriptor, "Axis B")!.Value.Get<NifVector4>();

            FbxObject point = FindPoint(model, constraint);

            (double rx, double ry, double rz) = point.Properties.GetVector3("Lcl Rotation");
            NifMatrix33 written = NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz);

            // The hinges name their axes differently — axle and two perpendiculars
            // rather than twist, plane and motor — and the packing is the same.
            AssertAxis(axle, written.M11, written.M21, written.M31);
        }

        private static void AssertAxis(NifVector4 expected, float x, float y, float z)
        {
            Assert.Equal(expected.X, x, 3);
            Assert.Equal(expected.Y, y, 3);
            Assert.Equal(expected.Z, z, 3);
        }

        /// <summary>The attachment point a given constraint produced.</summary>
        private static FbxObject FindPoint(NifModel model, NifItem constraint)
        {
            string owner = OwnerName(model, model.GetBlock(model.FindItem(constraint, "Entity A")!)!);

            return AttachPoints(new FbxScene(Export())).Single(
                o => o.Name.EndsWith(
                    $"{FbxConstraintWriter.NameSeparator}{owner}_rb{FbxConstraintWriter.NameSuffix}",
                    StringComparison.Ordinal));
        }

        /// <summary>The name of the node whose collision object holds a body.</summary>
        private static string OwnerName(NifModel model, NifItem body) =>
            model.GetName(model.Blocks.First(
                n => model.GetRef(n, "Collision Object") is { } collision
                    && model.GetRef(collision, "Body") == body));

        [Fact]
        public void LimitsAreCarried()
        {
            NifModel model = Load();

            NifItem constraint = ConstraintsOf(model, "bhkRagdollConstraint").First();
            FbxObject point = FindPoint(model, constraint);

            // The six ck-cmd writes by name, which se-cmd writes as part of walking
            // the whole descriptor (spec §1.3, §4.3).
            foreach (string field in new[]
                     {
                         "cone_max_angle", "plane_min_angle", "plane_max_angle",
                         "twist_min_angle", "twist_max_angle", "max_friction"
                     })
            {
                Assert.True(point.Properties.Contains($"{FbxConstraintWriter.FieldPrefix}{field}"), field);
            }
        }

        // --- round trip --------------------------------------------------------

        [Fact]
        public void EveryConstraintComesBackAsItself()
        {
            (NifModel model, List<string> warnings) = RoundTrip();

            Assert.Equal(11, ConstraintsOf(model, "bhkRagdollConstraint").Count());
            Assert.Equal(12, ConstraintsOf(model, "bhkLimitedHingeConstraint").Count());
            Assert.Empty(warnings);
        }

        [Fact]
        public void RagdollAxesAndLimitsSurvive()
        {
            NifModel before = Load();
            (NifModel after, _) = RoundTrip();

            NifItem source = before.ConstraintDescriptor(
                ConstraintsOf(before, "bhkRagdollConstraint").First());

            // Matched by the body they belong to, since block order is not preserved.
            string owner = OwnerName(
                before,
                before.GetBlock(before.FindItem(
                    ConstraintsOf(before, "bhkRagdollConstraint").First(), "Entity A")!)!);

            NifItem rebuilt = after.ConstraintDescriptor(
                ConstraintsOf(after, "bhkRagdollConstraint")
                    .Single(c => OwnerName(after, after.GetBlock(after.FindItem(c, "Entity A")!)!) == owner));

            foreach (string field in new[]
                     {
                         "Twist A", "Plane A", "Motor A", "Pivot A",
                         "Twist B", "Plane B", "Motor B", "Pivot B"
                     })
            {
                NifVector4 a = before.FindItem(source, field)!.Value.Get<NifVector4>();
                NifVector4 b = after.FindItem(rebuilt, field)!.Value.Get<NifVector4>();

                Assert.Equal(a.X, b.X, 5);
                Assert.Equal(a.Y, b.Y, 5);
                Assert.Equal(a.Z, b.Z, 5);
                Assert.Equal(a.W, b.W, 5);
            }

            foreach (string field in new[]
                     {
                         "Cone Max Angle", "Plane Min Angle", "Plane Max Angle",
                         "Twist Min Angle", "Twist Max Angle", "Max Friction"
                     })
            {
                Assert.Equal(
                    before.FindItem(source, field)!.Value.ToFloat(),
                    after.FindItem(rebuilt, field)!.Value.ToFloat(), 5);
            }
        }

        [Fact]
        public void ConstraintsAreListedByTheBodiesThatOwnThem()
        {
            (NifModel model, _) = RoundTrip();

            var listed = model.Blocks
                .SelectMany(b => model.GetRefArray(b, "Constraints"))
                .ToList();

            // Every one, and each by exactly one body: a constraint nothing lists is
            // a block in the file and a joint in nothing.
            Assert.Equal(23, listed.Count);
            Assert.Equal(23, listed.Distinct().Count());
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            (NifModel model, _) = RoundTrip();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Equal(11, ConstraintsOf(reloaded, "bhkRagdollConstraint").Count());
            Assert.Equal(12, ConstraintsOf(reloaded, "bhkLimitedHingeConstraint").Count());
        }
    }
}
