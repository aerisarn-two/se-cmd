using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The reader and writer against every mesh Skyrim ships.
    /// </summary>
    /// <remarks>
    /// <c>Skyrim - Meshes0.bsa</c> and <c>Meshes1.bsa</c> hold 22,047 NIFs between
    /// them: every vanilla static, creature, architecture piece, weapon and effect,
    /// across every block type Bethesda actually used. Loading each and saving it
    /// back has to reproduce the file byte for byte.
    ///
    /// Twice over: once re-saving the tree the reader built, and once rebuilding the
    /// whole model through the authoring API first (see <see cref="RebuildTests"/>).
    /// The second is the harder of the two and the one the FBX importer depends on.
    ///
    /// This is a different kind of check from the committed fixtures. Twenty-four
    /// files chosen for the features they demonstrate cannot tell you what the
    /// hundredth-most-common block looks like in the wild; twenty-two thousand
    /// arbitrary ones can. It found four bugs the fixtures never would have: ragged
    /// two-dimensional arrays sizing to nothing, half-precision NaNs losing their
    /// payload, block-type tables being reordered, and string tables being interned
    /// when they had to be copied.
    ///
    /// **Nothing is copied out of the archives.** They are read in place, from a
    /// folder named by <c>SECMD_SKYRIM_DATA</c>.
    ///
    /// **It does not run unless asked.** Without that variable the test returns, so
    /// an ordinary <c>dotnet test</c> is unaffected and a checkout without Skyrim
    /// passes. The two sweeps take about five and ten minutes, which is far too long
    /// to sit in the middle of everybody's build:
    ///
    /// <code>
    /// SECMD_SKYRIM_DATA="/path/to/Skyrim Special Edition/Data" dotnet test \
    ///     --filter "FullyQualifiedName~BsaCorpus"
    /// </code>
    ///
    /// <c>SECMD_BSA_SAMPLE=N</c> checks a subset instead of all of them, for when
    /// fifteen minutes is still too long.
    /// </remarks>
    [Trait("Category", "Corpus")]
    public class BsaCorpusTests
    {
        private static readonly string[] Archives = ["Skyrim - Meshes0.bsa", "Skyrim - Meshes1.bsa"];

        /// <summary>
        /// The Data folder to sweep, or null when nobody asked for one.
        /// </summary>
        /// <remarks>
        /// Named rather than searched for. A test that finds the game on its own
        /// runs on whoever happens to have it installed, which is how a five-minute
        /// sweep ends up in somebody else's ordinary build.
        /// </remarks>
        private static string? DataFolder()
        {
            string? configured = Environment.GetEnvironmentVariable("SECMD_SKYRIM_DATA");

            if (string.IsNullOrWhiteSpace(configured))
                return null;

            // Set but wrong is a different thing from not set: somebody asked for
            // this sweep and did not get it, and passing quietly would tell them it
            // had run.
            Assert.True(Directory.Exists(configured), $"SECMD_SKYRIM_DATA is not a folder: {configured}");

            string? missing = Archives.FirstOrDefault(a => !File.Exists(Path.Combine(configured, a)));

            Assert.True(missing is null, $"{missing} is not in {configured}");

            return configured;
        }

        [Fact]
        public void EveryVanillaMeshSavesBackByteForByte()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                using var output = new MemoryStream();
                model.Save(output);

                return Compare(original, output);
            });
        }

        [Fact]
        public void EveryVanillaMeshRebuildsByteForByte()
        {
            // The same files through the authoring path instead: every block
            // inserted by type and filled field by field, every array sized from its
            // own count, the header and string table recomputed. That is what the FBX
            // importer does, and the only thing that says it does it correctly at
            // scale.
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                using var output = new MemoryStream();
                RebuildTests.Rebuild(model).Save(output);

                return Compare(original, output);
            });
        }

        [Fact]
        public void EveryVanillaMeshAgreesWithItsCalculatedBsxFlags()
        {
            // BSXFlags is derived rather than authored -- every bit is a fact about
            // the block graph -- so the importer recalculates it instead of carrying
            // the source value. This is what says the rules are Bethesda's rules and
            // not a reading of ck-cmd's source: the files themselves were written by
            // the exporter that defined them.
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                NifItem? bsx = model.Blocks.FirstOrDefault(b => b.Name == "BSXFlags");

                if (bsx is null)
                    return null;

                uint stored = model.GetUInt(bsx, "Integer Data");
                uint calculated = model.Calculate();

                if (stored == calculated)
                    return null;

                return $"BSXFlags stored 0x{stored:X} but calculates 0x{calculated:X} "
                       + $"(differing bits 0x{stored ^ calculated:X})";
            },
            // Forty of the 13,068 vanilla meshes carrying a BSXFlags disagree with
            // the calculation, and every one was run down: none is a rule this gets
            // wrong. `docs/bsxflags-spec.md` §8 has the evidence per group. They are
            // listed rather than counted so that a new disagreement is a failure
            // instead of a number that quietly drifts.
            tolerated:
            [
                // Bit 7: stored clear where the graph is a single collision. The same
                // features occur in 10,205 meshes that do set it, against 22 that do
                // not, so these are outliers -- and they cluster in test content.
                "meshes/shadertests/testcaveepiccorner01.nif",
                "meshes/shadertests/testcaveepiccorner02.nif",
                "meshes/shadertests/testcaveepiccorner03.nif",
                "meshes/shadertests/testcaveepiccorner04.nif",
                "meshes/shadertests/testcaveepicinsidecorner01.nif",
                "meshes/shadertests/testcaveepicinsidecorner02.nif",
                "meshes/shadertests/testcaveepicinsidecorner03.nif",
                "meshes/shadertests/testcaveepicinsidecorner04.nif",
                "meshes/shadertests/testcaveepicmid03.nif",
                "meshes/shadertests/testcaveepicwall01.nif",
                "meshes/shadertests/testcaveepicwall02.nif",
                "meshes/shadertests/testcaveepicwall03.nif",
                "meshes/shadertests/testcaveepicwall04.nif",
                "meshes/architecture/markarth/markarthhousetemp01.nif",
                "meshes/architecture/markarth/markarthtemphouse.nif",
                "meshes/clutter/counterset/countercornerout01.nif",
                "meshes/clutter/table02.nif",
                "meshes/weapons/imperial/imperialswordgo.nif",
                "meshes/dlc02/dungeons/apocrypha/animated/forbiddenbook/apoforbiddenbookact01.nif",
                "meshes/actors/character/character assets/hair/hairlonghumanm.nif",

                // Bit 3: the hasRootCollision term, which ck-cmd's own source annotates
                // "wrong. may be complex but only in 6 models". Their features are
                // identical to 118 meshes that go the other way.
                "meshes/architecture/solitude/sbluepalacegate.nif",
                "meshes/architecture/solitude/sbluepalaceroof.nif",
                "meshes/architecture/solitude/serikur house.nif",
                "meshes/clutter/horsetrough/horsetrough01.nif",
                "meshes/dlc02/landscape/trees/treepineforestbroken04.nif",
                "meshes/dlc02/landscape/trees/treepineforestbroken04_smoking.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arceiling01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/ardoor01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/ardoorplug01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/traps/artraplongspikes01.nif",

                // Files whose stored value ck-cmd's algorithm cannot produce either,
                // because the block graph contradicts it -- Havok bits with no collision
                // block, an editor-marker bit with nothing named EditorMarker, a dynamic
                // bodies bit on a rigid body that is MO_QUAL_FIXED.
                "meshes/actors/character/character assets/hair/hairshorthumanfold.nif",
                "meshes/effects/dragoncrash/fxdragoncrashfurrow01.nif",
                "meshes/mps/mpsmotesforest01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arpitwalltall02.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/markerentrance.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/markerexit.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arcandleplate01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arcandleplate02.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arwelkydclusterfx01.nif",
                "meshes/creationclub/_shared/dungeons/ayleidruins/interior/arwelkydplanter01.nif"
            ]);
        }

        [Fact]
        public void NoVanillaMeshExportsGeometryThatIsNowhere()
        {
            // A mesh can export with every count right and every vertex in the same
            // place. That is what a BSDynamicTriShape did: its positions live in a
            // buffer the engine rewrites, the static entries beside them are zero, and
            // reading those collapsed the shape onto the origin. Nothing that counts
            // things could see it.
            //
            // The fixtures cover the shapes nifly ships. This covers the shapes the
            // game ships, which is where a conditional field nobody has thought about
            // will actually turn up.
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel model = NifModel.Load(input, db);

                // NaN geometry is excluded: the game ships a handful of effect meshes
                // whose vertices and node rotations are NaN in the file itself, and
                // they decode through the same path as the shapes beside them that
                // come out fine. What this is looking for is a collapse onto a finite
                // point, which is what a field read from the wrong place produces.
                return DegenerateGeometryTests.Degenerate(
                    new FbxScene(new NifToFbx(model).Convert()), reportNotANumber: false);
            });
        }

        [Fact]
        public void EveryVanillaMeshSurvivesTheFbxRoundTrip()
        {
            // NIF to FBX and back over the whole game. Byte identity is not the
            // measure here and will not be for a long time: what this asks is whether
            // the rebuilt file still has the same blocks, which is the thing that says
            // nothing was silently dropped on the way through.
            //
            // Two differences are expected everywhere and are not reported. A BSXFlags
            // is added to files that had none, because the importer calculates one;
            // and a rigid body comes back as bhkRigidBodyT, which is what ck-cmd's
            // non-rig path produces too (§5.7).
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (source.FindItem(source.Footer, "Roots") is not { Children.Count: > 0 } roots
                    || source.GetBlock(roots.Children[0]) is not { } root)
                {
                    return null;
                }

                var converter = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    });

                string? difference = CompareBlocks(source, converter.Convert(db));

                // A backend that crashed is worth reporting even when the file came
                // out right. Generation is retried, so a model that kills mopper can
                // be hidden entirely by the attempt that works -- and a sweep that
                // says nothing is how a crashing model stays unfound.
                var trouble = converter.Warnings
                    .Where(w => w.Contains("MOPP backend failed", StringComparison.Ordinal))
                    .ToList();

                if (trouble.Count == 0)
                    return difference;

                return difference is null
                    ? string.Join("; ", trouble)
                    : $"{difference}; {string.Join("; ", trouble)}";
            });
        }

        /// <summary>
        /// The same round trip, compared field by field rather than block by block.
        /// </summary>
        /// <remarks>
        /// The sweep above asks whether the rebuilt file still has the same blocks,
        /// which catches something being dropped whole. This asks whether the fields in
        /// them still say the same thing, which is what the fixtures are held to and
        /// what `RoundTripBaseline` records the known exceptions of.
        ///
        /// Separate from the block sweep because it is a different question with a
        /// different answer, and because it will be noisier for a long time: the
        /// baseline was written against two dozen fixtures and the game ships 22,047
        /// meshes. A field it reports is either a defect the fixtures never reached or
        /// an entry the baseline is missing, and both are worth knowing.
        ///
        /// Reported as counts per field rather than as differences: a mesh that has
        /// drifted in one field has usually drifted in it a thousand times, and the
        /// field name is the part that says what to look at.
        /// </remarks>
        [Fact]
        public void EveryVanillaMeshAgreesFieldByField()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (source.FindItem(source.Footer, "Roots") is not { Children.Count: > 0 } roots
                    || source.GetBlock(roots.Children[0]) is not { } root)
                {
                    return null;
                }

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                List<NifDifference> unexplained = RoundTripBaseline.Unexplained(source, rebuilt);

                if (unexplained.Count == 0)
                    return null;

                return string.Join(
                    ", ",
                    unexplained.GroupBy(d => d.Field)
                        .OrderByDescending(g => g.Count())
                        .Take(6)
                        .Select(g => $"{g.Key} x{g.Count()}"));
            },
            ceiling: KnownFieldDivergence);
        }

        /// <summary>
        /// The share of vanilla meshes known to differ in some field, as a ratchet.
        /// </summary>
        /// <remarks>
        /// 400 of 600 on the first sweep that asked. A ceiling rather than a list
        /// because naming the meshes would be longer than the code and would say less:
        /// what matters is that the number falls and never rises.
        ///
        /// The fields behind it, most first: skin weights and the bone lists that hold
        /// them, vertex counts inside `NiSkinData`, `Data Size` and the vertex
        /// descriptor. They are the shape of one problem rather than fifty, and none of
        /// them is reachable from the two dozen fixtures the baseline was written
        /// against.
        /// </remarks>
        private const double KnownFieldDivergence = 0.67;

        /// <summary>
        /// How the two files' blocks differ, ignoring the differences that are meant
        /// to be there.
        /// </summary>
        /// <remarks>
        /// Shared with <see cref="DivergentCorpusTests"/>, which examines the handful
        /// of meshes this sweep reports. Two tools that answer the same question
        /// differently is how an afternoon goes into a difference that was never
        /// there.
        /// </remarks>
        internal static string? CompareBlocks(NifModel source, NifModel rebuilt)
        {
            var before = Census(source);
            var after = Census(rebuilt);

            // Calculated rather than carried, so a file that had none gains one.
            before.Remove("BSXFlags");
            after.Remove("BSXFlags");

            // The import writes the transform-carrying body, as ck-cmd's does.
            Fold(before, "bhkRigidBody", "bhkRigidBodyT");
            Fold(after, "bhkRigidBody", "bhkRigidBodyT");

            var differences = before.Keys.Union(after.Keys)
                .Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => $"{k} {before.GetValueOrDefault(k)}->{after.GetValueOrDefault(k)}")
                .ToList();

            return differences.Count == 0 ? null : string.Join(", ", differences);
        }

        private static Dictionary<string, int> Census(NifModel model) =>
            model.Blocks.GroupBy(b => b.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        private static void Fold(Dictionary<string, int> census, string from, string into)
        {
            if (!census.Remove(from, out int count))
                return;

            census[into] = census.GetValueOrDefault(into) + count;
        }

        /// <summary>The reason the two differ, or null when they do not.</summary>
        private static string? Compare(byte[] original, MemoryStream saved)
        {
            byte[] actual = saved.ToArray();

            if (actual.AsSpan().SequenceEqual(original))
                return null;

            int at = 0;

            while (at < actual.Length && at < original.Length && actual[at] == original[at])
                at++;

            return $"differs at 0x{at:X} (length {original.Length} became {actual.Length})";
        }

        /// <summary>
        /// Runs a check over every mesh in the archives, or none if none were asked
        /// for.
        /// </summary>
        /// <summary>
        /// Where a running sweep reports how far it has got.
        /// </summary>
        /// <remarks>
        /// `dotnet test` holds everything a test writes until the run ends, so a sweep
        /// that takes an hour is an hour of silence — there is no way to tell a slow
        /// run from a wedged one, and no way to see a result taking shape. Progress
        /// therefore goes to a file, which can be watched from outside while the run
        /// is still going.
        ///
        /// Set <c>SECMD_PROGRESS</c> to a path to turn it on.
        /// </remarks>
        private static string? ProgressFile() => Environment.GetEnvironmentVariable("SECMD_PROGRESS");

        /// <summary>How many meshes are checked between progress reports.</summary>
        private static int BatchSize() =>
            int.TryParse(Environment.GetEnvironmentVariable("SECMD_BATCH"), out int size) && size > 0
                ? size
                : 1000;

        private static void Report(string? path, string line)
        {
            if (path is null)
                return;

            // Appending rather than rewriting, so an abandoned run leaves its history
            // behind rather than only its last line.
            lock (ProgressLock)
                File.AppendAllText(path, line + Environment.NewLine);
        }

        private static readonly object ProgressLock = new();

        /// <summary>
        /// The file a trace of every mesh is written to, or null when nobody asked.
        /// </summary>
        /// <remarks>
        /// Derived from the progress file rather than named separately: anyone who
        /// wants to watch a sweep wants both.
        /// </remarks>
        private static string? TraceFile() =>
            ProgressFile() is { } progress ? progress + ".trace" : null;

        /// <summary>
        /// Records a mesh starting and finishing, so a sweep that stops says where.
        /// </summary>
        /// <remarks>
        /// Written before the work rather than after it, and flushed as it goes. A
        /// batch line only narrows a hang to a thousand meshes: the run this was
        /// written for sat for three and a half hours, and all anyone knew afterwards
        /// was that it had been somewhere in batch eight of eighteen.
        ///
        /// A file is opened per line, which is slower than holding a handle and is
        /// deliberate. What this has to survive is a process that never gets to close
        /// anything -- a buffered writer's last few lines are exactly the ones naming
        /// the mesh that hung.
        ///
        /// Several meshes run at once, so the ones still going are those with a "&gt;"
        /// and no "&lt;":
        ///
        /// <code>
        /// awk '{ if ($1 == "&gt;") open[$2] = 1; else delete open[$2] }
        ///      END { for (f in open) print f }' progress.txt.trace
        /// </code>
        /// </remarks>
        private static void Trace(string mark, string path)
        {
            if (TraceFile() is not { } file)
                return;

            lock (TraceLock)
                File.AppendAllText(file, $"{mark} {path}{Environment.NewLine}");
        }

        private static readonly object TraceLock = new();

        private static void Sweep(
            Func<byte[], NifXmlDatabase, string?> check,
            string[]? tolerated = null,
            double ceiling = 0)
        {
            var allowed = new HashSet<string>(tolerated ?? [], StringComparer.OrdinalIgnoreCase);

            if (DataFolder() is not { } data)
                return;

            var db = NifXmlDatabase.LoadEmbedded();
            var failures = new ConcurrentBag<(string Path, string Reason)>();
            var stopwatch = Stopwatch.StartNew();
            int checked_ = 0;

            string? progress = ProgressFile();
            int batchSize = BatchSize();

            Report(progress, $"--- {DateTime.Now:HH:mm:ss} starting, batches of {batchSize}");

            foreach (string archive in Archives)
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, Path.Combine(data, archive));

                var files = reader.Files
                    .Where(f => f.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (Sample() is { } sample && sample < files.Count)
                    files = files.OrderBy(f => StableHash(f.Path)).Take(sample).ToList();

                Report(progress, $"{archive}: {files.Count} meshes");

                // Batched so there is something to report between the start and the
                // end; each batch is still checked in parallel.
                int batch = 0;

                foreach (var group in files.Chunk(batchSize))
                {
                    batch++;
                    checked_ += RunBatch(group, db, check, allowed, failures);

                    Report(
                        progress,
                        $"  {DateTime.Now:HH:mm:ss} {archive} batch {batch}: "
                        + $"{checked_} checked, {failures.Count} divergent, {stopwatch.Elapsed:hh\\:mm\\:ss}");
                }
            }

            Report(progress, $"--- {DateTime.Now:HH:mm:ss} done: {checked_} checked, {failures.Count} divergent");

            // Something has to have been checked, or a silent change to the archive
            // names would turn this into a test that always passes.
            Assert.True(checked_ > 0, $"no meshes found in {data}");

            if (failures.IsEmpty)
                return;

            // A sweep with a ceiling is a ratchet rather than a gate: it records how
            // much of the game is known to differ and fails when that grows. Used where
            // the answer is "most of it, and each one is its own investigation" -- a
            // list of the offenders would be longer than the code.
            double share = checked_ == 0 ? 1 : (double)failures.Count / checked_;

            if (ceiling > 0 && share <= ceiling)
            {
                // Written even though the sweep passes: the list is the point of a
                // ratchet. Without it a run under the ceiling says only "still bad",
                // where the file says which meshes and in which fields.
                ListFailures(failures);
                return;
            }

            string overBy = ceiling > 0
                ? $" -- {share:P1} of the sweep against a ceiling of {ceiling:P1}"
                : string.Empty;

            Assert.Fail(Describe(failures, checked_, stopwatch.Elapsed) + overBy);
        }

        /// <summary>Checks one batch, in parallel.</summary>
        /// <returns>How many were checked.</returns>
        private static int RunBatch(
            IReadOnlyList<IArchiveFile> files,
            NifXmlDatabase db,
            Func<byte[], NifXmlDatabase, string?> check,
            HashSet<string> allowed,
            ConcurrentBag<(string Path, string Reason)> failures)
        {
            int done = 0;

            Parallel.ForEach(files, file =>
                {
                    Interlocked.Increment(ref done);
                    Trace(">", file.Path);

                    byte[] original;

                    try
                    {
                        original = file.GetBytes();
                    }
                    catch (Exception e)
                    {
                        failures.Add((file.Path, $"could not be read out of the archive: {e.GetType().Name}"));
                        Trace("<", file.Path);
                        return;
                    }

                    try
                    {
                        if (check(original, db) is { } reason
                            && !allowed.Contains(file.Path.Replace('\\', '/')))
                        {
                            failures.Add((file.Path, reason));
                        }
                    }
                    catch (Exception e)
                    {
                        failures.Add((file.Path, e.Message));
                    }

                    Trace("<", file.Path);
                });

            return done;
        }

        /// <summary>
        /// Groups failures by cause, since one bug shows up as hundreds of files.
        /// </summary>
        /// <summary>
        /// Where every divergent mesh is listed, one per line.
        /// </summary>
        /// <remarks>
        /// The summary below groups by cause and shows three examples of each, which
        /// is what makes it readable and what makes two runs impossible to compare: a
        /// count moving from 123 to 137 says nothing about which files moved. The full
        /// list is written here instead, sorted, so two runs can simply be diffed.
        ///
        /// Set <c>SECMD_FAILURES</c> to a path, or leave it unset and it derives from
        /// <c>SECMD_PROGRESS</c>.
        /// </remarks>
        private static string? FailureFile() =>
            Environment.GetEnvironmentVariable("SECMD_FAILURES")
            ?? (ProgressFile() is { } progress ? progress + ".failures" : null);

        private static void ListFailures(ConcurrentBag<(string Path, string Reason)> failures)
        {
            if (FailureFile() is not { } path)
                return;

            File.WriteAllLines(
                path,
                failures
                    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(f => $"{f.Path.Replace('\\', '/')}\t{f.Reason}"));
        }

        private static string Describe(
            ConcurrentBag<(string Path, string Reason)> failures, int checked_, TimeSpan elapsed)
        {
            ListFailures(failures);

            var report = new System.Text.StringBuilder()
                .AppendLine($"{failures.Count} of {checked_} meshes did not survive the round trip "
                            + $"({elapsed.TotalSeconds:F0}s):")
                .AppendLine();

            var groups = failures
                .GroupBy(f => Generalise(f.Reason))
                .OrderByDescending(g => g.Count());

            foreach (var group in groups)
            {
                report.AppendLine($"  {group.Count(),5}  {group.Key}");

                foreach ((string path, string reason) in group.Take(3))
                    report.AppendLine($"           {path}  [{reason}]");
            }

            if (FailureFile() is { } listed)
                report.AppendLine().AppendLine($"  every divergent mesh is listed in {listed}");

            return report.ToString();
        }

        /// <summary>Strips the numbers out of a message so one cause groups as one.</summary>
        private static string Generalise(string reason) =>
            System.Text.RegularExpressions.Regex.Replace(reason, @"0x[0-9A-Fa-f]+|\d+", "N");

        /// <summary>
        /// A hash that is the same in every process.
        /// </summary>
        /// <remarks>
        /// `string.GetHashCode` is randomised per process, so sampling by it draws a
        /// different set of meshes on every run and two runs cannot be compared —
        /// which is how a divergence count of 123, then 137, then 139 came to look
        /// like changes in the code rather than changes in the sample.
        ///
        /// FNV-1a, because any stable hash will do and this one is four lines.
        /// </remarks>
        private static uint StableHash(string text)
        {
            uint hash = 2166136261;

            foreach (char c in text)
                hash = (hash ^ char.ToLowerInvariant(c)) * 16777619;

            return hash;
        }

        private static int? Sample() =>
            int.TryParse(
                Environment.GetEnvironmentVariable("SECMD_BSA_SAMPLE"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null;
    }
}
