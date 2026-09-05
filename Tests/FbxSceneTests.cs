using LeanMeshIO;
using SECmd.Fbx;
using Xunit;

namespace SECmd.Tests
{
    public class FbxSceneTests
    {
        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static FbxScene Load(string name) => new(FbxDocument.Load(PathTo(name)));

        [Fact]
        public void ReadsTheObjectPool()
        {
            FbxScene scene = Load("generate_rb_box_with_mesh.fbx");

            Assert.Equal(2, scene.OfClass("Geometry").Count());
            Assert.Equal(3, scene.OfClass("Model").Count());
            Assert.Single(scene.OfClass("Material"));
            Assert.Single(scene.OfClass("NodeAttribute"));
        }

        [Fact]
        public void SplitsQualifiedNames()
        {
            FbxScene scene = Load("generate_rb_box_with_mesh.fbx");

            FbxObject model = scene.OfClass("Model").First(o => o.QualifiedName == "Model::_rb_box");

            Assert.Equal("_rb_box", model.Name);
            Assert.Equal("Mesh", model.SubClass);
        }

        [Fact]
        public void ReadsConnections()
        {
            FbxScene scene = Load("generate_rb_box_with_mesh.fbx");

            Assert.NotEmpty(scene.Connections);
            Assert.All(scene.Connections, c => Assert.Equal(FbxConnectionKind.ObjectObject, c.Kind));
        }

        [Fact]
        public void ResolvesTheModelHierarchy()
        {
            FbxScene scene = Load("generate_rb_box_with_mesh.fbx");

            // _rb is a root Null, with _rb_box parented under it.
            FbxObject rb = scene.OfClass("Model").First(o => o.Name == "_rb");
            FbxObject box = scene.OfClass("Model").First(o => o.Name == "_rb_box");

            Assert.Contains(scene.RootModels(), o => o.Id == rb.Id);
            Assert.Contains(scene.ChildrenOf(rb.Id), o => o.Id == box.Id);
            Assert.Contains(scene.ParentsOf(box.Id), o => o.Id == rb.Id);
        }

        [Fact]
        public void FindsTheGeometryAttachedToAModel()
        {
            FbxScene scene = Load("generate_rb_box_with_mesh.fbx");

            FbxObject box = scene.OfClass("Model").First(o => o.Name == "_rb_box");

            FbxObject? geometry = scene.ChildrenOf(box.Id).FirstOrDefault(o => o.Class == "Geometry");

            Assert.NotNull(geometry);
            Assert.Equal("Mesh", geometry!.SubClass);
            Assert.NotNull(geometry.Child("Vertices"));
        }

        [Fact]
        public void ReadsStandardTransformProperties()
        {
            FbxScene scene = Load("generate_rb_box_with_transform_mesh.fbx");

            // At least one model in this fixture is deliberately transformed.
            var translations = scene.OfClass("Model")
                .Select(m => m.Properties.GetVector3("Lcl Translation"))
                .ToList();

            Assert.Contains(translations, t => t.X != 0 || t.Y != 0 || t.Z != 0);
        }

        [Fact]
        public void RoundTripsThroughTheDocument()
        {
            FbxDocument document = FbxDocument.Load(PathTo("multi_material_cube.fbx"));
            var scene = new FbxScene(document);

            int objects = scene.Objects.Count;
            int connections = scene.Connections.Count;

            scene.Flush();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            Assert.Equal(objects, reloaded.Objects.Count);
            Assert.Equal(connections, reloaded.Connections.Count);
        }

        [Fact]
        public void CanBuildAndConnectNewObjects()
        {
            FbxDocument document = FbxDocument.Load(PathTo("generate_rb.fbx"));
            var scene = new FbxScene(document);

            FbxObject parent = scene.AddObject("Model", "TestRoot", "Null");
            FbxObject child = scene.AddObject("Model", "TestChild", "Mesh");

            scene.ConnectToRoot(parent);
            scene.Connect(child, parent);

            child.Properties.SetUserString("hkAnnotation", "collision");

            scene.Flush();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            FbxObject? reloadedChild = reloaded.OfClass("Model").FirstOrDefault(o => o.Name == "TestChild");

            Assert.NotNull(reloadedChild);
            Assert.Contains(reloaded.RootModels(), o => o.Name == "TestRoot");

            // The user-defined property has to survive, since that is how Havok data
            // rides through a scene.
            FbxProperty70? annotation = reloadedChild!.Properties.Find("hkAnnotation");

            Assert.NotNull(annotation);
            Assert.True(annotation!.Value.IsUserDefined);
            Assert.Equal("collision", annotation.Value.Values[0]);
        }

        [Fact]
        public void FlushKeepsDefinitionsCountsInStep()
        {
            FbxDocument document = FbxDocument.Load(PathTo("generate_rb.fbx"));
            var scene = new FbxScene(document);

            scene.AddObject("Model", "Extra", "Null");
            scene.Flush();

            var count = document["Definitions"]!.Nodes.First(n => n.Name == "Count");

            Assert.Equal(scene.Objects.Count, Convert.ToInt32(count.Properties[0]));
        }
    }
}
