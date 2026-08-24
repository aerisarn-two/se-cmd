using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The meshes that do not survive the round trip, kept to hand.
    /// </summary>
    /// <remarks>
    /// The corpus sweep converts 22,047 meshes and takes about seventeen minutes,
    /// which is the right cost for proving a change is safe and the wrong cost for
    /// asking a question about one mesh. Every investigation in this file's history
    /// began by waiting for that sweep to reach the interesting file.
    ///
    /// So the interesting files are extracted once, into a folder git ignores, and
    /// examined from there in about a second. The list of paths is committed because
    /// it is a fact about the game rather than a piece of it; the meshes are not,
    /// because they are Bethesda's.
    ///
    /// Everything here skips quietly when the folder is empty, so an ordinary build
    /// on a machine with no game installed is unaffected.
    /// </remarks>
    public class DivergentCorpusTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static string Folder =>
            Path.Combine(AppContext.BaseDirectory, "Resources", "vanilla");

        /// <summary>The paths the sweep last reported, one per line.</summary>
        private static IEnumerable<string> Listed()
        {
            string list = Path.Combine(Folder, "divergent.txt");

            return File.Exists(list)
                ? File.ReadAllLines(list).Where(l => l.Trim().Length > 0)
                : [];
        }

        /// <summary>What a listed path is called once it is a file on disk.</summary>
        /// <remarks>
        /// Flattened, with separators becoming underscores: two meshes in different
        /// folders can share a name, and a flat folder is easier to look through than
        /// a reconstructed tree of single-file directories.
        /// </remarks>
        private static string FileNameFor(string path) =>
            path.Replace('\\', '/').Replace('/', '_').Replace(' ', '_');

        [Fact]
        public void ExtractsTheDivergentMeshes()
        {
            string? data = Environment.GetEnvironmentVariable("SECMD_SKYRIM_DATA");

            if (string.IsNullOrWhiteSpace(data))
                return;

            Assert.True(Directory.Exists(data), $"SECMD_SKYRIM_DATA is not a folder: {data}");

            var wanted = Listed()
                .ToDictionary(p => p.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

            Assert.NotEmpty(wanted);

            Directory.CreateDirectory(Folder);

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string archive in new[] { "Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa" })
            {
                string full = Path.Combine(data, archive);

                if (!File.Exists(full))
                    continue;

                foreach (var file in Archive.CreateReader(GameRelease.SkyrimSE, full).Files)
                {
                    string path = file.Path.Replace('\\', '/');

                    if (!wanted.ContainsKey(path))
                        continue;

                    File.WriteAllBytes(Path.Combine(Folder, FileNameFor(path)), file.GetBytes());
                    found.Add(path);
                }
            }

            // A listed mesh that is not in the archives is a list gone stale, which is
            // worth saying rather than leaving as a quietly smaller corpus.
            var missing = wanted.Keys.Where(p => !found.Contains(p)).ToList();

            Assert.True(missing.Count == 0, $"not in the archives: {string.Join(", ", missing)}");
        }

        /// <summary>
        /// The extracted meshes, or one empty name when there are none.
        /// </summary>
        /// <remarks>
        /// A theory with no cases is a *failure* in xunit, not a pass — and on a
        /// machine with no copy of the game there are legitimately none. So the empty
        /// case is a case, and the test returns from it having done nothing.
        /// </remarks>
        public static TheoryData<string> Extracted()
        {
            var data = new TheoryData<string>();

            var files = Directory.Exists(Folder)
                ? Directory.GetFiles(Folder, "*.nif")
                : [];

            if (files.Length == 0)
            {
                data.Add(string.Empty);
                return data;
            }

            foreach (string file in files)
                data.Add(Path.GetFileName(file));

            return data;
        }

        /// <summary>
        /// Reports what each extracted mesh loses or gains, without asserting.
        /// </summary>
        /// <remarks>
        /// These are the meshes already known not to round-trip, so a failing
        /// assertion would say only what is already written down. What is wanted from
        /// them is the *shape* of each difference while a fix is being tried, so the
        /// census goes to the test output and the test passes.
        ///
        /// The one thing it does assert is that the conversion returns at all: a mesh
        /// that hangs is a different and worse problem than one that diverges, and
        /// this corpus is where the last one was found.
        /// </remarks>
        [Theory]
        [MemberData(nameof(Extracted))]
        public void ReportsWhatEachDivergentMeshDoes(string name)
        {
            // Nothing extracted, which is what a machine without the game looks like.
            if (name.Length == 0)
                return;

            NifModel source = NifModel.Load(Path.Combine(Folder, name), Db);
            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            var converter = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                });

            NifModel rebuilt = converter.Convert(Db);

            // The sweep's own comparison, not a second opinion. It knows which
            // differences are meant to be there -- BSXFlags is calculated rather than
            // carried, and the import writes the transform-carrying rigid body -- and
            // a fast tool that disagreed with the slow one would be worse than no
            // fast tool.
            string? differences = BsaCorpusTests.CompareBlocks(source, rebuilt);

            Console.WriteLine(differences is null ? $"{name}: matches" : $"{name}: {differences}");

            foreach (string warning in converter.Warnings)
                Console.WriteLine($"    {warning}");
        }
    }
}
