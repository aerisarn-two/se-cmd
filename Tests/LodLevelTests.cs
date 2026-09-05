using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Marking which triangles belong to which level of detail.
    /// </summary>
    /// <remarks>
    /// A <c>BSLODTriShape</c> holds one triangle list and three counts into it: the
    /// first <c>LOD0 Size</c> triangles are the near level, the next <c>LOD1 Size</c>
    /// the middle one, and so on. Carrying the three counts across (§5.2.4) reproduces
    /// a shape that was already a LOD shape and gives an author nothing whatsoever to
    /// edit — the levels are invisible in a DCC tool, and a face cannot be moved
    /// between them.
    ///
    /// So they also ride as a material per polygon, named <c>LOD0</c>, <c>LOD1</c> and
    /// <c>LOD2</c>: the one per-face channel every DCC tool shows and lets an artist
    /// reassign, and the same mechanism ck-cmd uses to carry collision materials
    /// (§4.8). Reassigning a face is then the whole of authoring a level.
    ///
    /// The two halves are the pair the rest of the port uses. The counts are exact and
    /// reproduce a file that was not touched; a marking that disagrees is an artist
    /// having said something, and wins.
    /// </remarks>
    public class LodLevelTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>
        /// A shape with the level split the game's own potato plant has.
        /// </summary>
        /// <remarks>
        /// `meshes/plants/florapotatoplant01.nif` splits sixty triangles 0/10/50, which
        /// is worth copying for one reason: its first level is empty. A file whose
        /// counts are a prefix of the list would hide an off-by-one that this does not.
        /// </remarks>
        private static NifModel Build(uint lod0 = 0, uint lod1 = 10, uint lod2 = 50)
        {
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem shape = model.InsertBlock("BSLODTriShape");
            model.SetString(shape, "Name", "plant");

            model.FindItem(shape, "LOD0 Size")!.Value.SetCount(lod0);
            model.FindItem(shape, "LOD1 Size")!.Value.SetCount(lod1);
            model.FindItem(shape, "LOD2 Size")!.Value.SetCount(lod2);

            int triangles = (int)(lod0 + lod1 + lod2);

            NifItem data = model.InsertBlock("NiTriShapeData");
            WriteMesh(model, data, triangles);
            model.SetRef(shape, "Data", data);

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(shape));

            return model;
        }

        /// <summary>Three vertices per triangle, so no triangle shares one.</summary>
        /// <remarks>
        /// Shared vertices would let the mesh reader weld corners together and change
        /// the triangle count, which is the thing under test.
        /// </remarks>
        private static void WriteMesh(NifModel model, NifItem data, int triangles)
        {
            int vertices = triangles * 3;

            model.FindItem(data, "Num Vertices")!.Value.SetCount((uint)vertices);
            model.FindItem(data, "Has Vertices")!.Value.SetCount(1);

            NifItem positions = model.FindItem(data, "Vertices")!;
            positions.InvalidateConditionsRecursive();
            model.UpdateArraySize(positions);

            for (int i = 0; i < vertices; i++)
                positions.Children[i].Value.Set(new NifVector3(i, i * 2, i * 3));

            model.FindItem(data, "Num Triangles")!.Value.SetCount((uint)triangles);
            model.FindItem(data, "Num Triangle Points")!.Value.SetCount((uint)(triangles * 3));
            model.FindItem(data, "Has Triangles")!.Value.SetCount(1);

            NifItem list = model.FindItem(data, "Triangles")!;
            list.InvalidateConditionsRecursive();
            model.UpdateArraySize(list);

            for (int i = 0; i < triangles; i++)
            {
                list.Children[i].Value.Set(
                    new NifTriangle((ushort)(i * 3), (ushort)(i * 3 + 1), (ushort)(i * 3 + 2)));
            }
        }

        private static NifModel Import(FbxScene scene) =>
            new FbxToNif(scene, new FbxToNifOptions { RootName = "root", Version = 0x14020007, UserVersion = 12 })
                .Convert(Db);

        private static uint[] Sizes(NifModel model)
        {
            NifItem shape = model.Blocks.First(b => b.Name == "BSLODTriShape");

            return
            [
                model.FindItem(shape, "LOD0 Size")!.Value.ToUInt(),
                model.FindItem(shape, "LOD1 Size")!.Value.ToUInt(),
                model.FindItem(shape, "LOD2 Size")!.Value.ToUInt()
            ];
        }

        /// <summary>The level each polygon of the one LOD mesh was marked with.</summary>
        private static List<int> Marking(FbxScene scene)
        {
            FbxObject geometry = scene.Objects
                .Where(o => o.Class == "Geometry")
                .First(o => FbxMeshReader.ReadPolygonMaterials(o) is not null);

            var names = scene.ChildrenOf(scene.ParentsOf(geometry.Id).First().Id)
                .Where(o => o.Class == "Material")
                .Select(o => o.Name)
                .ToList();

            return [.. FbxMeshReader.ReadPolygonMaterials(geometry)!
                .Select(at => names[at] switch
                {
                    "LOD0" => 0,
                    "LOD1" => 1,
                    "LOD2" => 2,
                    _ => -1
                })];
        }

        [Fact]
        public void EveryTriangleIsMarkedWithItsLevel()
        {
            var scene = new FbxScene(new NifToFbx(Build()).Convert());

            List<int> levels = Marking(scene);

            Assert.Equal(60, levels.Count);
            Assert.Equal(10, levels.Count(l => l == 1));
            Assert.Equal(50, levels.Count(l => l == 2));

            // The empty level is empty, not one triangle wide.
            Assert.DoesNotContain(0, levels);
        }

        [Fact]
        public void TheMarkingComesBackAsTheSameCounts()
        {
            var scene = new FbxScene(new NifToFbx(Build()).Convert());

            Assert.Equal([0u, 10u, 50u], Sizes(Import(scene)));
        }

        [Fact]
        public void MovingAFaceToAnotherLevelMovesIt()
        {
            var scene = new FbxScene(new NifToFbx(Build()).Convert());

            // What an artist does in a DCC tool: assign a different material slot to
            // some faces. Five of level two's fifty become level one's.
            FbxObject geometry = scene.Objects
                .Where(o => o.Class == "Geometry")
                .First(o => FbxMeshReader.ReadPolygonMaterials(o) is not null);

            var names = scene.ChildrenOf(scene.ParentsOf(geometry.Id).First().Id)
                .Where(o => o.Class == "Material")
                .Select(o => o.Name)
                .ToList();

            int lod1 = names.IndexOf("LOD1");
            int[] materials = (int[])geometry.Node.Nodes
                .First(n => n.Name == "LayerElementMaterial")
                .Nodes.First(n => n.Name == "Materials")
                .Properties[0];

            for (int i = 0, moved = 0; i < materials.Length && moved < 5; i++)
            {
                if (materials[i] == names.IndexOf("LOD2"))
                {
                    materials[i] = lod1;
                    moved++;
                }
            }

            // The counts that rode across still say 0/10/50, and the marking wins.
            Assert.Equal([0u, 15u, 45u], Sizes(Import(scene)));
        }

        [Fact]
        public void AMarkedTriangleEndsUpInsideItsLevelsRun()
        {
            // The counts are runs over one list, so a level is only what it says if
            // the triangles are grouped and the groups are in order. Marking the
            // *first* triangle as the last level has to move it to the end.
            var levels = new List<int> { 2, 1, 1, 0 };

            (List<int> order, int[] sizes) = FbxLodSizes.GroupByLevel(levels);

            Assert.Equal([1u, 2u, 1u], sizes.Select(s => (uint)s));
            Assert.Equal([3, 1, 2, 0], order);
        }

        [Fact]
        public void AnUnmarkedFaceKeepsItsPlaceRatherThanVanishing()
        {
            // A face left on the shape's own material belongs to no level. Dropping it
            // would delete geometry an artist can see, which is the worse of the two.
            (List<int> order, int[] sizes) = FbxLodSizes.GroupByLevel([0, -1, 1]);

            Assert.Equal([1u, 1u, 0u], sizes.Select(s => (uint)s));
            Assert.Equal([0, 2, 1], order);
        }

        [Fact]
        public void TheCountsSayWhichLevelEachTriangleIsIn()
        {
            NifModel model = Build(2, 3, 0);
            NifItem shape = model.Blocks.First(b => b.Name == "BSLODTriShape");

            Assert.Equal([0, 0, 1, 1, 1], FbxLodSizes.LevelPerTriangle(model, shape, 5));
        }

        [Fact]
        public void ATriangleTheCountsDoNotReachBelongsToTheLastLevelThatHasAny()
        {
            // A shape whose counts do not cover its list is a file that exists; the
            // stragglers have to land somewhere, and the last real level is where the
            // engine draws them.
            NifModel model = Build(1, 1, 0);
            NifItem shape = model.Blocks.First(b => b.Name == "BSLODTriShape");

            Assert.Equal([0, 1, 1, 1], FbxLodSizes.LevelPerTriangle(model, shape, 4));
        }

        [Fact]
        public void ALevelMarkerIsNeverShadedWith()
        {
            // A shape has one material and the import takes the first on the node.
            // The markers are materials too, and a DCC tool writes them in whatever
            // order it likes -- a shape whose shader came out named LOD0 is what
            // happens if they are not passed over.
            var scene = new FbxScene(new NifToFbx(Build()).Convert());

            FbxObject holder = scene.Objects
                .Where(o => o.Class == "Model")
                .First(o => scene.ChildrenOf(o.Id).Any(m => m.Name == "LOD1"));

            // This shape has no material of its own, so the markers are the only ones
            // on the node -- the sharpest form of the hazard.
            Assert.All(
                scene.ChildrenOf(holder.Id).Where(o => o.Class == "Material"),
                m => Assert.True(FbxLodSizes.IsLevelMaterial(m.Name)));

            NifModel rebuilt = Import(scene);
            NifItem shape = rebuilt.Blocks.First(b => b.Name == "BSLODTriShape");

            // No shader at all is the right answer. A shape shaded with LOD0 is not.
            Assert.Null(rebuilt.GetRef(shape, "Shader Property"));

            // The markers are not Havok materials either, so nothing warns about them.
            Assert.True(FbxLodSizes.IsLevelMaterial("LOD0"));
            Assert.True(FbxLodSizes.IsLevelMaterial("LOD2"));
            Assert.False(FbxLodSizes.IsLevelMaterial("SKY_HAV_MAT_WOOD"));
        }

        [Fact]
        public void AMeshWithNoLevelMaterialsIsNotMarked()
        {
            // Every other mesh in the scene has one material and an AllSame element.
            // Reading a level out of that would put every shape's triangles in level
            // zero and change files nobody touched.
            var scene = new FbxScene(new NifToFbx(Build()).Convert());

            foreach (FbxObject geometry in scene.Objects.Where(o => o.Class == "Geometry"))
            {
                if (FbxMeshReader.ReadPolygonMaterials(geometry) is null)
                    continue;

                var names = scene.ChildrenOf(scene.ParentsOf(geometry.Id).First().Id)
                    .Where(o => o.Class == "Material")
                    .Select(o => o.Name)
                    .ToList();

                Assert.Contains("LOD1", names);
            }
        }
    }
}
