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
            },
            tolerated:
            [
                // The one mesh in the game whose collision cannot be rebuilt. Its
                // compressed shape has two chunks and the first is a near-flat sliver --
                // 0.049 by 0.007 by 0.063 metres, ten triangles -- which Havok will not
                // build a MOPP for on its own, so the two-material split fails and the
                // shape goes with it. ck-cmd builds these the same way and loses it too;
                // the spec records what has been ruled out (§7.3).
                //
                // Named rather than allowed for by a ceiling, so that a *second* mesh
                // losing its collision still fails. A share would absorb it.
                "meshes/architecture/solitude/clutter/sbigplanter01.nif"
            ]);
        }

        /// <summary>
        /// Every skinned mesh comes back standing where its own skeleton puts it.
        /// </summary>
        /// <remarks>
        /// Not a field comparison. This asks the question the fields are only evidence
        /// for: after the round trip, is a vertex still where the bone that owns it
        /// expects it? The measure is the mean distance from a vertex to that bone's
        /// origin, mapped by the bone's own skin transform, over the vertices a bone
        /// holds nine tenths of.
        ///
        /// It is measured against the file's own answer rather than an absolute, so a
        /// mesh that was always far from its bones is not a failure -- only one that
        /// moved.
        ///
        /// This catches what a field comparison cannot say plainly. Baking a shape's
        /// transform into its vertices displaced 220 of the 836 skinned meshes sampled,
        /// 26%, and showed up in the field sweep as thousands of differences in
        /// Triangles, Num Vertices, Vertex Data and the partition lists -- one fault
        /// wearing seven names.
        /// </remarks>
        [Fact]
        public void EveryVanillaSkinnedMeshStaysWithItsBones()
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

                (double before, int groups) = BoneFit(source);

                if (groups == 0)
                    return null;

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                (double after, int rebuiltMeasured) = BoneFit(rebuilt);

                if (rebuiltMeasured == 0)
                    return $"the skin is gone: {groups} owned vertices became none";

                double was = before / groups;
                double now = after / rebuiltMeasured;

                // A twentieth further out, and a unit, before it counts as moved: half
                // precision and a refitted hull both cost a little.
                return now > was * 1.05 + 1
                    ? $"skinned mesh moved from its bones: {was:F2} to {now:F2}"
                    : null;
            });
        }

        /// <summary>
        /// How far a vertex sits from the bone that owns it, over how many vertices.
        /// </summary>
        private static (double Total, int Vertices) BoneFit(NifModel m)
        {
            double total = 0;
            int measured = 0;

            foreach (NifItem shape in m.Blocks.Where(b => m.BlockInherits(b, "BSTriShape")).ToList())
            {
                if (m.GetRef(shape, "Skin") is not { } skin) continue;
                if (m.GetRef(skin, "Data") is not { } data) continue;
                if (m.ReadSkin(shape) is not { } read) continue;
                if (m.FindItem(data, "Bone List") is not { } bones) continue;

                NifItem? buffer = m.FindItem(shape, "Vertex Data");

                if ((buffer?.Children.Count ?? 0) == 0
                    && m.GetRef(skin, "Skin Partition") is { } partition)
                {
                    buffer = m.FindItem(partition, "Vertex Data");
                }

                if (buffer is null || buffer.Children.Count == 0) continue;

                var vertices = buffer.Children
                    .Select(v => m.FindItem(v, "Vertex")?.Value.Get<NifVector3>() ?? default)
                    .ToList();

                for (int b = 0; b < Math.Min(bones.Children.Count, read.Bones.Count); b++)
                {
                    var owned = read.Bones[b].Weights
                        .Where(w => w.Weight > 0.9f)
                        .Select(w => w.Vertex)
                        .Where(i => i < vertices.Count)
                        .ToList();

                    // No floor on how many vertices a bone must own.
                    //
                    // There was one, of eight, to keep a tiny group's average from
                    // swinging a mean of means. Measuring per vertex makes it
                    // unnecessary -- a bone owning one vertex now contributes one
                    // vertex -- and leaves it actively wrong, because whether a group
                    // clears the floor depends on how the bone list is arranged.
                    // `winteraspen06` has TrunkBone twice on one shape, owning 24
                    // vertices and 5, with the same pose and the same weights either
                    // way; merged, the 5 cross the floor and join the measurement, and
                    // a rebuild that moved nothing read as a mesh off its bones.
                    if (owned.Count == 0) continue;

                    if (m.FindItem(bones.Children[b], "Skin Transform") is not { } item) continue;

                    var bone = new NifTransform(
                        m.FindItem(item, "Translation")?.Value.Get<NifVector3>() ?? default,
                        m.FindItem(item, "Rotation")?.Value.Get<NifMatrix33>() ?? NifMatrix33.Identity,
                        m.FindItem(item, "Scale")?.Value.ToFloat() ?? 1f);

                    // Summed per vertex, not averaged per bone, so the answer is a
                    // mean over vertices rather than a mean of means.
                    //
                    // Averaging per bone made the measure depend on how the bone list
                    // happened to be arranged. Skyrim's conifers list one bone once per
                    // partition -- `winteraspen06` gives TrunkBone two entries with the
                    // same pose and disjoint vertices -- and a rebuild that merges those
                    // into one entry moves no vertex at all, yet changed both the terms
                    // and the divisor of a mean of means, and reported a mesh off its
                    // bones. What is being asked is whether a vertex sits where the bone
                    // owning it puts it, which is a question about vertices.
                    foreach (int i in owned)
                    {
                        NifVector3 v = bone.Apply(vertices[i]);
                        total += Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                        measured++;
                    }
                }
            }

            return (total, measured);
        }

        /// <summary>
        /// Every collision mesh comes back the same shape, if not the same bytes.
        /// </summary>
        /// <remarks>
        /// A compressed mesh shape is not stored as geometry. It is stored as chunks,
        /// each a quantised cloud of millimetre offsets from an origin Havok chose, and
        /// Havok chooses again on the way back. So `Chunks`, `Num Chunks`, `Min` and
        /// `Offset` all differ on a mesh that is otherwise perfectly intact, and the
        /// field sweep can only report that they do -- it has no way to say whether the
        /// *shape* survived, which is the only thing the physics engine reads.
        ///
        /// This asks that instead, of the decoded triangles rather than the encoding:
        ///
        /// - total surface area within 1%, so nothing of substance is added or lost;
        /// - the bounding box within half a game unit at every corner, so the thing
        ///   still occupies the space it did;
        /// - and no vertex of the *rebuilt* mesh further than two game units from the
        ///   nearest vertex of the source, so nothing is invented away from the surface
        ///   the file described.
        ///
        /// The tolerances come from the quantisation rather than from taste. A chunk
        /// stores millimetres and a game unit is about 14mm, so re-chunking against a
        /// different origin moves a vertex by a millimetre or so. Over 529 shapes the
        /// median vertex moves 0.12 units, the ninety-ninth percentile 0.85, and the
        /// worst 1.52.
        ///
        /// Two things this deliberately does not ask, having asked them and found them
        /// measuring the encoding rather than the shape.
        ///
        /// Not the triangle count. Havok welds at a millimetre, and welding takes the
        /// slivers with it: `hhmainhallfgate01` comes back with 105 triangles where it
        /// had 107, and the two it lost have areas of 4.35 and 7.25 against a mesh
        /// totalling 837,117 -- the surface changes by 0.009%. Thirty-six meshes lose a
        /// triangle or two that way, all of them slivers, none of them anything a
        /// physics engine could collide with.
        ///
        /// And not the source-to-rebuilt direction of the Hausdorff. A vertex used only
        /// by a sliver that has been welded away has no counterpart at all, so that
        /// direction reports the distance to the next real vertex -- tens of
        /// millimetres, which reads as a shape that moved when it is a shape that lost
        /// a sliver. The other direction still says what matters, that nothing appears
        /// where the file had nothing, and the area test is what bounds how much may
        /// quietly go missing.
        /// </remarks>
        [Fact]
        public void EveryVanillaCollisionMeshKeepsItsShape()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (!source.Blocks.Any(b => b.Name == "bhkCompressedMeshShape"))
                    return null;

                if (source.FindItem(source.Footer, "Roots") is not { Children.Count: > 0 } roots
                    || source.GetBlock(roots.Children[0]) is not { } root)
                {
                    return null;
                }

                List<MeshGeometry> was = CollisionMeshes(source);

                if (was.Count == 0)
                    return null;

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                List<MeshGeometry> now = CollisionMeshes(rebuilt);

                if (now.Count != was.Count)
                    return $"{was.Count} collision meshes became {now.Count}";

                for (int i = 0; i < was.Count; i++)
                {
                    if (Incongruent(was[i], now[i]) is { } why)
                        return $"collision mesh {i}: {why}";
                }

                return null;
            },
            ceiling: KnownCollisionDivergence);
        }

        /// <summary>
        /// The share of collision meshes known to come back a different shape.
        /// </summary>
        /// <remarks>
        /// 54 of 22,047 meshes, 0.24%, and the tail is thin: 24 lose or gain up to a
        /// couple of percent of their surface, 23 put a vertex two to two and a half
        /// units off anything in the source, 5 shift a bounding box by exactly 0.700
        /// units, and one is `sbigplanter01`, whose collision cannot be rebuilt at all
        /// (§7.3).
        ///
        /// A ceiling rather than a list of paths, as the field ratchet is, because the
        /// list would go stale against a corpus this large. The failures file names
        /// every one on any run that writes it.
        ///
        /// What causes them is Havok's own builder and not anything this port chose,
        /// which was checked rather than assumed: `civilwarmapflag01` is a box eight
        /// vertices and twelve triangles across, half a unit thick, and it comes back
        /// with all eight vertices and eight triangles -- two whole faces discarded, so
        /// it is not welding, which would have taken vertices with it. Rebuilding
        /// mopper with the weld tolerance at a tenth of a millimetre instead of one
        /// gives byte-identical output on all three of the worst meshes.
        /// </remarks>
        private const double KnownCollisionDivergence = 0.003;

        /// <summary>
        /// Every convex hull comes back with the corners it went in with.
        /// </summary>
        /// <remarks>
        /// Corners rather than planes, and that is the whole of the point.
        ///
        /// A `bhkConvexVerticesShape` stores both: the corners, and the half-spaces
        /// Havok actually collides against. The obvious test is whether the two hulls
        /// bound the same space -- every corner of each inside the other's half-spaces,
        /// which for convex bodies is a proof of equality rather than a tolerance. That
        /// test was written, run over all 3,516 hulls the game ships, and found 306 of
        /// them different by more than a hundredth of their own span.
        ///
        /// The 306 are vanilla's. Asked to contain their *own* corners, 306 of the
        /// game's hulls do not, by more than a hundredth of a span -- and another 454
        /// miss by less. The stored corners and the stored planes describe different
        /// bodies in the file itself. A rebuild that reproduces the corners and fits
        /// planes around them therefore reads as "too big" against planes that never
        /// bounded those corners to begin with, which says nothing about the rebuild.
        ///
        /// So what is asked is what can be answered: the corner sets agree, within a
        /// hundredth of the hull's own span, in both directions. They do, and by a
        /// wide margin -- 3,499 of 3,516 agree to a *thousandth* of a span, with a
        /// median displacement of exactly zero.
        ///
        /// The plane count is not asked about at all. It differs on half the hulls, and
        /// what to make of that is genuinely open (§7.3): our planes contain our corners
        /// and 306 files' planes do not contain theirs, so matching them field for field
        /// would mean reproducing a fault.
        /// </remarks>
        [Fact]
        public void EveryVanillaConvexHullKeepsItsCorners()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (!source.Blocks.Any(b => b.Name == "bhkConvexVerticesShape"))
                    return null;

                if (source.FindItem(source.Footer, "Roots") is not { Children.Count: > 0 } roots
                    || source.GetBlock(roots.Children[0]) is not { } root)
                {
                    return null;
                }

                List<List<NifVector3>> was = HullCorners(source);

                if (was.Count == 0)
                    return null;

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                List<List<NifVector3>> now = HullCorners(rebuilt);

                if (now.Count != was.Count)
                    return $"{was.Count} convex hulls became {now.Count}";

                for (int i = 0; i < was.Count; i++)
                {
                    float ex = was[i].Max(v => v.X) - was[i].Min(v => v.X);
                    float ey = was[i].Max(v => v.Y) - was[i].Min(v => v.Y);
                    float ez = was[i].Max(v => v.Z) - was[i].Min(v => v.Z);

                    double span = Math.Max(ex, Math.Max(ey, ez));

                    if (span <= 0)
                        continue;

                    double moved = Math.Max(CornersApart(was[i], now[i]), CornersApart(now[i], was[i]));

                    if (moved / span > 0.01)
                    {
                        return $"hull {i}: a corner moved {moved:F4}, "
                               + $"{moved / span:P1} of its {span:F3} span";
                    }
                }

                return null;
            },
            ceiling: KnownHullDivergence);
        }

        /// <summary>
        /// The share of meshes whose convex hulls come back a different shape.
        /// </summary>
        /// <remarks>
        /// 17 hulls of 3,516 move a corner by more than a hundredth of their span. The
        /// rest are exact: 3,499 agree to a thousandth, and the median displacement over
        /// all of them is zero.
        /// </remarks>
        private const double KnownHullDivergence = 0.002;

        /// <summary>Every convex hull's corners, in block order.</summary>
        private static List<List<NifVector3>> HullCorners(NifModel m)
        {
            var all = new List<List<NifVector3>>();

            foreach (NifItem hull in m.Blocks.Where(b => b.Name == "bhkConvexVerticesShape"))
            {
                var corners = new List<NifVector3>();

                foreach (NifItem item in m.FindItem(hull, "Vertices")?.Children ?? [])
                {
                    NifVector4 v = item.Value.Get<NifVector4>();
                    corners.Add(new NifVector3(v.X, v.Y, v.Z));
                }

                if (corners.Count > 0)
                    all.Add(corners);
            }

            return all;
        }

        /// <summary>The furthest any corner of one hull sits from the nearest of the other.</summary>
        /// <remarks>
        /// One way, so callers take both. A hull is a few dozen corners at most, so this
        /// is the honest quadratic rather than the bucketing the collision meshes need.
        /// </remarks>
        private static double CornersApart(List<NifVector3> from, List<NifVector3> to)
        {
            double worst = 0;

            foreach (NifVector3 a in from)
            {
                double best = double.MaxValue;

                foreach (NifVector3 b in to)
                {
                    double d = (a.X - b.X) * (a.X - b.X)
                               + (a.Y - b.Y) * (a.Y - b.Y)
                               + (a.Z - b.Z) * (a.Z - b.Z);

                    if (d < best) best = d;
                }

                if (best == double.MaxValue) return double.MaxValue;

                worst = Math.Max(worst, Math.Sqrt(best));
            }

            return worst;
        }

        /// <summary>
        /// Every node a rebuilt sequence names can be found in the palette.
        /// </summary>
        /// <remarks>
        /// A `NiDefaultAVObjectPalette` maps a name to a block so the animation system
        /// can resolve a sequence's tracks without walking the tree. A target the
        /// palette does not list is a track with nothing to bind to.
        ///
        /// This is not a comparison against vanilla, because the palette's *contents*
        /// are a known gap (`RoundTripBaseline`, "Num Objs"): Bethesda lists more than
        /// the animated nodes -- emitters and geometry among them -- and no rule for
        /// which has been found. What is not a gap, and is what the block is for, is
        /// that everything the sequences name is in there. Vanilla holds that in 1,271
        /// of its 1,274 palettes; the three that do not are files wrong about
        /// themselves, in the way 306 convex hulls and 13 MOPP trees are.
        ///
        /// So this asks it of the rebuilt file alone, which makes it an invariant rather
        /// than a fidelity measure: whatever the palette ends up holding, a sequence
        /// must be able to resolve through it.
        /// </remarks>
        [Fact]
        public void EveryRebuiltSequenceTargetIsInThePalette()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (!source.Blocks.Any(b => source.BlockInherits(b, "NiSequence")))
                    return null;

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

                var named = new HashSet<string>(StringComparer.Ordinal);

                foreach (NifItem sequence in rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiSequence")))
                foreach (NifItem entry in rebuilt.FindItem(sequence, "Controlled Blocks")?.Children ?? [])
                {
                    if (rebuilt.GetString(entry, "Node Name") is { Length: > 0 } target)
                        named.Add(target);
                }

                if (named.Count == 0)
                    return null;

                if (rebuilt.Blocks.FirstOrDefault(b => b.Name == "NiDefaultAVObjectPalette")
                    is not { } palette)
                {
                    return $"{named.Count} sequence targets and no palette to resolve them";
                }

                var listed = new HashSet<string>(StringComparer.Ordinal);

                foreach (NifItem obj in rebuilt.FindItem(palette, "Objs")?.Children ?? [])
                {
                    if (rebuilt.GetString(obj, "Name") is { Length: > 0 } name)
                        listed.Add(name);
                }

                var missing = named.Where(n => !listed.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

                return missing.Count == 0
                    ? null
                    : $"{missing.Count} sequence target(s) missing from the palette: "
                      + string.Join(", ", missing.Take(3));
            },
            ceiling: KnownPaletteDivergence);
        }

        /// <summary>
        /// The share of meshes whose rebuilt palette cannot resolve every target.
        /// </summary>
        /// <remarks>
        /// Two, and both are the source's doing. `dragon_oh_bloodyhead` names
        /// `Ohdaviing_Tail_DragonBloodTail` and `sprigganmatron` names two
        /// `SprigganBodyLeaves01` nodes that their own palettes do not list either --
        /// they are two of the three vanilla files that miss a target of the 1,274 that
        /// have one. Reproducing a file's own omission is the conversion being faithful;
        /// inventing an entry the source lacks would not be.
        /// </remarks>
        private const double KnownPaletteDivergence = 0.001;

        /// <summary>
        /// Every node a rebuilt transform track moves is named by the controller that
        /// fans the sequences out to it.
        /// </summary>
        /// <remarks>
        /// The companion to the palette check, and for the same reason. A
        /// `NiMultiTargetTransformController` lists the nodes a sequence's transform
        /// tracks drive, and the engine binds those tracks through the list rather than
        /// through each node's own controller chain -- so a node missing from it stays
        /// still however many keys name it.
        ///
        /// Whether Bethesda's list is *longer* than ours is a separate and open
        /// question: it pads the array, and 92.5% of the slots in the game's are empty
        /// (`RoundTripBaseline`, "Num Extra Targets"). Padding costs nothing. A missing
        /// entry costs the animation, which is what this asks about.
        ///
        /// Asked of the rebuilt file alone, so it is an invariant rather than a
        /// comparison -- the same shape as the palette check, which was written to
        /// confirm a list was adequate and found 866 meshes where it was not.
        /// </remarks>
        [Fact]
        public void EveryRebuiltTransformTargetIsFannedOutTo()
        {
            Sweep((original, db) =>
            {
                using var input = new MemoryStream(original);
                NifModel source = NifModel.Load(input, db);

                if (!source.Blocks.Any(b => source.BlockInherits(b, "NiSequence")))
                    return null;

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

                // Which nodes a transform track actually drives.
                var moved = new HashSet<string>(StringComparer.Ordinal);

                foreach (NifItem sequence in rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiSequence")))
                foreach (NifItem entry in rebuilt.FindItem(sequence, "Controlled Blocks")?.Children ?? [])
                {
                    if (rebuilt.GetString(entry, "Controller Type") is not "NiTransformController")
                        continue;

                    if (rebuilt.GetString(entry, "Node Name") is { Length: > 0 } target)
                        moved.Add(target);
                }

                if (moved.Count == 0)
                    return null;

                var fanned = new HashSet<string>(StringComparer.Ordinal);

                foreach (NifItem controller in rebuilt.Blocks
                             .Where(b => b.Name == "NiMultiTargetTransformController"))
                {
                    // The controller's own target counts: it is the root, and a sequence
                    // may accumulate against it.
                    if (rebuilt.GetRef(controller, "Target") is { } own)
                        fanned.Add(rebuilt.GetName(own));

                    foreach (NifItem slot in rebuilt.FindItem(controller, "Extra Targets")?.Children ?? [])
                    {
                        if (rebuilt.GetBlock(slot) is { } node)
                            fanned.Add(rebuilt.GetName(node));
                    }
                }

                var missing = moved.Where(n => !fanned.Contains(n))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();

                return missing.Count == 0
                    ? null
                    : $"{missing.Count} transform target(s) nothing fans out to: "
                      + string.Join(", ", missing.Take(3));
            });
        }

        /// <summary>
        /// A rebuilt file does not strand blocks the source could reach.
        /// </summary>
        /// <remarks>
        /// The block sweep counts blocks and the field sweep compares their contents.
        /// Neither asks whether they are still *joined up*: a shape whose parent no
        /// longer lists it, or a property nothing points at, is present in both counts
        /// and absent from the game.
        ///
        /// Counted rather than matched block for block, because the two files number
        /// their blocks differently and a name is not an identity -- what a stranded
        /// block costs is that it stops being reached, and the count of what is reached
        /// says that without needing to know which one went.
        ///
        /// Reachable means from the footer's roots, following every link and reference
        /// there is. Some of the game's files carry loose blocks on purpose -- there is
        /// a fixture named for it -- so this asks only that the rebuilt file reaches no
        /// *fewer* than the source did, not that it reaches everything.
        ///
        /// A gate rather than a ratchet: all 22,047 pass, so there is no known set to
        /// hold the line against, and a file that starts stranding blocks should fail
        /// rather than be counted.
        ///
        /// It is the slowest of these sweeps by some way -- twenty minutes against six
        /// -- because it walks every link in both files rather than a few fields. Worth
        /// it for what it covers: a shape whose parent no longer lists it is present in
        /// the block sweep's count and correct in the field sweep's comparison, and gone
        /// from the game.
        /// </remarks>
        [Fact]
        public void ARebuiltFileStrandsNothingTheSourceReached()
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

                int before = Reachable(source);

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                int after = Reachable(rebuilt);

                // Fewer blocks overall is the block sweep's business; this is about the
                // ones that survived and then went unreferenced.
                if (after >= before || rebuilt.Blocks.Count < before)
                    return null;

                return $"{before} blocks were reachable and {after} are, "
                       + $"of {rebuilt.Blocks.Count} in the rebuilt file";
            });
        }

        /// <summary>How many blocks the footer's roots can reach, by any link.</summary>
        private static int Reachable(NifModel m)
        {
            var seen = new HashSet<int>();
            var queue = new Queue<NifItem>();

            foreach (NifItem entry in m.FindItem(m.Footer, "Roots")?.Children ?? [])
            {
                if (m.GetBlock(entry) is { } block && seen.Add(m.IndexOf(block)))
                    queue.Enqueue(block);
            }

            while (queue.Count > 0)
            {
                NifItem block = queue.Dequeue();

                Walk(block);

                void Walk(NifItem item)
                {
                    foreach (NifItem child in item.Children)
                    {
                        if (child.Value.IsLink)
                        {
                            if (m.GetBlock(child) is { } target && seen.Add(m.IndexOf(target)))
                                queue.Enqueue(target);

                            continue;
                        }

                        if (child.Children.Count > 0)
                            Walk(child);
                    }
                }
            }

            return seen.Count;
        }

        /// <summary>
        /// A rebuilt mesh does not quietly start losing more vertices than it does now.
        /// </summary>
        /// <remarks>
        /// The reader merges two corners that agree in all twenty-three factors, which
        /// is what FBXWrangler does and what a scene from a DCC tool needs. Vanilla
        /// ships byte-identical duplicate rows -- a castle wall with 75 vertices and 54
        /// distinct ones -- so a rebuilt mesh is routinely smaller than its source, and
        /// nothing else in the file indexes those rows: no `BSSubIndexTriShape`, no
        /// segments, no morph controller anywhere in the game, and a dynamic shape's
        /// parallel buffer is rebuilt from the same list.
        ///
        /// Measured over the whole corpus: 11,988 shapes in 6,265 meshes come back
        /// smaller, losing 464,897 vertices of 36,183,819 -- 1.28%, and under 1% of what
        /// the eighteen-factor key alone would merge. Half of the affected shapes lose
        /// ten vertices or fewer.
        ///
        /// So this is a ratchet on the share of meshes affected rather than a gate. What
        /// it is for is the direction: welding is a judgement about what makes a vertex
        /// itself, and a change to the key that starts merging more aggressively would
        /// otherwise be invisible -- the block sweep counts blocks, and a shape with
        /// fewer vertices has the same ones.
        /// </remarks>
        [Fact]
        public void NoMoreVanillaMeshesLoseVerticesThanAlreadyDo()
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

                List<int> was = VertexCounts(source);

                if (was.Count == 0)
                    return null;

                NifModel rebuilt = new FbxToNif(
                    new FbxScene(new NifToFbx(source).Convert()),
                    new FbxToNifOptions
                    {
                        RootName = source.GetName(root),
                        Version = source.Version,
                        UserVersion = source.UserVersion,
                        LegendaryEdition = source.BSVersion < 100
                    }).Convert(db);

                List<int> now = VertexCounts(rebuilt);

                // A different number of shapes is the block sweep's business.
                if (now.Count != was.Count)
                    return null;

                int lost = 0, shapes = 0;

                for (int i = 0; i < was.Count; i++)
                {
                    if (now[i] >= was[i]) continue;

                    lost += was[i] - now[i];
                    shapes++;
                }

                return shapes == 0 ? null : $"{shapes} shape(s) lost {lost} vertices";
            },
            ceiling: KnownVertexLoss);
        }

        /// <summary>
        /// The share of vanilla meshes that come back with fewer vertices.
        /// </summary>
        /// <remarks>
        /// 6,265 of 22,047, and the cause is welding duplicate rows the game ships
        /// rather than anything lost. The number to watch is whether it rises.
        /// </remarks>
        private const double KnownVertexLoss = 0.30;

        /// <summary>Every geometry shape's vertex count, in block order.</summary>
        private static List<int> VertexCounts(NifModel m)
        {
            var all = new List<int>();

            foreach (NifItem shape in m.Blocks.Where(b => m.BlockInherits(b, "NiAVObject")).ToList())
            {
                if (m.BlockInherits(shape, "BSTriShape"))
                {
                    NifItem? buffer = m.FindItem(shape, "Vertex Data");

                    // A skinned SSE shape keeps its geometry in the partition, with its
                    // own counts at zero.
                    if ((buffer?.Children.Count ?? 0) == 0
                        && m.GetRef(shape, "Skin") is { } skin
                        && m.GetRef(skin, "Skin Partition") is { } partition)
                    {
                        buffer = m.FindItem(partition, "Vertex Data");
                    }

                    if (buffer is not null)
                        all.Add(buffer.Children.Count);
                }
                else if (m.BlockInherits(shape, "NiTriBasedGeom")
                         && m.GetRef(shape, "Data") is { } data
                         && m.FindItem(data, "Vertices") is { } vertices)
                {
                    all.Add(vertices.Children.Count);
                }
            }

            return all;
        }

        /// <summary>How two collision meshes fail to be the same shape, or null.</summary>
        private static string? Incongruent(MeshGeometry was, MeshGeometry now)
        {
            (double areaA, NifVector3 minA, NifVector3 maxA) = MeasureMesh(was);
            (double areaB, NifVector3 minB, NifVector3 maxB) = MeasureMesh(now);

            if (areaA > 0 && Math.Abs(areaB / areaA - 1) > 0.01)
                return $"surface area changed by {(areaB / areaA - 1) * 100:F2}%";

            double box = Math.Max(
                Math.Max(Math.Abs(minA.X - minB.X), Math.Max(Math.Abs(minA.Y - minB.Y), Math.Abs(minA.Z - minB.Z))),
                Math.Max(Math.Abs(maxA.X - maxB.X), Math.Max(Math.Abs(maxA.Y - maxB.Y), Math.Abs(maxA.Z - maxB.Z))));

            if (box > 0.5)
                return $"bounding box moved by {box:F3} units";

            double apart = HausdorffTo(now, was);

            return apart > 2.0
                ? $"a rebuilt vertex sits {apart:F3} units from anything in the source"
                : null;
        }

        /// <summary>Total surface area and bounding box.</summary>
        private static (double Area, NifVector3 Min, NifVector3 Max) MeasureMesh(MeshGeometry mesh)
        {
            double area = 0;

            foreach (NifTriangle t in mesh.Triangles)
            {
                NifVector3 a = mesh.Vertices[t.V1], b = mesh.Vertices[t.V2], c = mesh.Vertices[t.V3];

                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
                double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;

                double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;

                area += 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
            }

            return (
                area,
                new NifVector3(mesh.Vertices.Min(v => v.X), mesh.Vertices.Min(v => v.Y), mesh.Vertices.Min(v => v.Z)),
                new NifVector3(mesh.Vertices.Max(v => v.X), mesh.Vertices.Max(v => v.Y), mesh.Vertices.Max(v => v.Z)));
        }

        /// <summary>
        /// The furthest any vertex of one mesh sits from the nearest vertex of the other.
        /// </summary>
        /// <remarks>
        /// One way only, and the caller measures rebuilt against source: what that asks
        /// is whether anything was invented. The other direction asks whether anything
        /// went missing, which welding makes it bad at -- see the remarks on the test --
        /// and which the area comparison answers better.
        ///
        /// Bucketed, because the meshes run to thousands of vertices and the honest
        /// answer to "nearest of these to each of those" is quadratic. Buckets are four
        /// units across and only the twenty-seven around a point are searched, which is
        /// exact for any distance under a bucket and returns something too large rather
        /// than too small beyond it -- so a mesh this reports as congruent is congruent.
        /// </remarks>
        private static double HausdorffTo(MeshGeometry from, MeshGeometry to)
        {
            const double Cell = 4.0;

            var grid = new Dictionary<(int, int, int), List<NifVector3>>();

            static (int, int, int) At(NifVector3 v) =>
                ((int)Math.Floor(v.X / Cell), (int)Math.Floor(v.Y / Cell), (int)Math.Floor(v.Z / Cell));

            foreach (NifVector3 v in to.Vertices)
            {
                (int, int, int) key = At(v);

                if (!grid.TryGetValue(key, out List<NifVector3>? bucket))
                    grid[key] = bucket = [];

                bucket.Add(v);
            }

            double worst = 0;

            foreach (NifVector3 v in from.Vertices)
            {
                (int cx, int cy, int cz) = At(v);
                double best = double.MaxValue;

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out List<NifVector3>? bucket))
                        continue;

                    foreach (NifVector3 w in bucket)
                    {
                        double d = (v.X - w.X) * (v.X - w.X)
                                   + (v.Y - w.Y) * (v.Y - w.Y)
                                   + (v.Z - w.Z) * (v.Z - w.Z);

                        if (d < best) best = d;
                    }
                }

                // Nothing within a bucket of it. Reported as the whole extent rather
                // than infinity, so the message says something a reader can size up.
                if (best == double.MaxValue)
                    return double.MaxValue;

                worst = Math.Max(worst, Math.Sqrt(best));
            }

            return worst;
        }

        /// <summary>Every collision mesh in a model, decoded as the export decodes it.</summary>
        private static List<MeshGeometry> CollisionMeshes(NifModel m)
        {
            var all = new List<MeshGeometry>();
            FbxScene scene;

            try { scene = new FbxScene(new NifToFbx(m).Convert()); }
            catch { return all; }

            foreach (FbxObject node in scene.OfClass("Model").Where(o => o.Name.Contains("mopp_mesh")))
            {
                if (scene.ChildrenOf(node.Id).FirstOrDefault(o => o.Class == "Geometry") is not { } geometry)
                    continue;

                MeshGeometry? mesh = FbxMeshReader.Read(
                    geometry, new FbxMeshReader.Options { InvertU = false, InvertV = false });

                if (mesh is { Triangles.Count: > 0 })
                    all.Add(mesh);
            }

            return all;
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
        /// A ceiling rather than a list because naming the meshes would be longer than
        /// the code and would say less: what matters is that the number falls and never
        /// rises. It has -- 400 of 600 on the first sweep that asked, 15,634 of 22,047
        /// when it was first measured across the whole corpus, and 207 of 22,047 now.
        ///
        /// **Set it back down every time it falls.** At 0.72 against an actual 0.94% it
        /// was two orders of magnitude of slack: a change could have made seventy times
        /// as many meshes differ and the sweep would still have passed. A ratchet that
        /// is not tightened is a ratchet that has stopped being one.
        ///
        /// The fields behind what is left, most first: skin weights and the
        /// per-partition bone lists that hold them (153 of the 207), vertex counts and
        /// `Data Size`, triangles and the vertex maps that index them. They are the
        /// shape of a few problems rather than two hundred, and none of them is
        /// reachable from the two dozen fixtures the baseline was written against.
        ///
        /// Set from the whole corpus and not from a sample, because the sample
        /// flatters: `Sample` takes an equal count from each archive where the archives
        /// are neither the same size nor equally divergent, so 4,000 files answer 0.43%
        /// where all 22,047 answer 0.94%. Run the sweep sampled against a ceiling set
        /// from a sample and it is a ratchet measuring its own sampling.
        /// </remarks>
        private const double KnownFieldDivergence = 0.0095;

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
        /// <summary>
        /// How many meshes to convert at once.
        /// </summary>
        /// <remarks>
        /// Unbounded by default, as `Parallel.ForEach` is, which takes every core the
        /// machine has for the better part of an hour. That is right for a sweep run on
        /// its own and wrong for one run beside anything else, so `SECMD_THREADS` caps
        /// it -- a sweep is a background check, and a background check that makes the
        /// machine unusable gets stopped before it finishes, which is the same as not
        /// running it.
        /// </remarks>
        private static int Threads() =>
            int.TryParse(Environment.GetEnvironmentVariable("SECMD_THREADS"), out int n) && n > 0
                ? n
                : -1;

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

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Threads() }, file =>
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
