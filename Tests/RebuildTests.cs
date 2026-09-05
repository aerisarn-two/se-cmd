using NIFSharp;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Rebuilding a file through the authoring API rather than re-saving the tree
    /// the reader produced.
    /// </summary>
    /// <remarks>
    /// The corpus round trip loads a file and saves it back, which proves the reader
    /// and the serialiser agree. It says nothing about how a model gets *built*:
    /// <c>InsertBlock</c>, <c>SetArraySize</c>, the condition invalidation around
    /// them, and the header the whole thing is recomputed into. That is the path the
    /// FBX importer uses, and until now the only files exercising it were the ones
    /// tests construct by hand.
    ///
    /// So: read a file, build a second model from scratch by copying it block by
    /// block through the public API, save that, and require the same bytes.
    ///
    /// Byte identity holds because <see cref="NifModel.UpdateHeader"/> writes the
    /// block-type table in first-use order, which is what Bethesda's exporter does:
    /// of 2,500 vanilla Skyrim meshes checked, 2,500 are ordered that way and none
    /// are ordered any other way. A rebuilt header therefore comes out the same as
    /// the one that was read.
    ///
    /// A file written by some other tool may order its table differently, and that
    /// order is preserved rather than rewritten — re-saving a file should change what
    /// was edited and nothing else. Two corpus fixtures are nifly optimizer output
    /// and order theirs that way; they rebuild byte for byte too.
    /// </remarks>
    public class RebuildTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string ResourceRoot => Path.Combine(AppContext.BaseDirectory, "Resources");

        /// <summary>
        /// Files whose block-type table is not in first-use order.
        /// </summary>
        /// <remarks>
        /// Both are nifly's optimizer output rather than Bethesda's: it rewrites the
        /// geometry blocks, and the replacement type lands at the end of the table
        /// instead of where the block that uses it sits.
        /// </remarks>
        private static readonly string[] ToolOrdered =
        [
            "TestNifFile_Optimize_SE_to_LE.nif",
            "TestNifFile_Optimize_Dynamic_LE_to_SE.nif"
        ];

        public static TheoryData<string> Fixtures() => CorpusTests.AllFixtures();

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void RebuildingThroughTheApiReproducesTheFile(string relative)
        {
            string path = Path.Combine(ResourceRoot, relative);

            byte[] original = File.ReadAllBytes(path);

            using var saved = new MemoryStream();
            Rebuild(NifModel.Load(path, Db)).Save(saved);

            byte[] actual = saved.ToArray();

            Assert.Equal(original.Length, actual.Length);

            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != actual[i])
                {
                    Assert.Fail($"{relative} differs at offset 0x{i:X} "
                                + $"(expected 0x{original[i]:X2}, got 0x{actual[i]:X2})");
                }
            }
        }

        [Fact]
        public void EveryOtherFixtureOrdersItsTypesByFirstUse()
        {
            foreach (string relative in CorpusTests.FixturePaths()
                         .Where(r => !ToolOrdered.Any(t => r.EndsWith(t, StringComparison.Ordinal))))
            {
                NifModel model = NifModel.Load(Path.Combine(ResourceRoot, relative), Db);

                var table = model.FindItem(model.Header, "Block Types")!
                    .Children.Select(c => c.Value.AsString()).ToList();

                var firstUse = new List<string>();

                foreach (NifItem block in model.Blocks)
                {
                    if (!firstUse.Contains(block.Name))
                        firstUse.Add(block.Name);
                }

                // This is what makes a rebuilt header come out the same as the one
                // that was read, so it is worth stating rather than relying on.
                Assert.Equal(table, firstUse);
            }
        }

        [Theory]
        [InlineData("nifly/TestNifFile_Optimize_SE_to_LE.nif")]
        [InlineData("nifly/TestNifFile_Optimize_Dynamic_LE_to_SE.nif")]
        public void AToolsOwnTypeOrderIsKeptRatherThanNormalised(string relative)
        {
            NifModel model = NifModel.Load(Path.Combine(ResourceRoot, relative), Db);

            var table = model.FindItem(model.Header, "Block Types")!
                .Children.Select(c => c.Value.AsString()).ToList();

            var firstUse = new List<string>();

            foreach (NifItem block in model.Blocks)
            {
                if (!firstUse.Contains(block.Name))
                    firstUse.Add(block.Name);
            }

            // These are the files that would have been rewritten. Keeping the order
            // they came with is what lets them rebuild byte for byte as well.
            Assert.NotEqual(table, firstUse);
            Assert.Equal(table.OrderBy(t => t, StringComparer.Ordinal), firstUse.OrderBy(t => t, StringComparer.Ordinal));
        }

        [Fact]
        public void AnEmptyEntryKeepsItsPlaceInTheStringTable()
        {
            NifModel model = NifModel.CreateNew(Db);

            // Bethesda's files contain these: one vanilla weapon effect has an empty
            // entry a third of the way down its table of thirty-six.
            model.SetStringTable(["first", string.Empty, "third"]);

            NifItem node = model.InsertBlock("NiNode");
            model.FindItem(node, "Name")!.Value.SetCount(2);

            model.SetRoots([node]);
            model.UpdateHeader();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Equal(
                ["first", string.Empty, "third"],
                reloaded.FindItem(reloaded.Header, "Strings")!.Children.Select(c => c.Value.AsString()));

            // Interning would have dropped the empty one and pulled "third" up onto
            // index 1, so every name after it would resolve to its neighbour.
            Assert.Equal("third", reloaded.GetName(reloaded.Blocks[0]));
        }

        /// <summary>Reports every leaf whose value did not survive.</summary>
        private static void Compare(NifItem expected, NifItem actual, List<string> lost)
        {
            if (lost.Count >= 8)
                return;

            if (expected.Children.Count != actual.Children.Count)
            {
                lost.Add($"{NifModel.PathOf(expected)}: "
                         + $"{expected.Children.Count} children became {actual.Children.Count}");

                return;
            }

            if (expected.Children.Count == 0)
            {
                string before = expected.Value.ToString();
                string after = actual.Value.ToString();

                if (before != after)
                    lost.Add($"{NifModel.PathOf(expected)}: '{before}' became '{after}'");

                return;
            }

            for (int i = 0; i < expected.Children.Count; i++)
                Compare(expected.Children[i], actual.Children[i], lost);
        }

        /// <summary>
        /// Builds a fresh model with the same content, using only the public API.
        /// </summary>
        /// <remarks>
        /// Blocks are inserted by type and then filled field by field, which is what
        /// makes this a test of the authoring path: every array has to be sized from
        /// its own count, and every condition re-evaluated as the values it names are
        /// set.
        /// </remarks>
        public static NifModel Rebuild(NifModel source)
        {
            var rebuilt = NifModel.CreateNew(Db, source.Version, source.UserVersion, source.BSVersion);

            // Verbatim, not interned: the indices are already in the blocks about to
            // be copied, so the table has to line up with them entry for entry.
            // Bethesda's files contain empty entries, and folding one away shifts
            // every name after it.
            if (source.FindItem(source.Header, "Strings") is { } strings)
                rebuilt.SetStringTable(strings.Children.Select(e => e.Value.AsString()));

            CopyValues(rebuilt, source.Header, rebuilt.Header);

            foreach (NifItem block in source.Blocks)
                CopyValues(rebuilt, block, rebuilt.InsertBlock(block.Name));

            CopyValues(rebuilt, source.Footer, rebuilt.Footer);

            rebuilt.UpdateHeader();
            return rebuilt;
        }

        /// <summary>
        /// Copies one item's live fields onto another, sizing arrays as it goes.
        /// </summary>
        /// <remarks>
        /// The same ordered walk reading uses, for the same reason: a count has to be
        /// set before the array it sizes is reached, and a flag before the field it
        /// governs is looked for.
        /// </remarks>
        private static void CopyValues(NifModel model, NifItem from, NifItem to)
        {
            for (int i = 0; i < from.Children.Count && i < to.Children.Count; i++)
            {
                NifItem source = from.Children[i];
                NifItem target = to.Children[i];

                target.InvalidateCondition();

                if (target.IsAbstract || !model.EvalCondition(target))
                    continue;

                if (target.IsArray)
                {
                    model.UpdateArraySize(target);
                    CopyValues(model, source, target);
                    continue;
                }

                if (target.Children.Count > 0)
                {
                    CopyValues(model, source, target);
                    continue;
                }

                target.Value = source.Value;
            }
        }
    }
}
