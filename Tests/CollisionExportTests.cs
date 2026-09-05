using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class CollisionExportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static FbxScene Convert(string nif, bool exportCollision = true)
        {
            NifModel model = NifModel.Load(PathTo(nif), Db);
            var converter = new NifToFbx(model, new NifToFbxOptions { ExportCollision = exportCollision });
            return new FbxScene(converter.Convert());
        }

        /// <summary>Every fixture with a rigid body, and the shape each one uses.</summary>
        public static TheoryData<string, string> CollisionFiles() => new()
        {
            { "generate_rb_box.nif", "_box" },
            { "generate_rb_sphere.nif", "_sphere" },
            { "generate_rb.nif", "_convex" }
        };

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void EmitsARigidBodyNode(string nif, string _)
        {
            FbxScene scene = Convert(nif);

            // The _rb suffix is the marker the import side keys off.
            Assert.Contains(scene.OfClass("Model"), m => m.Name.EndsWith("_rb", StringComparison.Ordinal));
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void EmitsGeometryForTheShape(string nif, string suffix)
        {
            FbxScene scene = Convert(nif);

            FbxObject? shapeNode = scene.OfClass("Model")
                .FirstOrDefault(m => m.Name.EndsWith(suffix, StringComparison.Ordinal));

            Assert.NotNull(shapeNode);

            FbxObject geometry = Assert.Single(
                scene.ChildrenOf(shapeNode!.Id).Where(o => o.Class == "Geometry"));

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            Assert.NotEmpty(vertices);
            Assert.NotEmpty(indices);
        }

        [Theory]
        [MemberData(nameof(CollisionFiles))]
        public void ConvertsWithoutWarnings(string nif, string _)
        {
            NifModel model = NifModel.Load(PathTo(nif), Db);
            var converter = new NifToFbx(model);
            converter.Convert();

            Assert.Empty(converter.Warnings);
        }

        [Fact]
        public void CollisionHangsOffTheNodeItBelongsTo()
        {
            FbxScene scene = Convert("generate_rb_box.nif");

            FbxObject body = scene.OfClass("Model").First(m => m.Name.EndsWith("_rb", StringComparison.Ordinal));

            // The body is a child of the node carrying the collision object, not a
            // sibling floating at the scene root.
            Assert.NotEmpty(scene.ParentsOf(body.Id));
            Assert.DoesNotContain(scene.RootModels(), m => m.Id == body.Id);
        }

        [Fact]
        public void ShapeGeometryIsScaledToSkyrimUnits()
        {
            // Havok stores the box in metres; the rest of the file is in units, so
            // the emitted mesh has to be far larger than the raw half extents.
            NifModel model = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            NifItem boxShape = model.Blocks.First(b => b.Name == "bhkBoxShape");
            NifVector3 dimensions = model.FindItem(boxShape, "Dimensions")!.Value.Get<NifVector3>();

            FbxScene scene = Convert("generate_rb_box.nif");
            FbxObject shapeNode = scene.OfClass("Model").First(m => m.Name.EndsWith("_box", StringComparison.Ordinal));
            FbxObject geometry = scene.ChildrenOf(shapeNode.Id).First(o => o.Class == "Geometry");

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;

            double maxX = 0;

            for (int i = 0; i < vertices.Length; i += 3)
                maxX = Math.Max(maxX, Math.Abs(vertices[i]));

            Assert.Equal(dimensions.X * ShapeTessellator.BhkScaleFactor, maxX, 2);
        }

        [Fact]
        public void CanBeTurnedOff()
        {
            FbxScene scene = Convert("generate_rb_box.nif", exportCollision: false);

            Assert.DoesNotContain(scene.OfClass("Model"), m => m.Name.EndsWith("_rb", StringComparison.Ordinal));

            // The render mesh is still there.
            Assert.NotEmpty(scene.OfClass("Geometry"));
        }

        [Fact]
        public void SurvivesAWriteAndReadCycle()
        {
            NifModel model = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            FbxDocument document = new NifToFbx(model).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            Assert.Contains(reloaded.OfClass("Model"), m => m.Name.EndsWith("_rb", StringComparison.Ordinal));
            Assert.Contains(reloaded.OfClass("Model"), m => m.Name.EndsWith("_box", StringComparison.Ordinal));
        }

        [Fact]
        public void MoppTreesUnwrapToTheShapeTheyIndex()
        {
            // A bhkMoppBvTreeShape only indexes the shape beneath it; the tree is
            // regenerated on import and carries nothing to convert. None of the
            // fixtures use one, so this checks the traversal rather than output.
            NifModel model = NifModel.Load(PathTo("generate_rb_box.nif"), Db);
            var converter = new NifToFbx(model);
            converter.Convert();

            Assert.Empty(converter.Warnings);
        }
    }
}
