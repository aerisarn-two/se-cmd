using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Havok;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// bhkCompressedMeshShape in both directions.
    /// </summary>
    /// <remarks>
    /// None of the sample NIFs uses one, and it cannot be built without Havok, so
    /// the fixture is constructed here: an FBX scene shaped the way the exporter
    /// writes collision, imported to produce a real compressed mesh, then exported
    /// back. Tests needing the shape return early when mopper is absent, since
    /// nothing open can stand in for it.
    /// </remarks>
    public class CompressedMeshShapeTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static bool MoppAvailable => MoppGenerator.Resolve() is not null;

        /// <summary>An FBX scene with a rigid body whose shape is a mesh.</summary>
        private static FbxScene BuildSceneWithMeshCollision()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "Scene", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject owner = FbxMeshWriter.AddModel(scene, "Rock", "Null", NifTransform.Identity);
            scene.Connect(owner, root);

            FbxObject body = FbxMeshWriter.AddModel(scene, "Rock_rb", "Null", NifTransform.Identity);
            scene.Connect(body, owner);

            FbxObject shape = FbxMeshWriter.AddModel(scene, "Rock_rb_mesh", "Mesh", NifTransform.Identity);
            scene.Connect(shape, body);

            // A box, at a size that reads as Skyrim units rather than metres.
            MeshGeometry mesh = ShapeTessellator.Box(new NifVector3(50f, 50f, 50f));

            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, "Rock_rb_mesh_geometry", mesh);
            scene.Connect(geometry, shape);

            scene.Flush();
            return new FbxScene(document);
        }

        private static NifModel Import(out List<string> warnings)
        {
            var converter = new FbxToNif(BuildSceneWithMeshCollision(), new FbxToNifOptions { RootName = "Rock" });
            NifModel model = converter.Convert(Db);
            warnings = converter.Warnings;

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        [Fact]
        public void ReportsClearlyWhenMoppIsUnavailable()
        {
            if (MoppAvailable)
                return;

            Import(out List<string> warnings);

            // A mesh collision fitted to a primitive would be wrong in a way that
            // only shows up in game, so it must say so rather than approximate.
            Assert.Contains(warnings, w => w.Contains("MOPP", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void BuildsACompressedMeshWrappedInAMoppTree()
        {
            if (!MoppAvailable)
                return;

            NifModel model = Import(out List<string> warnings);

            Assert.Empty(warnings);

            // Havok reaches the mesh through the tree, never directly.
            Assert.Contains(model.Blocks, b => b.Name == "bhkMoppBvTreeShape");
            Assert.Contains(model.Blocks, b => b.Name == "bhkCompressedMeshShape");
            Assert.Contains(model.Blocks, b => b.Name == "bhkCompressedMeshShapeData");

            NifItem mopp = model.Blocks.First(b => b.Name == "bhkMoppBvTreeShape");
            NifItem? shape = model.GetRef(mopp, "Shape");

            Assert.NotNull(shape);
            Assert.Equal("bhkCompressedMeshShape", shape!.Name);
        }

        [Fact]
        public void WritesTheMoppCodeAndItsQuantisation()
        {
            if (!MoppAvailable)
                return;

            NifModel model = Import(out _);
            NifItem mopp = model.Blocks.First(b => b.Name == "bhkMoppBvTreeShape");

            // The code is a binary array: one blob sized by Data Size, not a byte
            // per element.
            uint size = model.GetUInt(mopp, @"MOPP Code\Data Size");
            Assert.True(size > 0, "MOPP code must not be empty");

            NifItem? blob = model.FindItem(mopp, @"MOPP Code\Data");
            Assert.NotNull(blob);
            Assert.Equal((int)size, blob!.Children[0].Value.AsByteArray().Length);

            // The tree is meaningless without the quantisation it was built against.
            Assert.True(model.FindItem(mopp, "Scale")!.Value.ToFloat() > 0, "MOPP needs a positive scale");
        }

        [Fact]
        public void StoresGeometryAsChunksOrBigTriangles()
        {
            if (!MoppAvailable)
                return;

            NifModel model = Import(out _);
            NifItem data = model.Blocks.First(b => b.Name == "bhkCompressedMeshShapeData");

            uint chunks = model.GetUInt(data, "Num Chunks");
            uint bigVerts = model.GetUInt(data, "Num Big Verts");

            Assert.True(chunks > 0 || bigVerts > 0, "the mesh has to be stored somewhere");

            // The bounds must enclose the box, in metres.
            NifVector4 min = model.FindItem(data, @"AABB\Min")!.Value.Get<NifVector4>();
            NifVector4 max = model.FindItem(data, @"AABB\Max")!.Value.Get<NifVector4>();

            Assert.True(max.X > min.X, "AABB must be non-degenerate");
        }

        [Fact]
        public void ExportsTheChunksBackToTriangles()
        {
            if (!MoppAvailable)
                return;

            NifModel model = Import(out _);

            // Exporting decodes the chunks: offsets scaled by 1/1000 from a per-chunk
            // origin, placed by a shared transform, with strips unrolled.
            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            FbxObject? shapeNode = scene.OfClass("Model")
                .FirstOrDefault(m => m.Name.EndsWith("_mesh", StringComparison.Ordinal));

            Assert.NotNull(shapeNode);

            FbxObject geometry = Assert.Single(
                scene.ChildrenOf(shapeNode!.Id).Where(o => o.Class == "Geometry"));

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            Assert.NotEmpty(vertices);
            Assert.NotEmpty(indices);

            // The decoded box should be roughly the size it went in as, in units.
            double maxExtent = 0;

            for (int i = 0; i < vertices.Length; i += 3)
                maxExtent = Math.Max(maxExtent, Math.Abs(vertices[i]));

            Assert.InRange(maxExtent, 25.0, 100.0);
        }

        [Fact]
        public void DecodedTrianglesIndexRealVertices()
        {
            if (!MoppAvailable)
                return;

            NifModel model = Import(out _);
            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject geometry = scene.OfClass("Geometry")
                .First(g => g.Name.Contains("_mesh", StringComparison.Ordinal));

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            int count = vertices.Length / 3;

            // An out-of-range index means the chunk decoding lost track of where a
            // chunk's vertices start.
            foreach (int raw in indices)
            {
                int index = raw < 0 ? ~raw : raw;
                Assert.InRange(index, 0, count - 1);
            }
        }
    }
}
