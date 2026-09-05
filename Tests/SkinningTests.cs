using LeanMeshIO;
using NIFSharp;
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

        [Theory]
        [InlineData("TestNifFile_Skinned_SE.nif")]
        [InlineData("TestNifFile_LooseBlocks_SE.nif")]
        public void ASkinPartitionKeepsEveryTriangle(string name)
        {
            // A skinned Skyrim SE shape keeps nothing in itself: both the vertices and
            // the triangles live in the skin partition, split into slices. Those
            // triangles index the shape's whole vertex array, and they were being put
            // through the partition's `Vertex Map` first, on the reading that a
            // partition's triangles are local to it.
            //
            // nif.xml says what the map is for in as many words — it maps "the
            // weight/influence lists in this submesh to the vertices in the shape being
            // skinned", the weights and not the faces. Mapping the triangles dropped
            // every index past the end of that partition's map (401 of a prisoner rags'
            // 2,132) and rewrote the rest to point at whichever vertex the map held, so
            // the surviving geometry was joined up wrongly.
            //
            // It read from outside as a mesh with vertices no triangle used, which is
            // the wrong end of it entirely: the vertices were fine, the triangles that
            // named them had been thrown away.
            //
            // **This does not reproduce that.** Both fixtures are single-partition with
            // an identity map, so the mapping was a no-op on them and this passes with
            // the fault reinstated. What caught it, and what measures it, is the corpus:
            // over 250 vanilla meshes the triangle count went from lossy to 556,162 of
            // 556,162. This guards the invariant so the next change to that loop has
            // something to fail against; a fixture with two partitions and a real map
            // would be worth adding.
            NifModel source = Load(name);

            // What the mesh describes, counted once. A skinned SE shape may keep its
            // triangles on the shape, in the partition, or in both; which of those the
            // rebuilt file chooses is a separate question from whether any went
            // missing, so the partition is only counted when the shape itself is empty.
            static int Triangles(NifModel m)
            {
                int total = 0;

                foreach (NifItem shape in m.Blocks)
                {
                    if (!m.BlockInherits(shape, "NiAVObject"))
                        continue;

                    int own = m.FindItem(shape, "Triangles")?.Children.Count ?? 0;

                    if (own > 0)
                    {
                        total += own;
                        continue;
                    }

                    NifItem? skin = m.GetRef(shape, "Skin") ?? m.GetRef(shape, "Skin Instance");
                    NifItem? part = skin is null
                        ? null
                        : m.GetRef(skin, "Skin Partition")
                          ?? (m.GetRef(skin, "Data") is { } d ? m.GetRef(d, "Skin Partition") : null);

                    if (part is null || m.FindItem(part, "Partitions") is not { } ps)
                        continue;

                    foreach (NifItem p in ps.Children)
                        total += m.FindItem(p, "Triangles")?.Children.Count ?? 0;
                }

                return total;
            }

            int before = Triangles(source);

            Assert.True(before > 0, $"{name} has no triangles to check");

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

            Assert.Equal(before, Triangles(rebuilt));
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

            // And says where it found them. Both copies read the same way -- the bone
            // list first, the renderer's when that holds nothing -- so which one a file
            // actually used is only knowable here, and a shape that kept its weights out
            // of NiSkinData came back with them in both.
            Assert.False(skin.WeightsInBoneList);
        }

        [Fact]
        public void AShapeThatKeptItsWeightsOutOfNiSkinDataStillDoes()
        {
            NifModel rebuilt = RoundTrip("TestNifFile_Skinned_NoNiSkinDataWeights.nif", legendary: false);

            NifItem data = rebuilt.Blocks.First(b => b.Name == "NiSkinData");

            Assert.Equal(0u, rebuilt.GetUInt(data, "Has Vertex Weights"));

            // The bones stay, with their bind poses: the flag says whether the weights
            // are there, not whether the bones are.
            NifItem bones = rebuilt.FindItem(data, "Bone List")!;

            Assert.NotEmpty(bones.Children);

            // And each keeps the count *this file* stated, which is 76 and 60.
            //
            // Not because a count beside a switched-off array means anything -- it does
            // not, and the game always writes zero there: of its 26,913 NiSkinData
            // blocks the 108 clearing this flag have every count at zero, without
            // exception. This fixture is nifly's and says otherwise, and both have to
            // come back as they went in. So the number travels with the bone rather than
            // being derived, and a scene that never stated one gets zero.
            Assert.Equal(
                new uint[] { 76, 60 },
                bones.Children.Select(b => rebuilt.GetUInt(b, "Num Vertices")).ToArray());

            // And the weights are still in the copy the renderer reads.
            SkinData skin = rebuilt.ReadSkin(FirstSkinnedShape(rebuilt))!;

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
        public void OnlyTheCopiesTheRendererReadsAreCutToFourInfluences()
        {
            // Six bones pulling one vertex. Skyrim renders four of them, and the
            // question is which copy of the weights that limit applies to.
            //
            // It applies to the two the renderer reads -- the partition, whose rows are
            // `Num Weights Per Vertex` wide, and the vertex buffer. `NiSkinData` is the
            // third copy and holds what was authored, which the game's own files show
            // going past four: over a 3,000-mesh sample it names a bone that no
            // partition of the same shape renders on 4,319 vertices. Cutting it back
            // here dropped those influences and renormalised what was left, moving every
            // other weight on the vertex with them.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem shape = model.InsertBlock("NiTriShape");
            model.SetString(shape, "Name", "Pulled");

            NifItem data = model.InsertBlock("NiTriShapeData");
            model.FindItem(data, "Num Vertices")!.Value.SetCount(3);
            model.FindItem(data, "Has Vertices")!.Value.SetCount(1);

            NifItem positions = model.FindItem(data, "Vertices")!;
            positions.InvalidateConditionsRecursive();
            model.UpdateArraySize(positions);

            for (int i = 0; i < 3; i++)
                positions.Children[i].Value.Set(new NifVector3(i, i * 2, 0f));

            model.SetRef(shape, "Data", data);

            float[] authored = [0.30f, 0.25f, 0.20f, 0.15f, 0.07f, 0.03f];

            var skin = new SkinData();
            var nodes = new Dictionary<string, NifItem>(StringComparer.Ordinal);

            for (int i = 0; i < authored.Length; i++)
            {
                var bone = new SkinBone { Name = $"Bone{i}" };
                bone.Weights.Add((0, authored[i]));
                skin.Bones.Add(bone);

                NifItem node = model.InsertBlock("NiNode");
                model.SetString(node, "Name", bone.Name);
                nodes[bone.Name] = node;
            }

            model.WriteSkin(
                shape, skin, nodes, root, 3, [new NifTriangle(0, 1, 2)], "NiSkinInstance");

            NifItem instance = model.GetSkinInstance(shape)!;

            // NiSkinData keeps all six, as authored and unscaled.
            NifItem boneList = model.FindItem(model.GetBlock(model.FindItem(instance, "Data")!)!, "Bone List")!;
            var kept = new List<float>();

            foreach (NifItem entry in boneList.Children)
            {
                if (model.FindItem(entry, "Vertex Weights") is not { } weights)
                    continue;

                foreach (NifItem w in weights.Children)
                {
                    if (model.GetUInt(w, "Index") == 0)
                        kept.Add(model.FindItem(w, "Weight")!.Value.ToFloat());
                }
            }

            kept.Sort();
            Assert.Equal(authored.OrderBy(x => x), kept);

            // The partition renders the heaviest four, normalised between them.
            NifItem partitions = model.FindItem(
                model.GetBlock(model.FindItem(instance, "Skin Partition")!)!, "Partitions")!;

            NifItem row = model.FindItem(partitions.Children[0], "Vertex Weights")!.Children[0];
            List<float> rendered = [.. row.Children.Select(c => c.Value.ToFloat()).Where(w => w > 0f)];

            Assert.Equal(4, rendered.Count);
            Assert.Equal(1f, rendered.Sum(), 4);

            // The four heaviest, and no others: 0.30, 0.25, 0.20 and 0.15 over their
            // own total of 0.90.
            Assert.Equal(
                authored.Take(4).Select(w => w / 0.90f).OrderBy(w => w),
                rendered.OrderBy(w => w),
                new FloatComparer(1e-4f));
        }

        /// <summary>Compares floats to a tolerance, for a sequence assertion.</summary>
        private sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
        {
            public bool Equals(float a, float b) => MathF.Abs(a - b) <= tolerance;

            public int GetHashCode(float value) => 0;
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

        [Fact]
        public void ASkinFindsBonesThatComeAfterItInTheTree()
        {
            // A cluster names a bone node, and the walk reaches a shape before it
            // reaches everything else. wrdrawbridge01's chains hang from four bones
            // that come after the shape, so all four were missing and the mesh left
            // with a skin deformer holding no clusters -- which is not a skin, and
            // came back as no skin at all.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            // Real geometry, so the shape travels as a mesh rather than as the
            // node an empty shape becomes.
            NifItem shape = model.InsertBlock("NiTriShape");
            model.SetString(shape, "Name", "Chains");

            NifItem data = model.InsertBlock("NiTriShapeData");
            model.FindItem(data, "Num Vertices")!.Value.SetCount(3);
            model.FindItem(data, "Has Vertices")!.Value.SetCount(1);

            NifItem positions = model.FindItem(data, "Vertices")!;
            positions.InvalidateConditionsRecursive();
            model.UpdateArraySize(positions);

            for (int i = 0; i < 3; i++)
                positions.Children[i].Value.Set(new NifVector3(i, i * 2, 0f));

            model.FindItem(data, "Num Triangles")!.Value.SetCount(1);
            model.FindItem(data, "Num Triangle Points")!.Value.SetCount(3);
            model.FindItem(data, "Has Triangles")!.Value.SetCount(1);

            NifItem list = model.FindItem(data, "Triangles")!;
            list.InvalidateConditionsRecursive();
            model.UpdateArraySize(list);
            list.Children[0].Value.Set(new NifTriangle(0, 1, 2));

            model.SetRef(shape, "Data", data);

            NifItem bone = model.InsertBlock("NiNode");
            model.SetString(bone, "Name", "ChainTop");

            // The shape first, the bone second: the order that used to lose the skin.
            if (model.SetArraySize(root, "Num Children", "Children", 2) is { } children)
            {
                children.Children[0].Value.SetLink(model.IndexOf(shape));
                children.Children[1].Value.SetLink(model.IndexOf(bone));
            }

            var skin = new SkinData();
            var influenced = new SkinBone { Name = "ChainTop" };

            influenced.Weights.Add((0, 1f));
            influenced.Weights.Add((1, 1f));
            influenced.Weights.Add((2, 1f));
            skin.Bones.Add(influenced);

            var nodes = new Dictionary<string, NifItem>(StringComparer.Ordinal) { ["ChainTop"] = bone };

            model.WriteSkin(shape, skin, nodes, root, 3, [new NifTriangle(0, 1, 2)], "NiSkinInstance");
            model.SetRoots([root]);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            FbxObject deformer = Assert.Single(
                scene.Objects, o => o.Class == "Deformer" && o.SubClass == "Skin");

            // A skin with no clusters is not a skin.
            Assert.NotEmpty(scene.ChildrenOf(deformer.Id).Where(o => o.SubClass == "Cluster"));
        }
    }
}
