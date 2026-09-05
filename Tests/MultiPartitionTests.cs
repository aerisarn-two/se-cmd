using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Splitting a skin across several partitions.
    /// </summary>
    /// <remarks>
    /// Skyrim's skinning shader addresses bones through a fixed-size palette, so a
    /// partition naming more than <see cref="NifSkinWriter.MaxBonesPerPartition"/>
    /// bones cannot be drawn. Meshes that exceed it have to be divided until each
    /// piece fits.
    ///
    /// No fixture in the corpus has that many bones — nifly's are two-bone
    /// cylinders — so the mesh here is synthetic: one triangle per bone, which
    /// makes the expected split arithmetic rather than a matter of inspection.
    /// </remarks>
    public class MultiPartitionTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>A mesh of <paramref name="boneCount"/> disjoint triangles, one per bone.</summary>
        private sealed class Fixture
        {
            public required NifModel Model { get; init; }
            public required NifItem Shape { get; init; }
            public required NifItem Partition { get; init; }
            public required List<NifTriangle> Triangles { get; init; }
            public required int VertexCount { get; init; }

            public List<NifItem> Partitions =>
                Model.FindItem(Partition, "Partitions")?.Children.ToList() ?? [];

            public uint Count(NifItem item, string field) => Model.GetUInt(item, field);

            public List<uint> Values(NifItem parent, string field) =>
                Model.FindItem(parent, field)?.Children.Select(c => c.Value.ToUInt()).ToList() ?? [];
        }

        /// <summary>
        /// Builds a skin whose every triangle needs exactly one bone, so the number
        /// of partitions follows directly from the bone count.
        /// </summary>
        private static Fixture Build(int boneCount, bool legendary = false)
        {
            NifModel model = NifModel.CreateNew(Db, bsVersion: legendary ? 83u : 100u);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem shape = model.InsertBlock(legendary ? "NiTriShape" : "BSTriShape");
            model.SetString(shape, "Name", "mesh");

            var skin = new SkinData();
            var nodes = new Dictionary<string, NifItem>(StringComparer.Ordinal);
            var triangles = new List<NifTriangle>();

            for (int i = 0; i < boneCount; i++)
            {
                string name = $"Bone{i}";

                NifItem node = model.InsertBlock("NiNode");
                model.SetString(node, "Name", name);
                nodes[name] = node;

                var bone = new SkinBone { Name = name };
                var triangle = new NifTriangle((ushort)(i * 3), (ushort)(i * 3 + 1), (ushort)(i * 3 + 2));

                // Weighting every vertex of the triangle wholly to this bone is
                // what makes each triangle cost exactly one palette slot.
                bone.Weights.Add((triangle.V1, 1f));
                bone.Weights.Add((triangle.V2, 1f));
                bone.Weights.Add((triangle.V3, 1f));

                skin.Bones.Add(bone);
                triangles.Add(triangle);
            }

            // Saving keeps only what the roots reach, so the shape and the bones
            // have to hang off the root node to survive a round trip.
            var children = new List<NifItem> { shape };
            children.AddRange(nodes.Values);

            if (model.SetArraySize(root, "Num Children", "Children", children.Count) is { } array)
            {
                for (int i = 0; i < children.Count && i < array.Children.Count; i++)
                    array.Children[i].Value.SetLink(model.IndexOf(children[i]));
            }

            int vertexCount = boneCount * 3;

            var missing = model.WriteSkin(shape, skin, nodes, root, vertexCount, triangles);
            Assert.Empty(missing);

            NifItem instance = model.Blocks.First(b => b.Name == "BSDismemberSkinInstance");

            return new Fixture
            {
                Model = model,
                Shape = shape,
                Partition = model.GetRef(instance, "Skin Partition")!,
                Triangles = triangles,
                VertexCount = vertexCount
            };
        }

        [Fact]
        public void MeshWithinTheBonePaletteStaysWhole()
        {
            Fixture fixture = Build(NifSkinWriter.MaxBonesPerPartition);

            // Splitting a mesh that already fits would cost vertices at the seams
            // for no gain, so the whole mesh has to stay in one piece.
            Assert.Single(fixture.Partitions);
            Assert.Equal(1u, fixture.Count(fixture.Partition, "Num Partitions"));
        }

        [Fact]
        public void MeshBeyondTheBonePaletteIsSplit()
        {
            Fixture fixture = Build(NifSkinWriter.MaxBonesPerPartition + 1);

            Assert.Equal(2, fixture.Partitions.Count);
            Assert.Equal(2u, fixture.Count(fixture.Partition, "Num Partitions"));
        }

        [Fact]
        public void NoPartitionOverflowsTheBonePalette()
        {
            Fixture fixture = Build(200);

            Assert.All(fixture.Partitions, part =>
            {
                uint bones = fixture.Count(part, "Num Bones");

                // Going over is the whole failure this splitting exists to avoid:
                // the shader silently reads past its palette.
                Assert.InRange(bones, 1u, (uint)NifSkinWriter.MaxBonesPerPartition);
                Assert.Equal((int)bones, fixture.Values(part, "Bones").Count);
            });
        }

        [Fact]
        public void EveryTriangleLandsInExactlyOnePartition()
        {
            Fixture fixture = Build(200);

            var seen = new List<NifTriangle>();

            foreach (NifItem part in fixture.Partitions)
            {
                var map = fixture.Values(part, "Vertex Map").Select(v => (ushort)v).ToList();

                Assert.Equal((int)fixture.Count(part, "Num Vertices"), map.Count);

                foreach (NifItem item in fixture.Model.FindItem(part, "Triangles")!.Children)
                {
                    NifTriangle triangle = item.Value.Get<NifTriangle>();

                    // A partition's triangles are in the shape's own numbering, not the
                    // partition's. This used to put them through the vertex map, on the
                    // reading that they were local to the partition -- and nif.xml says
                    // what the map is for in as many words: it "maps the weight/influence
                    // lists in this submesh to the vertices in the shape being skinned".
                    // The weights, not the faces.
                    //
                    // Vanilla settles it. `0000282d`'s first partition maps 108 vertices
                    // and its triangles reach index 878 of the shape's 996; `hair13`'s
                    // reach 963 of 964. Neither is a local index into a map that small.
                    //
                    // Every vertex a triangle names is still one this partition lists,
                    // which is what the map is checked for below.
                    Assert.Contains(triangle.V1, map);
                    Assert.Contains(triangle.V2, map);
                    Assert.Contains(triangle.V3, map);

                    seen.Add(triangle);
                }
            }

            Assert.Equal(
                fixture.Triangles.OrderBy(t => t.V1).ToList(),
                seen.OrderBy(t => t.V1).ToList());
        }

        [Fact]
        public void PartitionsListOnlyTheVerticesTheyUse()
        {
            Fixture fixture = Build(200);

            var all = new List<uint>();

            foreach (NifItem part in fixture.Partitions)
            {
                var map = fixture.Values(part, "Vertex Map");

                // A partition carrying vertices it never draws wastes both palette
                // slots and skinning work.
                Assert.Equal(map.Count, map.Distinct().Count());
                all.AddRange(map);
            }

            // Between them the partitions still have to cover the whole mesh.
            Assert.Equal(fixture.VertexCount, all.Distinct().Count());
        }

        [Fact]
        public void BoneIndicesAreLocalToTheirPartition()
        {
            Fixture fixture = Build(200);

            foreach (NifItem part in fixture.Partitions)
            {
                var bones = fixture.Values(part, "Bones");
                var map = fixture.Values(part, "Vertex Map");

                NifItem weights = fixture.Model.FindItem(part, "Vertex Weights")!;
                NifItem indices = fixture.Model.FindItem(part, "Bone Indices")!;

                for (int v = 0; v < map.Count; v++)
                {
                    for (int slot = 0; slot < NifSkinWriter.MaxInfluences; slot++)
                    {
                        float weight = weights.Children[v].Children[slot].Value.ToFloat();
                        uint index = indices.Children[v].Children[slot].Value.ToUInt();

                        // An index past the partition's own bone list points at
                        // whatever happens to follow it in the palette.
                        Assert.InRange(index, 0u, (uint)bones.Count - 1);

                        // Each vertex belongs wholly to the bone of its triangle.
                        if (slot == 0)
                            Assert.Equal(1f, weight, 5);
                        else
                            Assert.Equal(0f, weight, 5);
                    }

                    // ...and that bone has to be the one the fixture assigned.
                    uint bone = bones[(int)indices.Children[v].Children[0].Value.ToUInt()];
                    Assert.Equal(map[v] / 3, bone);
                }
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SplitSkinSurvivesSaveAndReload(bool legendary)
        {
            Fixture fixture = Build(200, legendary);

            fixture.Model.SetRoots([fixture.Model.Blocks[0]]);
            fixture.Model.UpdateHeader();

            using var stream = new MemoryStream();
            fixture.Model.Save(stream);
            stream.Position = 0;

            // Reading is sequential, so a partition written even one byte short
            // desynchronises every block after it rather than failing locally.
            NifModel reloaded = NifModel.Load(stream, Db);

            NifItem partition = reloaded.Blocks.First(b => b.Name == "NiSkinPartition");
            var parts = reloaded.FindItem(partition, "Partitions")!.Children;

            Assert.Equal(fixture.Partitions.Count, parts.Count);

            Assert.Equal(
                200,
                parts.Sum(p => (int)reloaded.GetUInt(p, "Num Triangles")));

            Assert.All(parts, p => Assert.InRange(
                reloaded.GetUInt(p, "Num Bones"), 1u, (uint)NifSkinWriter.MaxBonesPerPartition));
        }

        [Fact]
        public void SplitWeightsStillReadBack()
        {
            Fixture fixture = Build(200);

            // The skin data keeps the full, unsplit weights, so reading has to see
            // every bone regardless of how the partitions divided the mesh.
            SkinData? skin = fixture.Model.ReadSkin(fixture.Shape);

            Assert.NotNull(skin);
            Assert.Equal(200, skin!.Bones.Count);
            Assert.All(skin.Bones, b => Assert.Equal(3, b.Weights.Count));
        }
    }
}
