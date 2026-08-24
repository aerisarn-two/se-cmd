using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The reader and writer against every NIF in the test resources.
    /// </summary>
    /// <remarks>
    /// Three sets, with different provenance and different reasons for being here:
    /// four cubes this project's own tooling produced, nifly's corpus, and one
    /// skeleton from XPMSSE. Between them they cover Skyrim LE and SE, skinned
    /// meshes, deep block graphs, loose blocks, multi-bounds, ordered nodes,
    /// collision, constraints, particles and controllers.
    ///
    /// The fidelity checks below sweep the lot rather than naming files, so a
    /// fixture added for one narrow reason is checked against the whole reader and
    /// writer for free. Everything else in this class is about the corpus files
    /// specifically, and names them.
    /// </remarks>
    public class CorpusTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string ResourceRoot => Path.Combine(AppContext.BaseDirectory, "Resources");

        private static string PathTo(string name) => Path.Combine(ResourceRoot, "nifly", name);

        /// <summary>
        /// Every NIF in the resources, by path relative to them.
        /// </summary>
        /// <remarks>
        /// Found rather than listed, so that adding a fixture anywhere under
        /// Resources puts it through the checks below without anyone remembering to.
        /// The corrupt one is excluded because failing to load is what it is for.
        /// </remarks>
        public static TheoryData<string> AllFixtures()
        {
            var data = new TheoryData<string>();

            foreach (string relative in FixturePaths())
                data.Add(relative);

            return data;
        }

        /// <summary>The same list as <see cref="AllFixtures"/>, as plain strings.</summary>
        public static IEnumerable<string> FixturePaths() =>
            Directory
                .GetFiles(ResourceRoot, "*.nif", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => Path.GetRelativePath(ResourceRoot, f))
                .Where(FixtureFiles.IsFixture);

        /// <summary>The skinned files, which are the ones with skin blocks.</summary>
        public static TheoryData<string> Skinned() =>
        [
            "TestNifFile_Skinned_SE.nif",
            "TestNifFile_Skinned_Dynamic_SE.nif",
            "TestNifFile_Skinned_NoNiSkinDataWeights.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif",
            "TestNifFile_Optimize_Dynamic_SE_to_LE.nif",
            "TestNifFile_LooseBlocks_SE.nif"
        ];

        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void LoadsWithoutWarnings(string relative)
        {
            NifModel model = NifModel.Load(Path.Combine(ResourceRoot, relative), Db);

            Assert.NotEmpty(model.Blocks);
            Assert.Empty(model.Warnings);
        }

        /// <summary>
        /// The load/save round trip, which is the strongest single check on the
        /// whole reader and writer: descriptors, conditions, array lengths and both
        /// stream directions all have to agree for the bytes to match.
        /// </summary>
        [Theory]
        [MemberData(nameof(AllFixtures))]
        public void SavingReproducesTheFileByteForByte(string relative)
        {
            string path = Path.Combine(ResourceRoot, relative);

            byte[] original = File.ReadAllBytes(path);

            NifModel model = NifModel.Load(path, Db);

            using var saved = new MemoryStream();
            model.Save(saved);

            byte[] actual = saved.ToArray();

            Assert.Equal(original.Length, actual.Length);

            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != actual[i])
                {
                    Assert.Fail($"{relative} differs at offset 0x{i:X} " +
                                $"(expected 0x{original[i]:X2}, got 0x{actual[i]:X2})");
                }
            }
        }

        [Fact]
        public void CoversBothSkyrimStreamVersions()
        {
            var versions = FixturePaths()
                .Select(r => NifModel.Load(Path.Combine(ResourceRoot, r), Db).BSVersion)
                .Distinct()
                .ToList();

            // 83 is Skyrim LE, 100 Skyrim SE. This project's own fixtures are all 83,
            // so both only appear because the borrowed corpora are here.
            Assert.Contains(83u, versions);
            Assert.Contains(100u, versions);
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ReadsSkinningBlocks(string name)
        {
            NifModel model = NifModel.Load(PathTo(name), Db);

            // One skin instance per shape, and Bethesda's files use the dismember
            // subclass rather than a plain NiSkinInstance.
            var skins = model.Blocks
                .Where(b => model.BlockInherits(b, "NiSkinInstance"))
                .ToList();

            Assert.NotEmpty(skins);

            foreach (NifItem skin in skins)
            {
                // A skin is meaningless without its bones and its per-bone weights.
                NifItem? data = model.GetRef(skin, "Data");
                Assert.NotNull(data);

                uint bones = model.GetUInt(skin, "Num Bones");
                Assert.True(bones > 0, "a skin must reference at least one bone");

                Assert.Equal(bones, model.GetUInt(data!, "Num Bones"));

                // Every bone link must resolve to a real node.
                foreach (NifItem bone in model.GetRefArray(skin, "Bones"))
                    Assert.True(model.BlockInherits(bone, "NiNode"), $"{bone.Name} is not a node");
            }
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void SkinWeightsAreWellFormed(string name)
        {
            NifModel model = NifModel.Load(PathTo(name), Db);

            NifItem skin = model.Blocks.First(b => model.BlockInherits(b, "NiSkinInstance"));
            NifItem data = model.GetRef(skin, "Data")!;

            NifItem? boneList = model.FindItem(data, "Bone List");

            if (boneList is null)
                return;

            foreach (NifItem bone in boneList.Children)
            {
                NifItem? weights = model.FindItem(bone, "Vertex Weights");

                if (weights is null)
                    continue;

                foreach (NifItem entry in weights.Children)
                {
                    float weight = model.FindItem(entry, "Weight")!.Value.ToFloat();

                    // A weight outside 0..1 means the layout was misread rather than
                    // the file being odd.
                    Assert.InRange(weight, 0f, 1.0001f);
                }
            }
        }

        [Fact]
        public void NiTriShapeGeometryConvertsToFbx()
        {
            // The LE file stores its geometry as NiTriShape, which the converter
            // handles. Skinning is not carried across yet, but the mesh must still
            // arrive rather than the shape being skipped.
            NifModel model = NifModel.Load(PathTo("TestNifFile_Optimize_Dynamic_LE_to_SE.nif"), Db);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            Assert.Equal(2, scene.OfClass("Geometry").Count());
        }

        [Fact]
        public void BsTriShapeGeometryConvertsToFbx()
        {
            // Skyrim SE stores geometry in BSTriShape, which packs its vertex data
            // inline and inherits NiAVObject directly rather than NiTriBasedGeom.
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            Assert.Contains(model.Blocks, b => b.Name == "BSTriShape");

            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            Assert.Empty(converter.Warnings);
            Assert.Equal(2, scene.OfClass("Geometry").Count());
        }

        [Fact]
        public void BsTriShapeVertexDataIsDecoded()
        {
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            // A skinned SE shape stores nothing in itself: both the vertex data and
            // the triangles live in the skin partition, and the shape's own counts
            // are zero. So the expected numbers come from the partition.
            NifItem shape = model.Blocks.First(b => b.Name == "BSTriShape");
            Assert.Equal(0u, model.GetUInt(shape, "Num Vertices"));

            NifItem partition = model.Blocks.First(b => b.Name == "NiSkinPartition");
            NifItem entry = model.FindItem(partition, "Partitions")!.Children[0];

            uint declaredVertices = model.GetUInt(entry, "Num Vertices");
            uint declaredTriangles = model.GetUInt(entry, "Num Triangles");

            var scene = new FbxScene(new NifToFbx(model).Convert());
            FbxObject geometry = scene.OfClass("Geometry").First();

            var vertices = (double[])geometry.Child("Vertices")!.Properties[0]!;
            var indices = (int[])geometry.Child("PolygonVertexIndex")!.Properties[0]!;

            Assert.Equal((int)declaredVertices, vertices.Length / 3);
            Assert.Equal((int)declaredTriangles, indices.Length / 3);

            // Positions come from a packed vertex struct, so a decoding slip shows
            // up as everything collapsing to the origin.
            bool anyNonZero = vertices.Any(v => Math.Abs(v) > 1e-6);
            Assert.True(anyNonZero, "decoded vertices are all at the origin");
        }

        [Fact]
        public void BsTriShapeNormalsAndUvsSurvive()
        {
            NifModel model = NifModel.Load(PathTo("TestNifFile_Skinned_SE.nif"), Db);

            var scene = new FbxScene(new NifToFbx(model).Convert());
            FbxObject geometry = scene.OfClass("Geometry").First();

            var normals = geometry.Child("LayerElementNormal");
            var uvs = geometry.Child("LayerElementUV");

            Assert.NotNull(normals);
            Assert.NotNull(uvs);

            // Normals are stored as signed bytes, so a unit length is the check that
            // the -1..1 expansion happened.
            var data = (double[])normals!.Nodes.First(n => n.Name == "Normals").Properties[0]!;

            double length = Math.Sqrt(data[0] * data[0] + data[1] * data[1] + data[2] * data[2]);
            Assert.InRange(length, 0.9, 1.1);
        }

        [Fact]
        public void RejectsADeliberatelyCorruptFile()
        {
            // nifly ships this to check that a reader fails rather than producing
            // nonsense. Loading it successfully would be the bug.
            Assert.ThrowsAny<Exception>(() =>
                NifModel.Load(PathTo("TestNifFile_Corrupted.nif"), Db));
        }

        [Fact]
        public void ReadsALargeBlockGraph()
        {
            // 185 blocks, which exercises the block-type table and index resolution
            // far harder than a seventeen-block fixture does.
            NifModel model = NifModel.Load(PathTo("TestNifFile_DeepGraph_SE.nif"), Db);

            Assert.True(model.Blocks.Count > 100, $"expected a deep graph, got {model.Blocks.Count} blocks");
            Assert.Equal((uint)model.Blocks.Count, model.GetUInt(model.Header, "Num Blocks"));
        }
    }
}
