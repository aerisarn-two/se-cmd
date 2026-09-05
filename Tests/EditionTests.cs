using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Targeting Skyrim LE versus SE.
    /// </summary>
    /// <remarks>
    /// nif.xml separates the two only by the Bethesda stream version: both are file
    /// version 20.2.0.7 with user version 12, LE at 83 and SE at 100. That one
    /// number changes which blocks are legal, most visibly the geometry: BSTriShape
    /// is declared versions="#SSE# #FO4# #F76#" and does not exist in LE.
    /// </remarks>
    public class EditionTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static NifModel Convert(bool legendary)
        {
            var scene = new FbxScene(FbxDocument.Load(PathTo("multi_material_cube.fbx")));

            NifModel model = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = "cube",
                LegendaryEdition = legendary
            }).Convert(Db);

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        [Fact]
        public void SpecialEditionIsTheDefault() =>
            Assert.Equal(100u, new FbxToNifOptions().BSVersion);

        [Fact]
        public void EditionsUseTheStreamVersionsNifXmlDeclares()
        {
            Assert.Equal(83u, new FbxToNifOptions { LegendaryEdition = true }.BSVersion);
            Assert.Equal(100u, new FbxToNifOptions { LegendaryEdition = false }.BSVersion);

            // Everything else about the two is identical.
            Assert.Equal(0x14020007u, new FbxToNifOptions().Version);
            Assert.Equal(12u, new FbxToNifOptions().UserVersion);
        }

        [Fact]
        public void LegendaryEditionWritesNiTriShape()
        {
            NifModel model = Convert(legendary: true);

            Assert.Equal(83u, model.BSVersion);
            Assert.Contains(model.Blocks, b => b.Name == "NiTriShape");
            Assert.Contains(model.Blocks, b => b.Name == "NiTriShapeData");

            // BSTriShape does not exist before SE.
            Assert.DoesNotContain(model.Blocks, b => b.Name == "BSTriShape");
        }

        [Fact]
        public void SpecialEditionWritesBsTriShape()
        {
            NifModel model = Convert(legendary: false);

            Assert.Equal(100u, model.BSVersion);
            Assert.Contains(model.Blocks, b => b.Name == "BSTriShape");

            // Its data is inline, so there is no separate data block.
            Assert.DoesNotContain(model.Blocks, b => b.Name == "NiTriShapeData");
        }

        [Fact]
        public void SpecialEditionGeometryIsComplete()
        {
            NifModel model = Convert(legendary: false);

            NifItem shape = model.Blocks.First(b => b.Name == "BSTriShape");

            uint vertices = model.GetUInt(shape, "Num Vertices");
            uint triangles = model.GetUInt(shape, "Num Triangles");

            Assert.True(vertices > 0, "no vertices were written");
            Assert.True(triangles > 0, "no triangles were written");

            // The array actually holds them, rather than the counts merely claiming so.
            Assert.Equal((int)vertices, model.FindItem(shape, "Vertex Data")!.Children.Count);
            Assert.Equal((int)triangles, model.FindItem(shape, "Triangles")!.Children.Count);
        }

        [Fact]
        public void SpecialEditionDescriptorMatchesTheRealFormat()
        {
            NifModel model = Convert(legendary: false);
            NifItem shape = model.Blocks.First(b => b.Name == "BSTriShape");

            var desc = new BSVertexDesc(model.FindItem(shape, "Vertex Desc")!.Value.ToUInt64());

            // The same packing a real SE mesh uses: the position is always first
            // and has no offset member, its fourth lane is taken by the bitangent,
            // then UV, normal and tangent follow.
            Assert.True(desc.HasFlag(VertexFlags.Vertex));

            if (desc.HasFlag(VertexFlags.UV))
                Assert.Equal(16u, desc.UVOffset);

            if (desc.HasFlag(VertexFlags.Normal))
                Assert.Equal(20u, desc.NormalOffset);

            // Data Size must agree with the stride, or the block reads short.
            uint declared = model.GetUInt(shape, "Data Size");
            uint expected = desc.VertexSize * model.GetUInt(shape, "Num Vertices")
                            + model.GetUInt(shape, "Num Triangles") * 6;

            Assert.Equal(expected, declared);
        }

        [Fact]
        public void SpecialEditionGeometryReadsBackThroughTheConverter()
        {
            // The strongest check: write SE geometry, then convert it to FBX using
            // the BSTriShape reader and confirm the mesh survives.
            NifModel model = Convert(legendary: false);

            int written = model.Blocks.Count(b => b.Name == "BSTriShape");
            var scene = new FbxScene(new NifToFbx(model).Convert());

            Assert.Equal(written, scene.OfClass("Geometry").Count());
        }
    }
}
