using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Collision surviving a full NIF to FBX to NIF trip, which is the only way to
    /// tell that the tessellating and the fitting agree with each other.
    /// </summary>
    public class CollisionImportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static NifModel RoundTrip(string nif, out List<string> warnings)
        {
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(nif),
                LegendaryEdition = true
            });

            NifModel rebuilt = converter.Convert(Db);
            warnings = converter.Warnings;

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        public static TheoryData<string, string> CollisionFiles() => new()
        {
            { "generate_rb_box.nif", "bhkBoxShape" },
            { "generate_rb_sphere.nif", "bhkSphereShape" },
            { "generate_rb.nif", "bhkConvexVerticesShape" }
        };

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void RebuildsTheCollisionObject(string nif, string unusedShape)
        {
            NifModel model = RoundTrip(nif, out _);

            Assert.Contains(model.Blocks, b => b.Name == "bhkCollisionObject");
            Assert.Contains(model.Blocks, b => model.BlockInherits(b, "bhkRigidBody"));
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void ABodyAuthoredWithoutSettingsGetsBethesdaCommonestOnes(string nif, string unusedShape)
        {
            // A body modelled in a DCC tool carries no nif_rb_* properties at all, so
            // the scalars have to fall back to something. That something is Bethesda's
            // own commonest value, not nif.xml's default, because the two disagree:
            // vanilla damping sits on a 1/1024 grid (0.099609375, not 0.1) and a
            // static's penetration depth is 0.1 where nif.xml says 0.15.
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();
            var scene = new FbxScene(document);

            // Strip every carried setting, leaving the layer -- which is what decides
            // static from moving, and which a DCC body does carry by convention.
            foreach (FbxObject node in scene.Objects.Where(o => o.Class == "Model"))
            {
                foreach (FbxRigidBodyInfo.Scalar scalar in FbxRigidBodyInfo.Scalars)
                    node.Properties.Remove(scalar.Property);
            }

            var converter = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(nif),
                LegendaryEdition = true
            });

            NifModel rebuilt = converter.Convert(Db);

            List<NifItem> bodies =
                [.. rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "bhkRigidBody"))];

            Assert.NotEmpty(bodies);

            foreach (NifItem body in bodies)
            {
                bool isStatic = FbxRigidBodyInfo.IsStatic(FbxCollisionMaterial.LayerOf(rebuilt, body));

                foreach (FbxRigidBodyInfo.Scalar scalar in FbxRigidBodyInfo.Scalars)
                {
                    NifItem? item = rebuilt.FindItem(body, $@"Rigid Body Info\{scalar.Field}");

                    Assert.NotNull(item);
                    Assert.Equal(scalar.Default(isStatic), item!.Value.ToFloat());
                }
            }
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void RebuildsTheSameShapeKind(string nif, string expectedShape)
        {
            NifModel model = RoundTrip(nif, out _);

            // The suffix written on export is what picks the primitive on import, so
            // a box must come back a box rather than a hull of its corners.
            Assert.Contains(model.Blocks, b => b.Name == expectedShape);
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void ConvertsWithoutWarnings(string nif, string unusedShape)
        {
            RoundTrip(nif, out List<string> warnings);

            Assert.Empty(warnings);
        }

        [Fact]
        public void CollisionAttachesToTheNodeItCameFrom()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem? owner = model.Blocks.FirstOrDefault(b =>
                model.BlockInherits(b, "NiAVObject") && model.GetRef(b, "Collision Object") is not null);

            Assert.NotNull(owner);

            NifItem collision = model.GetRef(owner!, "Collision Object")!;

            // ...and points back at it.
            Assert.Equal(model.IndexOf(owner!), model.FindItem(collision, "Target")!.Value.ToLink());
        }

        [Fact]
        public void BoxKeepsItsDimensions()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            NifVector3 before = source.FindItem(
                source.Blocks.First(b => b.Name == "bhkBoxShape"), "Dimensions")!.Value.Get<NifVector3>();

            NifModel model = RoundTrip("generate_rb_box.nif", out _);
            NifVector3 after = model.FindItem(
                model.Blocks.First(b => b.Name == "bhkBoxShape"), "Dimensions")!.Value.Get<NifVector3>();

            // Tessellated to metres-scaled geometry and fitted back, so a couple of
            // decimal places is the honest tolerance.
            Assert.Equal(before.X, after.X, 3);
            Assert.Equal(before.Y, after.Y, 3);
            Assert.Equal(before.Z, after.Z, 3);
        }

        [Fact]
        public void SphereKeepsItsRadius()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_sphere.nif"), Db);
            float before = source.FindItem(
                source.Blocks.First(b => b.Name == "bhkSphereShape"), "Radius")!.Value.ToFloat();

            NifModel model = RoundTrip("generate_rb_sphere.nif", out _);
            float after = model.FindItem(
                model.Blocks.First(b => b.Name == "bhkSphereShape"), "Radius")!.Value.ToFloat();

            // A tessellated sphere's vertices sit exactly on the radius, so the
            // fitted sphere should land close.
            Assert.Equal(before, after, 2);
        }

        [Fact]
        public void ConvexKeepsItsHullAndGainsPlanes()
        {
            NifModel model = RoundTrip("generate_rb.nif", out _);

            NifItem shape = model.Blocks.First(b => b.Name == "bhkConvexVerticesShape");

            uint vertices = model.GetUInt(shape, "Num Vertices");
            uint normals = model.GetUInt(shape, "Num Normals");

            Assert.True(vertices >= 4, $"a hull needs at least four vertices, got {vertices}");

            // Havok needs the face planes too; it does not derive them.
            Assert.True(normals > 0, "convex shapes must carry their face planes");
        }

        [Fact]
        public void StaticBodiesGetZeroMass()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));

            // A static with a mass is treated as movable, which is how scenery ends
            // up falling through the world.
            Assert.Equal(0f, model.FindItem(body, @"Rigid Body Info\Mass")!.Value.ToFloat(), 5);
        }

        [Fact]
        public void StaticBodiesGetAZeroedInertiaTensor()
        {
            // ck-cmd zeroes the whole matrix alongside the mass, and its conversion
            // writes zero into the fourth column whatever the tensor held, so all
            // twelve components go. A static that keeps a tensor is a body Havok can
            // still be asked to spin.
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));

            for (int row = 1; row <= 3; row++)
            {
                for (int column = 1; column <= 4; column++)
                {
                    string field = $@"Rigid Body Info\Inertia Tensor\m{row}{column}";

                    Assert.Equal(0f, model.FindItem(body, field)!.Value.ToFloat(), 6);
                }
            }
        }

        [Fact]
        public void BodyTransformReturnsToHavokMetres()
        {
            NifModel source = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            NifItem sourceBody = source.Blocks.First(b => source.BlockInherits(b, "bhkRigidBody"));
            NifVector4 before = source.FindItem(sourceBody, @"Rigid Body Info\Translation")!.Value.Get<NifVector4>();

            NifModel model = RoundTrip("generate_rb_box.nif", out _);
            NifItem body = model.Blocks.First(b => model.BlockInherits(b, "bhkRigidBody"));
            NifVector4 after = model.FindItem(body, @"Rigid Body Info\Translation")!.Value.Get<NifVector4>();

            // Scaled out to units and back to metres; the two factors are not exact
            // reciprocals, so this is close rather than equal.
            Assert.Equal(before.X, after.X, 2);
            Assert.Equal(before.Y, after.Y, 2);
            Assert.Equal(before.Z, after.Z, 2);
        }

        [Fact]
        public void RenderGeometrySurvivesAlongsideCollision()
        {
            NifModel model = RoundTrip("generate_rb_box.nif", out _);

            // The collision must not have displaced the visible mesh.
            Assert.Contains(model.Blocks, b => b.Name == "NiTriShape");
            Assert.Contains(model.Blocks, b => b.Name == "bhkBoxShape");
        }
    }
}
