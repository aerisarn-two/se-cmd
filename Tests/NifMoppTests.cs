using NIFSharp;
using SECmd.Havok;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// NifMopp.dll is a Havok build and therefore Windows-only, so these tests assert
    /// that its absence degrades cleanly rather than that it works. Where it is
    /// present, the generation test runs for real.
    /// </summary>
    public class NifMoppTests
    {
        private static readonly NifVector3[] Cube =
        [
            new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0),
            new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)
        ];

        private static readonly NifTriangle[] CubeTriangles =
        [
            new(0, 1, 2), new(0, 2, 3),
            new(4, 6, 5), new(4, 7, 6),
            new(0, 4, 5), new(0, 5, 1),
            new(1, 5, 6), new(1, 6, 2),
            new(2, 6, 7), new(2, 7, 3),
            new(3, 7, 4), new(3, 4, 0)
        ];

        [Fact]
        public void ReportsAvailabilityWithoutThrowing()
        {
            // The point is that probing is safe on any platform.
            bool available = NifMopp.IsAvailable;

            if (!available)
                Assert.False(string.IsNullOrEmpty(NifMopp.UnavailableReason));
        }

        [Fact]
        public void GenerateReturnsNullWhenTheLibraryIsMissing()
        {
            if (NifMopp.IsAvailable)
                return;

            Assert.Null(NifMopp.Generate(Cube, CubeTriangles));
        }

        [Fact]
        public void GenerateReturnsNullForEmptyInput() =>
            Assert.Null(NifMopp.Generate([], []));

        [Fact]
        public void GeneratesCodeWhenTheLibraryIsPresent()
        {
            if (!NifMopp.IsAvailable)
                return;

            MoppCode? mopp = NifMopp.Generate(Cube, CubeTriangles);

            Assert.NotNull(mopp);
            Assert.NotEmpty(mopp!.Code);
            Assert.True(mopp.Scale > 0);
        }
    }
}
