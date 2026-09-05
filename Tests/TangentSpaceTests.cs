using NIFSharp;
using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Tangent space generation, ported from NifSkope's <c>spTangentSpace</c>.
    /// </summary>
    /// <remarks>
    /// The strongest of these is <see cref="ReproducesTheTangentsInTheFile"/>: the
    /// generated vectors are compared against the ones a shipped file already holds,
    /// which is what says this is the same algorithm rather than merely a plausible
    /// one. Normal maps are read in tangent space, so a wrong frame is a surface lit
    /// from the wrong direction — visible, but as a texture problem rather than a
    /// geometry one.
    /// </remarks>
    public class TangentSpaceTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>The mesh out of a fixture's <c>NiTriShapeData</c>.</summary>
        private static (MeshGeometry Mesh, List<NifVector3> Tangents, List<NifVector3> Bitangents) FromFixture()
        {
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "generate_rb_box.nif"), Db);

            NifItem data = model.Blocks.First(b => b.Name == "NiTriShapeData");

            var mesh = new MeshGeometry();

            mesh.Vertices.AddRange(Vectors(model, data, "Vertices"));
            mesh.Normals.AddRange(Vectors(model, data, "Normals"));

            NifItem set0 = model.FindItem(data, "UV Sets")!.Children[0];
            mesh.Uvs.AddRange(set0.Children.Select(c => c.Value.Get<NifVector2>()));

            mesh.Triangles.AddRange(
                model.FindItem(data, "Triangles")!.Children.Select(c => c.Value.Get<NifTriangle>()));

            return (mesh, Vectors(model, data, "Tangents"), Vectors(model, data, "Bitangents"));
        }

        private static List<NifVector3> Vectors(NifModel model, NifItem data, string field) =>
            model.FindItem(data, field)!.Children.Select(c => c.Value.Get<NifVector3>()).ToList();

        [Fact]
        public void ReproducesTheTangentsInTheFile()
        {
            (MeshGeometry mesh, List<NifVector3> tangents, List<NifVector3> bitangents) = FromFixture();

            Assert.NotEmpty(tangents);
            Assert.True(TangentSpace.Generate(mesh));

            for (int i = 0; i < tangents.Count; i++)
            {
                Assert.Equal(tangents[i].X, mesh.Tangents[i].X, 4);
                Assert.Equal(tangents[i].Y, mesh.Tangents[i].Y, 4);
                Assert.Equal(tangents[i].Z, mesh.Tangents[i].Z, 4);

                Assert.Equal(bitangents[i].X, mesh.Bitangents[i].X, 4);
                Assert.Equal(bitangents[i].Y, mesh.Bitangents[i].Y, 4);
                Assert.Equal(bitangents[i].Z, mesh.Bitangents[i].Z, 4);
            }
        }

        [Fact]
        public void TheFrameIsOrthonormal()
        {
            (MeshGeometry mesh, _, _) = FromFixture();

            Assert.True(TangentSpace.Generate(mesh));

            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                NifVector3 n = mesh.Normals[i], t = mesh.Tangents[i], b = mesh.Bitangents[i];

                Assert.Equal(1f, Length(t), 3);
                Assert.Equal(1f, Length(b), 3);

                // The bitangent is orthogonalised against the normal and the tangent
                // both, rather than taken as their cross product.
                Assert.Equal(0f, Dot(n, t), 3);
                Assert.Equal(0f, Dot(n, b), 3);
                Assert.Equal(0f, Dot(t, b), 3);
            }
        }

        [Fact]
        public void ADegenerateUvTriangleDoesNotBlowUp()
        {
            // The textbook algorithm divides by the UV determinant, which is zero when
            // a triangle's UVs are collinear. NifSkope takes only its sign, so this is
            // a finite frame rather than an infinity.
            var mesh = new MeshGeometry();

            mesh.Vertices.AddRange([new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)]);
            mesh.Normals.AddRange([new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)]);
            mesh.Uvs.AddRange([new(0, 0), new(1, 1), new(2, 2)]);
            mesh.Triangles.Add(new NifTriangle(0, 1, 2));

            Assert.True(TangentSpace.Generate(mesh));

            foreach (NifVector3 t in mesh.Tangents)
                Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z), $"{t}");
        }

        [Fact]
        public void AVertexNoTriangleTouchesStillGetsAFrame()
        {
            var mesh = new MeshGeometry();

            mesh.Vertices.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(9, 9, 9)]);
            mesh.Normals.AddRange([new(0, 0, 1), new(0, 0, 1), new(0, 0, 1), new(0, 1, 0)]);
            mesh.Uvs.AddRange([new(0, 0), new(1, 0), new(0, 1), new(0, 0)]);
            mesh.Triangles.Add(new NifTriangle(0, 1, 2));

            Assert.True(TangentSpace.Generate(mesh));

            // The orphan gets an arbitrary but stable frame; a zero vector would be a
            // division by zero in whatever reads it.
            Assert.Equal(1f, Length(mesh.Tangents[3]), 3);
            Assert.Equal(0f, Dot(mesh.Normals[3], mesh.Tangents[3]), 3);
        }

        [Fact]
        public void WithoutUvsThereIsNothingToGenerateFrom()
        {
            // Tangent space is defined by the UV layout, so a mesh without one has no
            // frame to compute rather than a default one.
            var mesh = new MeshGeometry();

            mesh.Vertices.AddRange([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)]);
            mesh.Normals.AddRange([new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)]);
            mesh.Triangles.Add(new NifTriangle(0, 1, 2));

            Assert.False(TangentSpace.Generate(mesh));
            Assert.Empty(mesh.Tangents);
        }

        private static float Dot(NifVector3 a, NifVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static float Length(NifVector3 v) => MathF.Sqrt(Dot(v, v));
    }
}
