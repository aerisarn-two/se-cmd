using NIFSharp;
using SECmd.Havok;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// mopper.exe is a Havok build and is not redistributable, so it will usually be
    /// absent. These tests cover the parts that do not need it: the wire format in
    /// both directions, and clean degradation when it is missing.
    /// </summary>
    public class MopperTests
    {
        private static readonly NifVector3[] Triangle =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)
        ];

        private static readonly NifTriangle[] Indices = [new(0, 1, 2)];

        [Fact]
        public void SerialisesTheInputFormat()
        {
            string input = MopperProcessGenerator.BuildSimpleMeshInput(Triangle, Indices);

            string[] lines = input.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal("3", lines[0]);
            Assert.Equal("0 0 0", lines[1]);
            Assert.Equal("1 0 0", lines[2]);
            Assert.Equal("0 1 0", lines[3]);
            Assert.Equal("1", lines[4]);
            Assert.Equal("0 1 2", lines[5]);

            // The material-index count must be zero: mopper reads each index with
            // operator>> into a uint8, which consumes a character rather than a
            // number, so a non-zero count would be misparsed.
            Assert.Equal("0", lines[6]);
        }

        [Fact]
        public void FormatsFloatsInvariantly()
        {
            // A comma decimal separator would make mopper stop reading mid-vertex.
            string input = MopperProcessGenerator.BuildSimpleMeshInput(
                [new NifVector3(1.5f, -2.25f, 0.125f)], Indices);

            Assert.Contains("1.5 -2.25 0.125", input);
            Assert.DoesNotContain(",", input);
        }

        [Fact]
        public void ParsesTheOutputFormat()
        {
            // origin, scale, length, bytes, then triangle count and welding info.
            string output = string.Join('\n',
                "1.5", "-2.5", "3.5",
                "0.0078125",
                "4",
                "40", "0", "255", "39",
                "1",
                "6");

            MoppResult? result = MopperProcessGenerator.ParseSimpleMeshOutput(output);

            Assert.NotNull(result);
            Assert.Equal([40, 0, 255, 39], result!.Code);
            Assert.Equal(1.5f, result.Origin.X);
            Assert.Equal(-2.5f, result.Origin.Y);
            Assert.Equal(3.5f, result.Origin.Z);
            Assert.Equal(0.0078125f, result.Scale);
            Assert.Equal([(ushort)6], result.WeldingInfo);
        }

        [Fact]
        public void ParsesOutputWithoutWeldingInfo()
        {
            string output = string.Join('\n', "0", "0", "0", "1", "2", "10", "20");

            MoppResult? result = MopperProcessGenerator.ParseSimpleMeshOutput(output);

            Assert.NotNull(result);
            Assert.Equal([10, 20], result!.Code);
            Assert.Empty(result.WeldingInfo);
        }

        [Fact]
        public void RejectsErrorOutput()
        {
            // On failure mopper prints Havok's diagnostics instead of numbers.
            Assert.Null(MopperProcessGenerator.ParseSimpleMeshOutput(
                "Havok error: could not build MOPP code"));
        }

        [Fact]
        public void RejectsTruncatedOutput()
        {
            // Claims four bytes but supplies two.
            Assert.Null(MopperProcessGenerator.ParseSimpleMeshOutput(
                string.Join('\n', "0", "0", "0", "1", "4", "10", "20")));
        }

        [Fact]
        public void DegradesCleanlyWhenMopperIsMissing()
        {
            var generator = new MopperProcessGenerator { MopperPath = "/nonexistent/mopper.exe" };

            Assert.False(generator.IsAvailable);
            Assert.False(string.IsNullOrEmpty(generator.UnavailableReason));
            Assert.Null(generator.GenerateSimpleMesh(Triangle, Indices));
        }

        [Fact]
        public void ExplainsUnavailabilityForBothBackends()
        {
            if (MoppGenerator.Resolve() is not null)
                return;

            string message = MoppGenerator.DescribeUnavailability();

            Assert.Contains("NifMopp.dll", message);
            Assert.Contains("mopper.exe", message);
        }

        [Fact]
        public void GeneratesRealCodeWhenMopperIsPresent()
        {
            var generator = new MopperProcessGenerator();

            if (!generator.IsAvailable)
                return;

            MoppResult? result = generator.GenerateSimpleMesh(Triangle, Indices);

            Assert.NotNull(result);
            Assert.NotEmpty(result!.Code);
            Assert.True(result.Scale > 0);
        }

        [Fact]
        public void LooksInTheWorkingDirectoryBeforeTheExecutableDirectory()
        {
            var generator = new MopperProcessGenerator();
            var paths = generator.SearchPaths().ToList();

            Assert.Equal(Path.Combine(Environment.CurrentDirectory, "mopper.exe"), paths[0]);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "mopper.exe"), paths[1]);
        }

        [Fact]
        public void AnExplicitPathIsTheOnlyOneConsidered()
        {
            var generator = new MopperProcessGenerator { MopperPath = "/somewhere/mopper.exe" };

            Assert.Equal(["/somewhere/mopper.exe"], generator.SearchPaths());
        }

        [Fact]
        public void NifMoppLooksInTheWorkingDirectoryToo()
        {
            var paths = NifMopp.SearchPaths().ToList();

            Assert.Contains(Path.Combine(Environment.CurrentDirectory, "NifMopp.dll"), paths);
            Assert.Contains(Path.Combine(AppContext.BaseDirectory, "NifMopp.dll"), paths);
        }
    }
}
