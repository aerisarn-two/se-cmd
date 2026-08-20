using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Skinning, against nifly's LE and SE fixtures.
    /// </summary>
    /// <remarks>
    /// The two editions store weights in different places. LE keeps them in
    /// NiSkinData's bone list; SE keeps them per vertex in the skin partition,
    /// which also owns the geometry. Both fixtures describe the same two-bone
    /// cylinder, so the same assertions should hold either way.
    /// </remarks>
    public class SkinningTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        /// <summary>The skinned fixtures, and which edition each is.</summary>
        public static TheoryData<string> Skinned() =>
        [
            "TestNifFile_Skinned_SE.nif",
            "TestNifFile_Skinned_Dynamic_SE.nif",
            "TestNifFile_Skinned_NoNiSkinDataWeights.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif",
            "TestNifFile_Optimize_Dynamic_SE_to_LE.nif"
        ];

        private static NifItem FirstSkinnedShape(NifModel model) =>
            model.Blocks.First(b => model.GetSkinInstance(b) is not null);

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ReadsSkinFromEitherEdition(string name)
        {
            NifModel model = Load(name);
            SkinData? skin = model.ReadSkin(FirstSkinnedShape(model));

            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.Bones);

            // Every bone must be named, or the FBX side cannot link a cluster to it.
            Assert.All(skin.Bones, b => Assert.False(string.IsNullOrEmpty(b.Name)));

            // And at least one must actually move something.
            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        [Theory]
        [MemberData(nameof(Skinned))]
        public void WeightsPerVertexSumToOne(string name)
        {
            NifModel model = Load(name);
            SkinData skin = model.ReadSkin(FirstSkinnedShape(model))!;

            foreach ((ushort vertex, List<(int Bone, float Weight)> influences) in skin.ByVertex())
            {
                float total = influences.Sum(i => i.Weight);

                // A vertex whose weights do not sum to one drifts toward the origin
                // when the mesh deforms.
                Assert.True(Math.Abs(total - 1f) < 0.01f,
                    $"vertex {vertex} has weights summing to {total:G6}");
            }
        }

        [Fact]
        public void BothEditionsDescribeTheSameSkin()
        {
            // The LE and SE fixtures are the same cylinder saved twice, so the skins
            // should agree despite being stored completely differently.
            SkinData le = Load("TestNifFile_Optimize_Dynamic_LE_to_SE.nif") is var lm
                ? lm.ReadSkin(FirstSkinnedShape(lm))!
                : throw new InvalidOperationException();

            NifModel sm = Load("TestNifFile_Skinned_SE.nif");
            SkinData se = sm.ReadSkin(FirstSkinnedShape(sm))!;

            Assert.Equal(le.Bones.Count, se.Bones.Count);
            Assert.Equal(
                le.Bones.Select(b => b.Name).OrderBy(n => n),
                se.Bones.Select(b => b.Name).OrderBy(n => n));
        }

        [Fact]
        public void ReadsWeightsWhenOnlyThePartitionHasThem()
        {
            // This fixture exists precisely because NiSkinData carries no weights,
            // so they can only come from the partition.
            NifModel model = Load("TestNifFile_Skinned_NoNiSkinDataWeights.nif");
            SkinData skin = model.ReadSkin(FirstSkinnedShape(model))!;

            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        [Fact]
        public void UnskinnedShapesReportNoSkin()
        {
            NifModel model = Load("TestNifFile_Static_SE.nif");

            Assert.All(model.Blocks, b => Assert.Null(model.ReadSkin(b)));
        }

        // --- limiting influences ---------------------------------------------

        [Fact]
        public void LimitingInfluencesKeepsTheHeaviestAndRenormalises()
        {
            var skin = new SkinData();

            for (int i = 0; i < 6; i++)
                skin.Bones.Add(new SkinBone { Name = $"Bone{i}" });

            // One vertex pulled by six bones, which Skyrim cannot represent.
            float[] weights = [0.30f, 0.25f, 0.20f, 0.15f, 0.07f, 0.03f];

            for (int i = 0; i < weights.Length; i++)
                skin.Bones[i].Weights.Add((0, weights[i]));

            skin.LimitInfluences(4);

            var influences = skin.ByVertex()[0];

            Assert.Equal(4, influences.Count);

            // Renormalised, or the vertex would be under-weighted by the 10% that
            // the dropped influences carried.
            Assert.Equal(1f, influences.Sum(i => i.Weight), 4);

            // The four kept are the heaviest four.
            Assert.Equal([0, 1, 2, 3], influences.Select(i => i.Bone).OrderBy(b => b));
        }

        // --- conversion to FBX -------------------------------------------------

        [Theory]
        [MemberData(nameof(Skinned))]
        public void ConvertsSkinToFbxDeformers(string name)
        {
            NifModel model = Load(name);
            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            Assert.Empty(converter.Warnings);

            // One Skin deformer per skinned mesh, and a Cluster per bone under it.
            var skins = scene.OfClass("Deformer", "Skin").ToList();
            Assert.NotEmpty(skins);

            foreach (FbxObject skin in skins)
            {
                var clusters = scene.ChildrenOf(skin.Id)
                    .Where(o => o.Class == "Deformer" && o.SubClass == "Cluster")
                    .ToList();

                Assert.NotEmpty(clusters);

                foreach (FbxObject cluster in clusters)
                {
                    // A cluster with no bone linked to it deforms nothing.
                    Assert.NotEmpty(scene.ChildrenOf(cluster.Id).Where(o => o.Class == "Model"));

                    var indices = (int[])cluster.Child("Indexes")!.Properties[0]!;
                    var weights = (double[])cluster.Child("Weights")!.Properties[0]!;

                    Assert.Equal(indices.Length, weights.Length);
                    Assert.NotEmpty(indices);
                }
            }
        }

        [Fact]
        public void SkinIsAttachedToTheGeometry()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject geometry = scene.OfClass("Geometry").First();

            // FBX hangs the skin off the geometry, not off the model.
            Assert.Single(scene.ChildrenOf(geometry.Id).Where(o => o.SubClass == "Skin"));
        }

        [Fact]
        public void ClusterIndicesAreInRangeOfTheMesh()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject geometry = scene.OfClass("Geometry").First();
            int vertexCount = ((double[])geometry.Child("Vertices")!.Properties[0]!).Length / 3;

            FbxObject skin = scene.ChildrenOf(geometry.Id).First(o => o.SubClass == "Skin");

            foreach (FbxObject cluster in scene.ChildrenOf(skin.Id).Where(o => o.SubClass == "Cluster"))
            {
                var indices = (int[])cluster.Child("Indexes")!.Properties[0]!;

                // An index past the mesh is how a skin silently deforms nothing.
                Assert.All(indices, i => Assert.InRange(i, 0, vertexCount - 1));
            }
        }

        [Fact]
        public void SkinSurvivesAWriteAndReadCycle()
        {
            NifModel model = Load("TestNifFile_Skinned_SE.nif");
            FbxDocument document = new NifToFbx(model).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            FbxObject geometry = reloaded.OfClass("Geometry").First();
            SkinData? skin = FbxSkinIO.ReadSkin(reloaded, geometry);

            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.Bones);
            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        // --- writing back ------------------------------------------------------

        /// <summary>NIF to FBX and back, for a skinned mesh.</summary>
        private static NifModel RoundTrip(string name, bool legendary)
        {
            NifModel source = Load(name);
            FbxDocument document = new NifToFbx(source).Convert();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions
            {
                RootName = "skinned",
                LegendaryEdition = legendary
            });

            NifModel rebuilt = converter.Convert(Db);

            Assert.Empty(converter.Warnings);

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WritesSkinBlocksForEitherEdition(bool legendary)
        {
            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary);

            Assert.Contains(model.Blocks, b => b.Name == "BSDismemberSkinInstance");
            Assert.Contains(model.Blocks, b => b.Name == "NiSkinData");

            // Skyrim renders skinned geometry from the partition, so a skin without
            // one draws as though it had no skeleton at all.
            Assert.Contains(model.Blocks, b => b.Name == "NiSkinPartition");
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WrittenSkinReadsBack(bool legendary)
        {
            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary);

            NifItem shape = model.Blocks.First(b => model.GetSkinInstance(b) is not null);
            SkinData? skin = model.ReadSkin(shape);

            Assert.NotNull(skin);
            Assert.NotEmpty(skin!.Bones);
            Assert.Contains(skin.Bones, b => b.Weights.Count > 0);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void WrittenWeightsStillSumToOne(bool legendary)
        {
            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary);

            NifItem shape = model.Blocks.First(b => model.GetSkinInstance(b) is not null);
            SkinData skin = model.ReadSkin(shape)!;

            foreach ((ushort vertex, List<(int Bone, float Weight)> influences) in skin.ByVertex())
            {
                float total = influences.Sum(i => i.Weight);
                Assert.True(Math.Abs(total - 1f) < 0.01f, $"vertex {vertex} sums to {total:G6}");
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BoneNamesSurviveTheRoundTrip(bool legendary)
        {
            NifModel source = Load("TestNifFile_Skinned_SE.nif");
            SkinData before = source.ReadSkin(FirstSkinnedShape(source))!;

            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary);
            SkinData after = model.ReadSkin(FirstSkinnedShape(model))!;

            Assert.Equal(
                before.Bones.Where(b => b.Weights.Count > 0).Select(b => b.Name).OrderBy(n => n),
                after.Bones.Where(b => b.Weights.Count > 0).Select(b => b.Name).OrderBy(n => n));
        }

        [Fact]
        public void BonesReferenceRealNodes()
        {
            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary: false);

            NifItem skin = model.Blocks.First(b => b.Name == "BSDismemberSkinInstance");

            // A dangling bone link is how a skin silently deforms nothing.
            var bones = model.GetRefArray(skin, "Bones").ToList();

            Assert.NotEmpty(bones);
            Assert.All(bones, b => Assert.True(model.BlockInherits(b, "NiNode")));

            // ...and the skeleton root has to resolve too.
            Assert.NotNull(model.GetRef(skin, "Skeleton Root"));
        }

        [Fact]
        public void PartitionCapsInfluencesAtFour()
        {
            NifModel model = RoundTrip("TestNifFile_Skinned_SE.nif", legendary: false);

            NifItem partition = model.Blocks.First(b => b.Name == "NiSkinPartition");
            NifItem entry = model.FindItem(partition, "Partitions")!.Children[0];

            Assert.Equal((uint)NifSkinWriter.MaxInfluences,
                model.GetUInt(entry, "Num Weights Per Vertex"));

            // Every vertex gets exactly that many slots, padded with zero weights.
            NifItem weights = model.FindItem(entry, "Vertex Weights")!;
            Assert.All(weights.Children, w => Assert.Equal(NifSkinWriter.MaxInfluences, w.Children.Count));
        }

        [Fact]
        public void TwoShapesThatSharedASkinDataStillShareIt()
        {
            // Bethesda's files point two shapes at one NiSkinData and one
            // NiSkinPartition -- a facegen head's two scar marks are the same weights
            // on the same bone, so the blocks are shared rather than duplicated.
            // Rebuilding each shape's skin on its own turns one block into two.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem bone = model.InsertBlock("NiNode");
            model.SetString(bone, "Name", "Bone0");

            var children = new List<NifItem> { bone };
            var shapes = new List<NifItem>();

            for (int i = 0; i < 2; i++)
            {
                NifItem shape = model.InsertBlock("BSTriShape");
                model.SetString(shape, "Name", $"Mark{i}");
                shapes.Add(shape);
                children.Add(shape);
            }

            if (model.SetArraySize(root, "Num Children", "Children", children.Count) is { } array)
            {
                for (int i = 0; i < children.Count; i++)
                    array.Children[i].Value.SetLink(model.IndexOf(children[i]));
            }

            var nodes = new Dictionary<string, NifItem>(StringComparer.Ordinal) { ["Bone0"] = bone };
            var triangles = new List<NifTriangle> { new(0, 1, 2) };
            var shared = new Dictionary<int, (NifItem Data, NifItem Partition)>();

            // Both skins name the same source block, which is what sharing is.
            foreach (NifItem shape in shapes)
            {
                var skin = new SkinData { SkinDataId = 42 };
                var influenced = new SkinBone { Name = "Bone0" };

                influenced.Weights.Add((0, 1f));
                influenced.Weights.Add((1, 1f));
                influenced.Weights.Add((2, 1f));
                skin.Bones.Add(influenced);

                Assert.Empty(model.WriteSkin(shape, skin, nodes, root, 3, triangles, "NiSkinInstance", shared));
            }

            // Two instances, one data, one partition -- as the source has.
            Assert.Equal(2, model.Blocks.Count(b => b.Name == "NiSkinInstance"));
            NifItem data = Assert.Single(model.Blocks, b => b.Name == "NiSkinData");
            Assert.Single(model.Blocks, b => b.Name == "NiSkinPartition");

            // And both instances point at it, rather than one pointing at nothing.
            foreach (NifItem shape in shapes)
                Assert.Equal(data, model.GetRef(model.GetRef(shape, "Skin")!, "Data"));
        }

        [Fact]
        public void ASkinThatNamesNoSourceBlockGetsItsOwn()
        {
            // A skin authored in a DCC tool shared nothing, and -1 has to mean "its
            // own" rather than "the same as every other unmarked skin".
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem bone = model.InsertBlock("NiNode");
            model.SetString(bone, "Name", "Bone0");

            var nodes = new Dictionary<string, NifItem>(StringComparer.Ordinal) { ["Bone0"] = bone };
            var triangles = new List<NifTriangle> { new(0, 1, 2) };
            var shared = new Dictionary<int, (NifItem Data, NifItem Partition)>();

            for (int i = 0; i < 2; i++)
            {
                NifItem shape = model.InsertBlock("BSTriShape");
                model.SetString(shape, "Name", $"Mesh{i}");

                var skin = new SkinData();
                var influenced = new SkinBone { Name = "Bone0" };

                influenced.Weights.Add((0, 1f));
                influenced.Weights.Add((1, 1f));
                influenced.Weights.Add((2, 1f));
                skin.Bones.Add(influenced);

                model.WriteSkin(shape, skin, nodes, root, 3, triangles, "NiSkinInstance", shared);
            }

            Assert.Equal(2, model.Blocks.Count(b => b.Name == "NiSkinData"));
            Assert.Equal(2, model.Blocks.Count(b => b.Name == "NiSkinPartition"));
        }
    }
}
