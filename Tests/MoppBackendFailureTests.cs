using NIFSharp;
using SECmd.Havok;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// A backend that dies rather than declining.
    /// </summary>
    /// <remarks>
    /// Generation is retried, so a model that crashes mopper can be hidden entirely by
    /// the attempt that works: the file comes out right and nothing says the tool was
    /// killed. That is the case worth reporting loudest, because it is the only chance
    /// to learn which model does it.
    ///
    /// These drive the real backend at a program that fails, which is the only way to
    /// exercise the exit-code check and the bookkeeping around it — a stand-in
    /// generator would only test itself.
    /// </remarks>
    public class MoppBackendFailureTests
    {
        /// <summary>A program that exits non-zero and says nothing.</summary>
        private const string AlwaysFails = "/bin/false";

        /// <summary>A program that exits zero and says nothing.</summary>
        private const string AlwaysSucceeds = "/bin/true";

        private static MopperProcessGenerator Backend(string program) =>
            new()
            {
                MopperPath = program,

                // Run it directly: it is not a Windows binary and wants no emulator.
                WineCommand = string.Empty,
                Timeout = TimeSpan.FromSeconds(20)
            };

        private static readonly List<NifVector3> Vertices =
            [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

        private static readonly List<NifTriangle> Triangles = [new(0, 1, 2)];

        [Fact]
        public void ABackendThatDiesIsReported()
        {
            if (!File.Exists(AlwaysFails))
                return;

            MopperProcessGenerator backend = Backend(AlwaysFails);

            Assert.True(backend.IsAvailable, backend.UnavailableReason);
            Assert.Null(backend.GenerateSimpleMesh(Vertices, Triangles));

            // Every attempt, not just the last: a sweep names the model on these.
            Assert.NotEmpty(backend.LastFailures);

            // And each says what happened rather than how it looked from here. A
            // backend that dies before reading its input breaks the pipe first, and
            // "Broken pipe" sends the reader nowhere -- the exit code is the answer.
            Assert.All(
                backend.LastFailures,
                f => Assert.Contains("exited with code", f, StringComparison.Ordinal));
        }

        [Fact]
        public void ABackendThatSaysNothingUsefulIsNotAccusedOfDying()
        {
            if (!File.Exists(AlwaysSucceeds))
                return;

            MopperProcessGenerator backend = Backend(AlwaysSucceeds);

            // It exits cleanly and produces no numbers, which is a shape the backend
            // would not index rather than a backend that fell over. Both come back
            // null; only one is a crash.
            Assert.Null(backend.GenerateSimpleMesh(Vertices, Triangles));

            Assert.All(
                backend.LastFailures,
                f => Assert.DoesNotContain("exited with code", f, StringComparison.Ordinal));
        }

        [Fact]
        public void AGenerationThatWasNeverTriedReportsNothing()
        {
            MopperProcessGenerator backend = Backend("/does/not/exist");

            Assert.False(backend.IsAvailable);
            Assert.Null(backend.GenerateSimpleMesh(Vertices, Triangles));

            // Refusing to run is not failing to run, and saying so would send someone
            // looking for a model that never reached the backend.
            Assert.Empty(backend.LastFailures);
        }

        [Fact]
        public void TheInterfaceDefaultsClaimNoTrouble()
        {
            IMoppGenerator quiet = new Quiet();

            Assert.Empty(quiet.LastFailures);
            Assert.Null(quiet.LastDiagnostics);
        }

        private sealed class Quiet : IMoppGenerator
        {
            public bool IsAvailable => false;

            public string? UnavailableReason => "not here";

            public MoppResult? GenerateSimpleMesh(
                IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles) => null;
        }
    }
}
