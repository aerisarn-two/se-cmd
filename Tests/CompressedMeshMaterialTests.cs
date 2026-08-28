using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// A collision mesh made of more than one Havok material.
    /// </summary>
    /// <remarks>
    /// Three things had to be true at once and none of them were. The materials live in
    /// a table on the shape's *data* block, which the export never reached, so the mesh
    /// went into the scene with no material at all. mopper was never told them, so Havok
    /// left `Chunk::m_materialInfo` unwritten and it held whatever was in that memory.
    /// And the import wrote that number straight into an array index.
    ///
    /// ck-cmd sets the pair Havok needs -- `hkpNamedMeshMaterial` entries on the shape
    /// (`HKXWrangler.cpp:3354`) and a material on every triangle
    /// (`FBXWrangler.cpp:1886`) -- which is why its chunk indices mean something.
    /// </remarks>
    public class CompressedMeshMaterialTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        [Fact]
        public void TheSceneCarriesEveryMaterialAChunkedMeshIsMadeOf()
        {
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifItem data = source.Blocks.First(b => b.Name == "bhkCompressedMeshShapeData");
            int materials = (int)source.GetUInt(data, "Num Materials");

            Assert.True(materials > 1, "the fixture's collision mesh has one material");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject holder = scene.Objects.First(
                o => o.Class == "Model" && o.Name.EndsWith("_mesh", StringComparison.Ordinal));

            // One FBX material per table entry, so a per-polygon index means the same
            // thing on the way back.
            Assert.Equal(
                materials,
                scene.ChildrenOf(holder.Id).Count(o => o.Class == "Material"));

            // And the channel that says which polygon is which.
            FbxObject geometry = scene.ChildrenOf(holder.Id).First(o => o.Class == "Geometry");

            Assert.NotNull(FbxMeshReader.ReadPolygonMaterials(geometry));
        }

        [Fact]
        public void AChunkedMeshComesBackWithEveryMaterialItHad()
        {
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifItem sourceData = source.Blocks.First(b => b.Name == "bhkCompressedMeshShapeData");
            uint materials = source.GetUInt(sourceData, "Num Materials");

            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifModel rebuilt = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);

            // A mopper without -ccmm refuses a multi-material mesh rather than merging
            // it into one substance, so there is nothing to check against one.
            if (rebuilt.Blocks.FirstOrDefault(b => b.Name == "bhkCompressedMeshShapeData") is not { } data)
                return;

            Assert.Equal(materials, rebuilt.GetUInt(data, "Num Materials"));

            List<uint> onChunks =
                [.. (rebuilt.FindItem(data, "Chunks")?.Children ?? [])
                    .Select(c => rebuilt.GetUInt(c, "Material Index"))];

            Assert.NotEmpty(onChunks);

            // In range, which is what the field used to fail at outright.
            Assert.All(onChunks, m => Assert.True(m < materials, $"chunk material {m} of {materials}"));

            // And more than one of them, or the split did nothing.
            Assert.True(
                onChunks.Distinct().Count() > 1,
                "every chunk came back on the same material");

            // The table holds the same Havok materials the source did.
            static List<string> Names(NifModel m, NifItem block) =>
                [.. (m.FindItem(block, "Chunk Materials")?.Children ?? [])
                    .Select(e => FbxCollisionMaterial.NameOf(m, e)).Order()];

            Assert.Equal(Names(source, sourceData), Names(rebuilt, data));
        }
    }
}
