using LeanMeshIO.Formats;
using LeanMeshIO;
using SECmd.Fbx;
using Xunit;

namespace SECmd.Tests
{
    public class FbxDocumentTests
    {
        public static TheoryData<string> FbxFiles() =>
        [
            "generate_rb.fbx",
            "generate_rb_box.fbx",
            "generate_rb_sphere.fbx",
            "generate_rb_box_with_mesh.fbx",
            "generate_rb_box_with_transform_mesh.fbx",
            "multi_material_cube.fbx"
        ];

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        [Theory]
        [MemberData(nameof(FbxFiles))]
        public void LoadsTheFixture(string name)
        {
            FbxDocument document = FbxDocument.Load(PathTo(name));

            Assert.NotEmpty(document.Nodes);
        }

        [Theory]
        [MemberData(nameof(FbxFiles))]
        public void ReadsTheStandardTopLevelRecords(string name)
        {
            FbxDocument document = FbxDocument.Load(PathTo(name));

            // Every FBX 7.x file has these, and they are what the semantic layer
            // will hang off.
            Assert.NotNull(document["FBXHeaderExtension"]);
            Assert.NotNull(document["Definitions"]);
            Assert.NotNull(document["Objects"]);
            Assert.NotNull(document["Connections"]);
        }

        [Theory]
        [MemberData(nameof(FbxFiles))]
        public void SurvivesAReadWriteReadCycle(string name)
        {
            FbxDocument original = FbxDocument.Load(PathTo(name));

            using var stream = new MemoryStream();
            original.Save(stream);
            stream.Position = 0;

            FbxDocument reloaded = FbxDocument.Load(stream);

            Assert.Equal(original.Version, reloaded.Version);
            Assert.Equal(
                original.Nodes.Select(n => n.Name),
                reloaded.Nodes.Select(n => n.Name));

            // The object graph is what carries the actual model, so check it
            // survived rather than just the top-level record names.
            int originalObjects = original["Objects"]!.Nodes.Count;
            int reloadedObjects = reloaded["Objects"]!.Nodes.Count;

            Assert.Equal(originalObjects, reloadedObjects);
        }

        [Fact]
        public void RefusesToWriteAscii()
        {
            // MeshIO's ASCII writer emits booleans as a raw \x01 escape, which is
            // not valid FBX ASCII and which its own parser rejects. Refusing beats
            // producing a file that nothing can open.
            FbxDocument original = FbxDocument.Load(PathTo("generate_rb_box.fbx"));

            using var stream = new MemoryStream();

            Assert.Throws<NotSupportedException>(() => original.Save(stream, ContentType.ASCII));
        }

        [Fact]
        public void DetectsBinaryContent()
        {
            FbxDocument document = FbxDocument.Load(PathTo("generate_rb_box.fbx"));

            Assert.Equal(ContentType.Binary, document.ContentType);
        }

        [Fact]
        public void ReadsObjectRecordsWithTheirProperties()
        {
            FbxDocument document = FbxDocument.Load(PathTo("multi_material_cube.fbx"));

            var objects = document["Objects"]!;

            // An FBX object record is "Name*N { ... }" with id, name and subclass as
            // its first three properties.
            var geometry = objects.Nodes.FirstOrDefault(n => n.Name == "Geometry");

            Assert.NotNull(geometry);
            Assert.True(geometry!.Properties.Count >= 3);

            // Vertices and polygon indices are the payload we will need.
            Assert.NotNull(geometry.Nodes.FirstOrDefault(n => n.Name == "Vertices"));
            Assert.NotNull(geometry.Nodes.FirstOrDefault(n => n.Name == "PolygonVertexIndex"));
        }
    }
}
