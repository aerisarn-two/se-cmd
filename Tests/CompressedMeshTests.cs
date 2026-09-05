using NIFSharp;
using SECmd.Havok;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// mopper's <c>-ccm</c> mode, which returns a whole compressed mesh shape rather
    /// than just a MOPP tree.
    /// </summary>
    public class CompressedMeshTests
    {
        private static readonly NifVector3[] CubeVertices =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)
        ];

        private static readonly NifTriangle[] CubeTriangles =
        [
            new(0, 1, 2), new(0, 2, 3), new(4, 6, 5), new(4, 7, 6),
            new(0, 4, 5), new(0, 5, 1), new(1, 5, 6), new(1, 6, 2),
            new(2, 6, 7), new(2, 7, 3), new(3, 7, 4), new(3, 4, 0)
        ];

        private static MoppGeometry Cube => new(CubeVertices, CubeTriangles);

        [Fact]
        public void SerialisesTheGeometryListFormat()
        {
            string input = MopperProcessGenerator.BuildCompressedMeshInput([Cube]);
            string[] lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Geometry count, then per geometry a vertex block and a triangle block.
            Assert.Equal("1", lines[0]);
            Assert.Equal("8", lines[1]);
            Assert.Equal("0 0 0", lines[2]);
            Assert.Equal("12", lines[10]);
            Assert.Equal("0 1 2", lines[11]);
        }

        [Fact]
        public void SerialisesSeveralGeometries()
        {
            string input = MopperProcessGenerator.BuildCompressedMeshInput([Cube, Cube]);

            Assert.StartsWith("2\n", input);
        }

        [Fact]
        public void UndoesTheRotatedMoppCode()
        {
            // mopper prints the LAST code byte first, then bytes 0..n-2. Reading it
            // in order yields a rotated tree Havok will not accept.
            string output = string.Join('\n',
                "0", "0", "0",          // origin
                "1",                     // scale
                "3",                     // code length
                "99",                    // the last byte, printed first
                "10", "20",              // then bytes 0 and 1
                "0", "0", "0", "0",      // bounds min
                "1", "1", "1", "0",      // bounds max
                "0",                     // big vertices
                "0",                     // big triangles
                "0",                     // transforms
                "0");                    // chunks

            CompressedMeshResult? result = MopperProcessGenerator.ParseCompressedMeshOutput(output);

            Assert.NotNull(result);
            Assert.Equal([10, 20, 99], result!.Mopp.Code);
        }

        [Fact]
        public void ParsesBoundsBigGeometryTransformsAndChunks()
        {
            string output = string.Join('\n',
                "0", "0", "0", "1",
                "1", "7",                            // one code byte
                "-1", "-2", "-3", "0",               // bounds min
                "1", "2", "3", "0",                  // bounds max
                "1", "4", "5", "6", "1",             // one big vertex
                "1", "0", "1", "2", "3", "4",        // one big triangle
                "1", "7", "8", "9", "0",             // one transform: translation
                "0", "0", "0", "1",                  // ...and its rotation
                "1",                                 // one chunk
                "1", "2", "3", "4",                  // chunk offset
                "11",                                // material info
                "65535",                             // hard-coded, ignored
                "0",                                 // transform index
                "2", "100", "200",                   // vertices
                "3", "1", "2", "3",                  // indices
                "1", "3",                            // strip lengths
                "1", "42");                          // welding info

            CompressedMeshResult? result = MopperProcessGenerator.ParseCompressedMeshOutput(output);

            Assert.NotNull(result);

            Assert.Equal(-1f, result!.BoundsMin.X);
            Assert.Equal(3f, result.BoundsMax.Z);

            NifVector4 bigVertex = Assert.Single(result.BigVertices);
            Assert.Equal(4f, bigVertex.X);

            var bigTriangle = Assert.Single(result.BigTriangles);
            Assert.Equal(3u, bigTriangle.Material);

            CompressedMeshTransform transform = Assert.Single(result.Transforms);
            Assert.Equal(7f, transform.Translation.X);

            // The rotation is read x, y, z, w but stored w first.
            Assert.Equal(1f, transform.Rotation.W);

            CompressedMeshChunk chunk = Assert.Single(result.Chunks);
            Assert.Equal(11u, chunk.MaterialInfo);
            Assert.Equal([(ushort)100, (ushort)200], chunk.Vertices);
            Assert.Equal([(ushort)1, (ushort)2, (ushort)3], chunk.Indices);
            Assert.Equal([(ushort)3], chunk.StripLengths);
            Assert.Equal([(ushort)42], chunk.WeldingInfo);
        }

        [Fact]
        public void RejectsTruncatedOutput() =>
            // Claims a chunk but stops before describing it.
            Assert.Null(MopperProcessGenerator.ParseCompressedMeshOutput(
                string.Join('\n', "0", "0", "0", "1", "1", "7",
                    "0", "0", "0", "0", "1", "1", "1", "0", "0", "0", "0", "1")));

        [Fact]
        public void RejectsErrorOutput() =>
            Assert.Null(MopperProcessGenerator.ParseCompressedMeshOutput("Havok error: could not build"));

        [Fact]
        public void DegradesCleanlyWhenMopperIsMissing()
        {
            var generator = new MopperProcessGenerator { MopperPath = "/nonexistent/mopper.exe" };

            Assert.Null(generator.GenerateCompressedMesh([Cube]));
        }

        [Fact]
        public void TheDllBackendDoesNotOfferCompressedMeshes() =>
            // NifMopp.dll exports only the simple-mesh entry points, so a compressed
            // mesh needs mopper.exe. The default interface implementation says so.
            Assert.Null(((IMoppGenerator)new NifMoppGenerator()).GenerateCompressedMesh([Cube]));

        [Fact]
        public void BuildsARealCompressedMeshWhenMopperIsPresent()
        {
            var generator = new MopperProcessGenerator();

            if (!generator.IsAvailable)
                return;

            CompressedMeshResult? result = generator.GenerateCompressedMesh([Cube]);

            Assert.NotNull(result);
            Assert.NotEmpty(result!.Mopp.Code);
            Assert.True(result.Mopp.Scale > 0);

            // A cube has to come back as some geometry, whether chunked or "big".
            Assert.True(result.Chunks.Count > 0 || result.BigVertices.Count > 0,
                "expected either chunks or big vertices");

            // The bounds must actually contain the unit cube.
            Assert.True(result.BoundsMin.X <= 0.001f);
            Assert.True(result.BoundsMax.X >= 0.999f);
        }
    }
}
