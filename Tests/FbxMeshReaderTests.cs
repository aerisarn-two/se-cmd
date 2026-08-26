using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class FbxMeshReaderTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static FbxScene LoadScene(string name) => new(FbxDocument.Load(PathTo(name)));

        /// <summary>Converts a NIF and reads the geometry straight back out.</summary>
        private static (MeshGeometry Source, MeshGeometry Reloaded) RoundTrip(string nif, string shapeName)
        {
            NifModel model = NifModel.Load(PathTo(nif), Db);
            var converter = new NifToFbx(model);
            var scene = new FbxScene(converter.Convert());

            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == shapeName);
            MeshGeometry reloaded = FbxMeshReader.Read(geometry, new FbxMeshReader.Options())!;

            // The source, with the shape transform baked and V flipped, which is
            // what the writer emitted.
            NifItem shape = model.Blocks.First(b =>
                b.Name == "NiTriShape" && model.GetName(b) == shapeName);
            NifItem data = model.GetRef(shape, "Data")!;
            NifTransform transform = model.GetTransform(shape);

            var source = new MeshGeometry();

            foreach (NifVector3 v in model.GetVertices(data))
                source.Vertices.Add(transform.Apply(v));

            source.Triangles.AddRange(model.GetGeometryTriangles(data));

            return (source, reloaded);
        }

        /// <summary>Writes a mesh to a scene and reads it straight back.</summary>
        private static MeshGeometry Bounce(MeshGeometry mesh)
        {
            var scene = new FbxScene(new FbxDocument());
            FbxObject geometry = FbxMeshWriter.AddGeometry(scene, "probe", mesh);

            // Written in FBX convention and read back in the same one, so neither
            // flip is applied and the numbers are comparable.
            return FbxMeshReader.Read(
                geometry, new FbxMeshReader.Options { InvertU = false, InvertV = false })!;
        }

        [Fact]
        public void TwoVerticesThatDifferOnlyInTangentStayTwo()
        {
            // A vertex is told from another by comparing all eighteen numbers it
            // carries, and six of those are the tangent frame. Nothing wrote them, so
            // the comparison was really on fourteen and vertices the file said were
            // different looked identical and merged: `norpullchainanim01`'s gear catch
            // has 78 vertices of which only 46 differ in position, normal and texture
            // coordinate, and it came back as 46.
            var mesh = new MeshGeometry();

            for (int i = 0; i < 3; i++)
            {
                mesh.Vertices.Add(new NifVector3(i, 0f, 0f));
                mesh.Normals.Add(new NifVector3(0f, 0f, 1f));
                mesh.Uvs.Add(new NifVector2(0f, 0f));
                mesh.Tangents.Add(new NifVector3(1f, 0f, 0f));
                mesh.Bitangents.Add(new NifVector3(0f, 1f, 0f));
            }

            // A fourth in the same place as the first, facing the same way, with the
            // same texture coordinate, and a different tangent. Only the tangent
            // separates them.
            mesh.Vertices.Add(new NifVector3(0f, 0f, 0f));
            mesh.Normals.Add(new NifVector3(0f, 0f, 1f));
            mesh.Uvs.Add(new NifVector2(0f, 0f));
            mesh.Tangents.Add(new NifVector3(0f, 1f, 0f));
            mesh.Bitangents.Add(new NifVector3(-1f, 0f, 0f));

            mesh.Triangles.Add(new NifTriangle(0, 1, 2));
            mesh.Triangles.Add(new NifTriangle(3, 1, 2));

            MeshGeometry back = Bounce(mesh);

            Assert.Equal(4, back.Vertices.Count);
            Assert.Equal(4, back.Tangents.Count);
        }

        [Fact]
        public void AVertexNoTriangleUsesIsStillAVertex()
        {
            // The reader only ever reaches a control point through a polygon corner,
            // so a vertex nothing indexes was dropped without a word. The game's files
            // carry them: a prisoner's rags hold 1,303 vertices of which the triangles
            // name 1,084, and it came back as 1,084. They carry a position, a normal, a
            // texture coordinate and their own bone weights, and a file that had them
            // is not the file that comes back without them.
            var mesh = new MeshGeometry();

            for (int i = 0; i < 5; i++)
            {
                mesh.Vertices.Add(new NifVector3(i, 0f, 0f));
                mesh.Normals.Add(new NifVector3(0f, 0f, 1f));
                mesh.Uvs.Add(new NifVector2(i * 0.1f, 0f));
            }

            // Only three of the five are used.
            mesh.Triangles.Add(new NifTriangle(0, 1, 2));

            MeshGeometry back = Bounce(mesh);

            Assert.Equal(5, back.Vertices.Count);
            Assert.Single(back.Triangles);

            // And the ones the triangles use keep the numbering they had, so nothing
            // had to be renumbered to make room for the strays.
            Assert.Equal(0, back.Triangles[0].V1);
            Assert.Equal(1, back.Triangles[0].V2);
            Assert.Equal(2, back.Triangles[0].V3);
        }

        [Fact]
        public void ReadsBlenderExportedGeometry()
        {
            // These fixtures use ByPolygonVertex with IndexToDirect, the awkward
            // combination, so reading them exercises the resolution path.
            FbxScene scene = LoadScene("multi_material_cube.fbx");
            FbxObject geometry = scene.OfClass("Geometry").First();

            MeshGeometry? mesh = FbxMeshReader.Read(geometry);

            Assert.NotNull(mesh);
            Assert.NotEmpty(mesh!.Vertices);
            Assert.NotEmpty(mesh.Triangles);
            Assert.True(mesh.IsWellFormed(out string? problem), problem);
        }

        [Fact]
        public void TriangulatesQuads()
        {
            // Blender exports the cube as six quads; NIF needs twelve triangles.
            FbxScene scene = LoadScene("multi_material_cube.fbx");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube");

            MeshGeometry mesh = FbxMeshReader.Read(geometry)!;

            Assert.Equal(12, mesh.Triangles.Count);
        }

        [Fact]
        public void SplitsVerticesWhereAttributesDisagree()
        {
            // A cube has 8 corners geometrically, but its face normals differ at
            // every one, so each must be split into separate vertices.
            FbxScene scene = LoadScene("multi_material_cube.fbx");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube");

            var controlPoints = (double[])geometry.Child("Vertices")!.Properties[0]!;
            MeshGeometry mesh = FbxMeshReader.Read(geometry)!;

            Assert.Equal(8, controlPoints.Length / 3);
            Assert.True(mesh.Vertices.Count > 8,
                $"expected splitting at normal seams, got {mesh.Vertices.Count} vertices");
        }

        [Fact]
        public void AttributeArraysMatchTheVertexCount()
        {
            FbxScene scene = LoadScene("multi_material_cube.fbx");

            foreach (FbxObject geometry in scene.OfClass("Geometry"))
            {
                MeshGeometry mesh = FbxMeshReader.Read(geometry)!;

                Assert.True(mesh.IsWellFormed(out string? problem), $"{geometry.Name}: {problem}");
            }
        }

        [Fact]
        public void RoundTripsVertexPositions()
        {
            (MeshGeometry source, MeshGeometry reloaded) = RoundTrip("multi_material_cube.nif", "Cube_Material0");

            // Attributes are per-vertex on both sides here, so nothing should split.
            Assert.Equal(source.Vertices.Count, reloaded.Vertices.Count);
            Assert.Equal(source.Triangles.Count, reloaded.Triangles.Count);

            for (int i = 0; i < source.Vertices.Count; i++)
            {
                Assert.Equal(source.Vertices[i].X, reloaded.Vertices[i].X, 4);
                Assert.Equal(source.Vertices[i].Y, reloaded.Vertices[i].Y, 4);
                Assert.Equal(source.Vertices[i].Z, reloaded.Vertices[i].Z, 4);
            }
        }

        [Fact]
        public void RoundTripsTriangles()
        {
            (MeshGeometry source, MeshGeometry reloaded) = RoundTrip("multi_material_cube.nif", "Cube_Material0");

            for (int i = 0; i < source.Triangles.Count; i++)
            {
                Assert.Equal(source.Triangles[i].V1, reloaded.Triangles[i].V1);
                Assert.Equal(source.Triangles[i].V2, reloaded.Triangles[i].V2);
                Assert.Equal(source.Triangles[i].V3, reloaded.Triangles[i].V3);
            }
        }

        [Fact]
        public void RoundTripsUvsThroughBothFlips()
        {
            NifModel model = NifModel.Load(PathTo("multi_material_cube.nif"), Db);
            NifItem shape = model.Blocks.First(b =>
                b.Name == "NiTriShape" && model.GetName(b) == "Cube_Material0");
            var original = model.GetUvSet(model.GetRef(shape, "Data")!);

            if (original.Count == 0)
                return;

            (_, MeshGeometry reloaded) = RoundTrip("multi_material_cube.nif", "Cube_Material0");

            // Flipped on the way out and back, so V returns to where it started.
            Assert.Equal(original.Count, reloaded.Uvs.Count);
            Assert.Equal(original[0].X, reloaded.Uvs[0].X, 4);
            Assert.Equal(original[0].Y, reloaded.Uvs[0].Y, 4);
        }

        [Fact]
        public void HonoursTheInvertOptions()
        {
            FbxScene scene = LoadScene("multi_material_cube.fbx");
            FbxObject geometry = scene.OfClass("Geometry").First(g => g.Name == "Cube");

            MeshGeometry flipped = FbxMeshReader.Read(geometry, new FbxMeshReader.Options { InvertV = true })!;
            MeshGeometry plain = FbxMeshReader.Read(geometry, new FbxMeshReader.Options { InvertV = false })!;

            if (flipped.Uvs.Count == 0)
                return;

            Assert.Equal(1f - plain.Uvs[0].Y, flipped.Uvs[0].Y, 5);
            Assert.Equal(plain.Uvs[0].X, flipped.Uvs[0].X, 5);
        }

        [Fact]
        public void ReturnsNullForAGeometryWithoutVertices()
        {
            var scene = new FbxScene(FbxDocumentTemplate.CreateEmpty());
            FbxObject empty = scene.AddObject("Geometry", "Empty", "Mesh");

            Assert.Null(FbxMeshReader.Read(empty));
        }

        [Fact]
        public void RecalculatesNormalsWhenAbsent()
        {
            var mesh = new MeshGeometry();
            mesh.Vertices.Add(new NifVector3(0, 0, 0));
            mesh.Vertices.Add(new NifVector3(1, 0, 0));
            mesh.Vertices.Add(new NifVector3(0, 1, 0));
            mesh.Triangles.Add(new NifTriangle(0, 1, 2));

            mesh.RecalculateNormals();

            // A triangle in the XY plane faces +Z.
            Assert.Equal(3, mesh.Normals.Count);
            Assert.All(mesh.Normals, n =>
            {
                Assert.Equal(0f, n.X, 4);
                Assert.Equal(0f, n.Y, 4);
                Assert.Equal(1f, n.Z, 4);
            });
        }

        [Fact]
        public void ComputesABoundingSphereContainingEveryVertex()
        {
            FbxScene scene = LoadScene("multi_material_cube.fbx");
            MeshGeometry mesh = FbxMeshReader.Read(scene.OfClass("Geometry").First(g => g.Name == "Cube"))!;

            (NifVector3 center, float radius) = mesh.ComputeBoundingSphere();

            Assert.All(mesh.Vertices, v =>
            {
                float dx = v.X - center.X, dy = v.Y - center.Y, dz = v.Z - center.Z;
                float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

                // Approximate, so allow a hair of slack rather than demanding exact.
                Assert.True(distance <= radius * 1.001f + 1e-4f,
                    $"vertex at {distance:G6} lies outside radius {radius:G6}");
            });
        }
    }
}
