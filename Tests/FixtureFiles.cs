namespace SECmd.Tests
{
    /// <summary>
    /// Which files under <c>Resources</c> are fixtures.
    /// </summary>
    /// <remarks>
    /// Three suites walk that folder for every `.nif` it holds, and it now holds two
    /// kinds of thing. The fixtures are committed and the same on every machine; the
    /// meshes under `vanilla` are extracted from an installed copy of the game by
    /// whoever has one (see <see cref="DivergentCorpusTests"/>).
    ///
    /// Swept up by the fixture suites, those meshes make the test run depend on local
    /// state: a machine with the game runs more tests than one without, and the count
    /// changes whenever the divergent list does. Whatever is examined there should be
    /// examined deliberately, by the suite that owns it.
    /// </remarks>
    internal static class FixtureFiles
    {
        /// <summary>The folder holding meshes extracted from the game.</summary>
        internal const string Extracted = "vanilla";

        /// <summary>Whether a path under Resources is a committed fixture.</summary>
        /// <remarks>
        /// The corrupted fixture is excluded for a different reason: it exists to fail
        /// loading, so a suite that loads everything cannot include it.
        /// </remarks>
        internal static bool IsFixture(string path) =>
            !path.Contains("Corrupted", StringComparison.Ordinal)
            && !path.Replace('\\', '/').Contains($"/{Extracted}/", StringComparison.Ordinal)
            && !path.Replace('\\', '/').StartsWith($"{Extracted}/", StringComparison.Ordinal);
    }
}
