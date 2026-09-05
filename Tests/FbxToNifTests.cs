using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class FbxToNifTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        /// <summary>Converts an FBX fixture to a NIF, then saves and reloads it.</summary>
        private static NifModel FromFbx(string name, out List<string> warnings)
        {
            var scene = new FbxScene(FbxDocument.Load(PathTo(name)));
            // These fixtures and assertions are about NiTriShape geometry, so they
            // target LE explicitly. SE emits BSTriShape and is covered separately.
            var converter = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = Path.GetFileNameWithoutExtension(name),
                LegendaryEdition = true
            });

            NifModel model = converter.Convert(Db);
            warnings = converter.Warnings;

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        /// <summary>
        /// An FBX authored elsewhere, with normals and UVs but no tangent layer, has
        /// its tangent frame completed on the way in.
        /// </summary>
        /// <remarks>
        /// Most DCC exporters write no tangent layer at all. A shape that reaches the
        /// game without one renders unlit under a normal map, which is what the frame
        /// is in the vertex buffer for, so a mesh that arrives without one and says
        /// nothing about why gets a frame built from its positions, normals and UVs.
        /// </remarks>
        [Fact]
        public void AnFbxWithNoTangentsHasItsFrameCompleted()
        {
            var scene = new FbxScene(FbxDocument.Load(PathTo("multi_material_cube.fbx")));

            // The fixture is the case under test: normals and UVs, no tangents.
            foreach (FbxObject geometry in scene.OfClass("Geometry"))
            {
                if (FbxMeshReader.Read(geometry) is not { } mesh)
                    continue;

                Assert.True(mesh.HasNormals);
                Assert.True(mesh.HasUvs);
                Assert.False(mesh.HasTangents);
            }

            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            var datas = model.Blocks.Where(b => b.Name == "NiTriShapeData").ToList();
            Assert.NotEmpty(datas);

            foreach (NifItem data in datas)
            {
                List<NifVector3> tangents = model.GetTangents(data);
                List<NifVector3> normals = model.GetNormals(data);

                Assert.NotEmpty(tangents);
                Assert.Equal(model.GetVertices(data).Count, tangents.Count);
                Assert.Equal(tangents.Count, model.GetBitangents(data).Count);

                // A frame, not a filler: every vector unit length and perpendicular to
                // its normal, which is what a normal map is sampled against.
                for (int i = 0; i < tangents.Count; i++)
                {
                    NifVector3 t = tangents[i], n = normals[i];

                    Assert.Equal(1f, MathF.Sqrt(t.X * t.X + t.Y * t.Y + t.Z * t.Z), 3);
                    Assert.Equal(0f, t.X * n.X + t.Y * n.Y + t.Z * n.Z, 3);
                }
            }
        }

        /// <summary>
        /// A shape whose NIF had no tangent frame does not gain one on a round trip.
        /// </summary>
        /// <remarks>
        /// The same FBX comes out of a NIF that never had tangents as out of a DCC mesh
        /// that never had them, so the fact has to travel on the geometry. Getting this
        /// wrong is not cosmetic: `Bitangent X` and `Unused W` share a slot, chosen by
        /// the Tangents flag, so a shape that gains a frame loses that word and every
        /// offset in `Vertex Desc` after it moves along.
        /// </remarks>
        [Fact]
        public void AShapeThatHadNoTangentsDoesNotGainThem()
        {
            NifModel source = NifModel.Load(PathTo("nifly/TestNifFile_Static_SE.nif"), Db);

            var shapes = source.Blocks
                .Where(b => b.Name is "BSTriShape" or "NiTriShapeData")
                .ToList();

            Assert.NotEmpty(shapes);

            FbxDocument document = new NifToFbx(source).Convert();

            // The marker the exporter leaves, which is what the importer reads.
            var scene = new FbxScene(document);

            NifModel rebuilt = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = "static",
                Version = source.Version,
                UserVersion = source.UserVersion,
                LegendaryEdition = source.BSVersion < 100
            }).Convert(Db);

            // Whatever the source said about tangents, the rebuild says the same.
            Assert.Equal(TangentFlags(source), TangentFlags(rebuilt));
        }

        /// <summary>Which of a model's shapes announce a tangent frame.</summary>
        private static List<bool> TangentFlags(NifModel model)
        {
            var flags = new List<bool>();

            foreach (NifItem shape in model.Blocks)
            {
                if (model.FindItem(shape, "Vertex Desc") is not { } desc)
                    continue;

                ulong attributes = (desc.Value.ToUInt64() >> BSVertexDesc.Member.VertexAttributes) & 0xFFF;
                flags.Add(((VertexFlags)attributes & VertexFlags.Tangent) != 0);
            }

            return flags;
        }

        /// <summary>NIF to FBX and back, the full round trip.</summary>
        private static NifModel RoundTrip(string nif)
        {
            NifModel source = NifModel.Load(PathTo(nif), Db);
            FbxDocument document = new NifToFbx(source).Convert();

            var converter = new FbxToNif(
                new FbxScene(document),
                new FbxToNifOptions { RootName = Path.GetFileNameWithoutExtension(nif), LegendaryEdition = true });

            NifModel rebuilt = converter.Convert(Db);

            using var stream = new MemoryStream();
            rebuilt.Save(stream);
            stream.Position = 0;

            return NifModel.Load(stream, Db);
        }

        public static TheoryData<string> FbxFiles() =>
        [
            "generate_rb_box_with_mesh.fbx",
            "generate_rb_box_with_transform_mesh.fbx",
            "multi_material_cube.fbx"
        ];

        [Theory]
        [MemberData(nameof(FbxFiles))]
        public void ProducesALoadableNif(string name)
        {
            NifModel model = FromFbx(name, out _);

            Assert.NotEmpty(model.Blocks);
            Assert.Equal("BSFadeNode", model.Blocks[0].Name);
            Assert.Empty(model.Warnings);
        }

        [Fact]
        public void NamesTheRootAfterTheFile()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            Assert.Equal("multi_material_cube", model.GetName(model.Blocks[0]));
        }

        [Fact]
        public void BuildsGeometry()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            var shapes = model.Blocks.Where(b => b.Name == "NiTriShape").ToList();
            Assert.NotEmpty(shapes);

            foreach (NifItem shape in shapes)
            {
                NifItem? data = model.GetRef(shape, "Data");
                Assert.NotNull(data);

                var vertices = model.GetVertices(data!);
                var triangles = model.GetGeometryTriangles(data!);

                Assert.NotEmpty(vertices);
                Assert.NotEmpty(triangles);

                // A NIF with an out-of-range index crashes the game, so this is the
                // assertion that actually matters.
                Assert.All(triangles, t =>
                {
                    Assert.InRange(t.V1, 0, vertices.Count - 1);
                    Assert.InRange(t.V2, 0, vertices.Count - 1);
                    Assert.InRange(t.V3, 0, vertices.Count - 1);
                });
            }
        }

        [Fact]
        public void WritesNormalsAndABoundingSphere()
        {
            NifModel model = FromFbx("multi_material_cube.fbx", out _);

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");
            NifItem data = model.GetRef(shape, "Data")!;

            var vertices = model.GetVertices(data);
            var normals = model.GetNormals(data);

            Assert.Equal(vertices.Count, normals.Count);

            float radius = model.FindItem(data, @"Bounding Sphere\Radius")!.Value.ToFloat();
            Assert.True(radius > 0, "a non-empty mesh must have a positive bounding radius");
        }

        [Fact]
        public void BuildsCollisionFromRigidBodyNodes()
        {
            // The _rb suffix marks a rigid body, which is rebuilt rather than
            // becoming an ordinary node.
            NifModel model = FromFbx("generate_rb_box_with_mesh.fbx", out _);

            Assert.Contains(model.Blocks, b => b.Name == "bhkCollisionObject");
            Assert.DoesNotContain(model.Blocks, b => model.GetName(b).EndsWith("_rb", StringComparison.Ordinal));
        }

        // --- full round trip --------------------------------------------------

        [Fact]
        public void RoundTripPreservesShapeCount()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            int before = source.Blocks.Count(b => b.Name == "NiTriShape");
            int after = rebuilt.Blocks.Count(b => b.Name == "NiTriShape");

            Assert.Equal(before, after);
        }

        [Fact]
        public void RoundTripPreservesGeometry()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem sourceShape = source.Blocks.First(b =>
                b.Name == "NiTriShape" && source.GetName(b) == "Cube_Material0");
            NifItem rebuiltShape = rebuilt.Blocks.First(b =>
                b.Name == "NiTriShape" && rebuilt.GetName(b) == "Cube_Material0");

            var sourceData = source.GetRef(sourceShape, "Data")!;
            var rebuiltData = rebuilt.GetRef(rebuiltShape, "Data")!;

            var sourceVertices = source.GetVertices(sourceData);
            var rebuiltVertices = rebuilt.GetVertices(rebuiltData);

            Assert.Equal(sourceVertices.Count, rebuiltVertices.Count);
            Assert.Equal(
                source.GetGeometryTriangles(sourceData).Count,
                rebuilt.GetGeometryTriangles(rebuiltData).Count);

            // The shape transform was baked into the vertices on the way out, so
            // positions come back in the parent's space rather than the shape's.
            NifTransform transform = source.GetTransform(sourceShape);

            for (int i = 0; i < sourceVertices.Count; i++)
            {
                NifVector3 expected = transform.Apply(sourceVertices[i]);

                Assert.Equal(expected.X, rebuiltVertices[i].X, 3);
                Assert.Equal(expected.Y, rebuiltVertices[i].Y, 3);
                Assert.Equal(expected.Z, rebuiltVertices[i].Z, 3);
            }
        }

        [Fact]
        public void RoundTripPreservesUvs()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);

            NifItem sourceShape = source.Blocks.First(b =>
                b.Name == "NiTriShape" && source.GetName(b) == "Cube_Material0");
            var sourceUvs = source.GetUvSet(source.GetRef(sourceShape, "Data")!);

            if (sourceUvs.Count == 0)
                return;

            NifModel rebuilt = RoundTrip("multi_material_cube.nif");
            NifItem rebuiltShape = rebuilt.Blocks.First(b =>
                b.Name == "NiTriShape" && rebuilt.GetName(b) == "Cube_Material0");
            var rebuiltUvs = rebuilt.GetUvSet(rebuilt.GetRef(rebuiltShape, "Data")!);

            Assert.Equal(sourceUvs.Count, rebuiltUvs.Count);

            // Flipped out and back, so V lands where it started.
            for (int i = 0; i < sourceUvs.Count; i++)
            {
                Assert.Equal(sourceUvs[i].X, rebuiltUvs[i].X, 3);
                Assert.Equal(sourceUvs[i].Y, rebuiltUvs[i].Y, 3);
            }
        }

        [Fact]
        public void RoundTripPreservesTheMaterial()
        {
            NifModel source = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem sourceShader = source.GetRef(
                source.Blocks.First(b => b.Name == "NiTriShape"), "Shader Property")!;
            NifItem rebuiltShader = rebuilt.GetRef(
                rebuilt.Blocks.First(b => b.Name == "NiTriShape"), "Shader Property")!;

            Assert.Equal(
                source.FindItem(sourceShader, "Glossiness")!.Value.ToFloat(),
                rebuilt.FindItem(rebuiltShader, "Glossiness")!.Value.ToFloat(), 2);

            // Scaled to 0..1 for FBX and back to 0..999 for NIF.
            Assert.Equal(
                source.FindItem(sourceShader, "Specular Strength")!.Value.ToFloat(),
                rebuilt.FindItem(rebuiltShader, "Specular Strength")!.Value.ToFloat(), 1);
        }

        [Fact]
        public void RoundTripKeepsTheHierarchy()
        {
            NifModel rebuilt = RoundTrip("multi_material_cube.nif");

            NifItem root = rebuilt.Blocks[0];
            Assert.Equal("BSFadeNode", root.Name);

            var names = rebuilt.GetChildren(root).Select(rebuilt.GetName).ToList();

            Assert.Contains("Cube", names);
            Assert.Contains("Light", names);
            Assert.Contains("Camera", names);
        }
    }
}
