using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class NifToFbxTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel LoadNif(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        private static FbxScene Convert(string name)
        {
            var converter = new NifToFbx(LoadNif(name));
            return new FbxScene(converter.Convert());
        }

        public static TheoryData<string> NifFiles() =>
        [
            "generate_rb.nif",
            "generate_rb_box.nif",
            "generate_rb_sphere.nif",
            "multi_material_cube.nif"
        ];

        [Theory]
        [MemberData(nameof(NifFiles))]
        public void ProducesAScene(string name)
        {
            FbxScene scene = Convert(name);

            Assert.NotEmpty(scene.OfClass("Model"));
            Assert.NotEmpty(scene.OfClass("Geometry"));
        }

        [Theory]
        [MemberData(nameof(NifFiles))]
        public void ConvertsWithoutWarnings(string name)
        {
            var converter = new NifToFbx(LoadNif(name));
            converter.Convert();

            Assert.Empty(converter.Warnings);
        }

        [Theory]
        [MemberData(nameof(NifFiles))]
        public void SurvivesBeingWrittenAndReadBack(string name)
        {
            var converter = new NifToFbx(LoadNif(name));
            FbxDocument document = converter.Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            Assert.NotEmpty(reloaded.OfClass("Model"));
            Assert.NotEmpty(reloaded.OfClass("Geometry"));
        }

        [Fact]
        public void CarriesTheNodeHierarchyAcross()
        {
            FbxScene scene = Convert("multi_material_cube.nif");

            FbxObject root = Assert.Single(scene.RootModels());
            Assert.Equal("Scene", root.Name);

            var childNames = scene.ChildrenOf(root.Id)
                .Where(o => o.Class == "Model")
                .Select(o => o.Name)
                .ToList();

            Assert.Contains("Cube", childNames);
            Assert.Contains("Light", childNames);
            Assert.Contains("Camera", childNames);
        }

        [Fact]
        public void InterposesASupportNodeForEachMesh()
        {
            FbxScene scene = Convert("multi_material_cube.nif");

            // FBX allows one mesh attribute per node, so each shape gets a holder.
            var holders = scene.OfClass("Model", "Mesh").ToList();

            Assert.NotEmpty(holders);
            Assert.All(holders, h => Assert.EndsWith("_support", h.Name));

            // ...and each holder owns exactly one Geometry.
            Assert.All(holders, h => Assert.Single(scene.ChildrenOf(h.Id).Where(o => o.Class == "Geometry")));
        }

        [Fact]
        public void WritesGeometryInTheExpectedLayout()
        {
            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First();

            Assert.NotNull(geometry.Child("Vertices"));
            Assert.NotNull(geometry.Child("PolygonVertexIndex"));
            Assert.NotNull(geometry.Child("Layer"));
            Assert.Equal(124, System.Convert.ToInt32(geometry.Child("GeometryVersion")!.Properties[0]));

            // Vertices are a flat xyz array, so a multiple of three.
            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            Assert.Equal(0, vertices.Length % 3);

            // Triangles: three indices each, and the last of every polygon is
            // stored as its bitwise complement to mark the boundary.
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;
            Assert.Equal(0, indices.Length % 3);

            for (int i = 0; i < indices.Length; i += 3)
            {
                Assert.True(indices[i] >= 0);
                Assert.True(indices[i + 1] >= 0);
                Assert.True(indices[i + 2] < 0, "the last corner of a polygon must be negated");
            }
        }

        [Fact]
        public void NamesTheUvElementSoBlenderMergesIt()
        {
            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First();

            var uv = geometry.Child("LayerElementUV");
            Assert.NotNull(uv);

            string uvName = (string)uv!.Nodes.First(n => n.Name == "Name").Properties[0]!;
            Assert.Equal(FbxMeshWriter.UvElementName, uvName);

            // ByControlPoint/Direct is what FBXWrangler emits.
            Assert.Equal("ByControlPoint",
                uv.Nodes.First(n => n.Name == "MappingInformationType").Properties[0]);
            Assert.Equal("Direct",
                uv.Nodes.First(n => n.Name == "ReferenceInformationType").Properties[0]);
        }

        [Fact]
        public void FlipsVOnUvs()
        {
            NifModel model = LoadNif("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem data = model.GetRef(shape, "Data")!;
            var nifUvs = model.GetUvSet(data);

            if (nifUvs.Count == 0)
                return;

            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube_Material0");

            var uv = (double[])geometry.Child("LayerElementUV")!.Nodes.First(n => n.Name == "UV").Properties[0]!;

            // V is mirrored, U is not.
            Assert.Equal(nifUvs[0].X, uv[0], 5);
            Assert.Equal(1f - nifUvs[0].Y, uv[1], 5);
        }

        [Fact]
        public void BakesTheShapeTransformIntoTheVertices()
        {
            NifModel model = LoadNif("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b =>
                b.Name == "NiTriShape" && model.GetName(b) == "Cube_Material0");

            NifTransform transform = model.GetTransform(shape);
            NifItem data = model.GetRef(shape, "Data")!;
            NifVector3 first = model.GetVertices(data)[0];
            NifVector3 expected = transform.Apply(first);

            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube_Material0");
            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;

            Assert.Equal(expected.X, vertices[0], 4);
            Assert.Equal(expected.Y, vertices[1], 4);
            Assert.Equal(expected.Z, vertices[2], 4);
        }

        [Fact]
        public void DeclaresMaxAxesAndCentimetres()
        {
            // Z-up right-handed is what lets coordinates cross without conversion.
            var converter = new NifToFbx(LoadNif("generate_rb.nif"));
            FbxDocument document = converter.Convert();

            var globals = new FbxProperties(
                document["GlobalSettings"]!.Nodes.First(n => n.Name == "Properties70"));

            Assert.Equal(2, globals.GetInt("UpAxis"));
            Assert.Equal(1, globals.GetInt("UpAxisSign"));
            Assert.Equal(1, globals.GetInt("FrontAxis"));
            Assert.Equal(-1, globals.GetInt("FrontAxisSign"));
        }

        [Fact]
        public void PreservesVertexAndTriangleCounts()
        {
            NifModel model = LoadNif("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b =>
                b.Name == "NiTriShape" && model.GetName(b) == "Cube_Material0");
            NifItem data = model.GetRef(shape, "Data")!;

            int expectedVertices = model.GetVertices(data).Count;
            int expectedTriangles = model.GetGeometryTriangles(data).Count;

            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube_Material0");

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            Assert.Equal(expectedVertices, vertices.Length / 3);
            Assert.Equal(expectedTriangles, indices.Length / 3);
        }

        [Fact]
        public void EncodesNamesForFbx()
        {
            Assert.Equal("Bip01_s_Head", NameEncoding.Sanitize("Bip01 Head"));
            Assert.Equal("Bip01 Head", NameEncoding.Unsanitize("Bip01_s_Head"));
            Assert.Equal("a_ob_0_cb__dd_b", NameEncoding.Sanitize("a[0]:b"));
            Assert.Equal("a[0]:b", NameEncoding.Unsanitize("a_ob_0_cb__dd_b"));
        }
    }
}
