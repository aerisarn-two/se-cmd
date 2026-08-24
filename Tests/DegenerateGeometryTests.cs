using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Geometry that exports to nothing, or to a single point.
    /// </summary>
    /// <remarks>
    /// This exists because of a bug that hid from everything else. A
    /// `BSDynamicTriShape` keeps its positions in a second buffer and leaves the static
    /// entries at zero, and reading those exported 136 vertices all at the origin — the
    /// whole mesh collapsed onto a point, with the vertex count, the triangle count and
    /// every block in the file correct.
    ///
    /// Nothing that counts things can see that. What catches it is asking whether the
    /// vertices are anywhere, which is what these do.
    ///
    /// The collapse test is deliberately "all vertices are the same point" rather than
    /// "all vertices are zero": a shape whose data is missing collapses onto whatever
    /// its absent field defaults to, and that is not always the origin.
    /// </remarks>
    public class DegenerateGeometryTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        public static TheoryData<string> Fixtures()
        {
            var data = new TheoryData<string>();
            string root = Path.Combine(AppContext.BaseDirectory, "Resources");

            foreach (string path in Directory.GetFiles(root, "*.nif", SearchOption.AllDirectories))
            {
                if (FixtureFiles.IsFixture(path))
                    data.Add(Path.GetRelativePath(root, path));
            }

            return data;
        }

        /// <summary>The reason a mesh is degenerate, or null when it is not.</summary>
        /// <remarks>
        /// Shared with the corpus sweep, so the two ask exactly the same question.
        /// </remarks>
        public static string? Degenerate(FbxScene scene, bool reportNotANumber = true)
        {
            foreach (FbxObject geometry in scene.Objects.Where(o => o.Class == "Geometry"))
            {
                MeshGeometry? mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options());

                if (mesh is null || mesh.Vertices.Count == 0)
                    return $"{geometry.Name} exported no vertices";

                if (!IsCollapsed(mesh.Vertices))
                    continue;

                NifVector3 point = mesh.Vertices[0];

                // A mesh of NaN is the file's own data rather than a fault here: the
                // game ships a few, and they decode through the same path as the
                // shapes beside them that come out fine. A collapse onto a *finite*
                // point is the fault worth failing over.
                if (!reportNotANumber && float.IsNaN(point.X))
                    continue;

                return $"{geometry.Name} exported {mesh.Vertices.Count} vertices, "
                       + $"all at ({point.X}, {point.Y}, {point.Z})";
            }

            return null;
        }

        /// <summary>
        /// Whether every vertex sits in the same place.
        /// </summary>
        /// <remarks>
        /// Not "every vertex is zero": a shape whose data is missing collapses onto
        /// whatever its absent field defaults to, and that is not always the origin.
        /// A single vertex is not a collapse — there is nowhere else for it to be.
        /// </remarks>
        public static bool IsCollapsed(IReadOnlyList<NifVector3> vertices) =>
            vertices.Count > 1 && vertices.Distinct().Count() == 1;

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void NoFixtureExportsGeometryThatIsNowhere(string name)
        {
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            Assert.Null(Degenerate(scene));
        }

        [Fact]
        public void ACollapseIsSeenWhereverItIs()
        {
            // The sweep is only worth running if this catches a collapse anywhere, not
            // just one onto zero.
            Assert.True(IsCollapsed([new(7f, 7f, 7f), new(7f, 7f, 7f), new(7f, 7f, 7f)]));
            Assert.True(IsCollapsed([new(0f, 0f, 0f), new(0f, 0f, 0f)]));
        }

        [Fact]
        public void RealGeometryIsNotACollapse()
        {
            Assert.False(IsCollapsed([new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)]));

            // Two points that differ only in the last component still differ.
            Assert.False(IsCollapsed([new(1f, 2f, 3f), new(1f, 2f, 3.0001f)]));
        }

        [Fact]
        public void ASingleVertexIsNotACollapse()
        {
            // There is nowhere else for it to be, so calling it collapsed would report
            // every point helper in the corpus.
            Assert.False(IsCollapsed([new(5f, 5f, 5f)]));
        }

    }
}
