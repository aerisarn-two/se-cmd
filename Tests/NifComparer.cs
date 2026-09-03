using SECmd.Nif;

namespace SECmd.Tests
{
    /// <summary>One field two models disagree about.</summary>
    /// <param name="Path">Where it sits, as a path of block and field names.</param>
    /// <param name="Field">The field's own name, which is what gaps are listed by.</param>
    public readonly record struct NifDifference(string Path, string Field, string Left, string Right)
    {
        public override string ToString() => $"{Path}: {Left} vs {Right}";
    }

    /// <summary>
    /// Compares two models by what they say rather than by how they are laid out.
    /// </summary>
    /// <remarks>
    /// The walk starts at the root and follows references, so two files that describe
    /// the same scene compare equal however their blocks are ordered — which is what
    /// makes it usable on a round trip, where the order is rebuilt from scratch.
    ///
    /// Three kinds of difference are not differences and are not reported:
    ///
    /// <list type="bullet">
    /// <item>Negative zero. It is a distinct float with a distinct encoding, and it
    /// appears wherever a rotation went through a negation, but it is the same
    /// number.</item>
    /// <item>Floats that agree to within a relative epsilon. A transform that crosses
    /// into FBX and back has been decomposed and recomposed, and comes back with the
    /// last bits rearranged.</item>
    /// <item>Bit 19 of an <c>NiAVObject</c>'s flags, which nif.xml describes as
    /// present in some Skyrim files and absent in others.</item>
    /// </list>
    /// </remarks>
    public static class NifComparer
    {
        /// <summary>Floats closer than this, relatively, are the same float.</summary>
        private const float Epsilon = 1e-5f;

        /// <summary>The flag nif.xml says Skyrim carries sometimes and not others.</summary>
        private const uint IgnoredAvFlag = 0x80000;

        /// <summary>
        /// The field a table row with no counterpart is reported under.
        /// </summary>
        /// <remarks>
        /// Not a nif.xml field name: no single field is wrong, the row is. Named
        /// separately so that it can be recorded without recording `Name` everywhere.
        /// </remarks>
        public const string UnpairedEntry = "Unpaired Table Entry";

        /// <summary>
        /// The field a partition row is reported under when the file drew fewer
        /// influences than it authored.
        /// </summary>
        /// <remarks>
        /// Not a nif.xml field: the row is not wrong in one place, it holds a
        /// different set of influences. Named separately so it can be recorded on its
        /// own, without excusing a weight difference anywhere else.
        /// </remarks>
        public const string DroppedInfluence = "Dropped Influence";

        /// <summary>Every field the two models disagree about.</summary>
        public static List<NifDifference> Compare(NifModel left, NifModel right)
        {
            var state = new State(left, right);

            NifItem a = left.GetBlock(left.FindItem(left.Footer, "Roots")!.Children[0])!;
            NifItem b = right.GetBlock(right.FindItem(right.Footer, "Roots")!.Children[0])!;

            state.Blocks(a, b, string.Empty);

            return state.Differences;
        }

        private sealed class State(NifModel left, NifModel right)
        {
            public readonly List<NifDifference> Differences = [];

            private readonly HashSet<(NifItem, NifItem)> _seen = [];

            /// <summary>Arrays whose two sides line up by name rather than by index.</summary>
            private readonly Dictionary<NifItem, int[]> _permuted = [];

            /// <summary>
            /// Table rows the two sides do not agree on, and the names that differ.
            /// </summary>
            /// <remarks>
            /// Two palette entries naming different things are different entries, and
            /// what stands behind them is not a comparison worth making: following both
            /// pairs a billboard with a mesh and reports every field of each. The
            /// disagreement is the name, and that is what gets reported.
            /// </remarks>
            private readonly Dictionary<NifItem, (string Left, string Right)> _unpaired = [];

            /// <summary>Partition rows the file drew with fewer influences than it authored.</summary>
            private readonly Dictionary<NifItem, (string Left, string Right)> _dropped = [];

            /// <summary>Arrays whose two sides may be of different lengths.</summary>
            /// <remarks>
            /// Set only where a row has been dropped from the comparison on purpose,
            /// which makes the lengths disagree by design.
            /// </remarks>
            private readonly HashSet<NifItem> _ragged = [];

            private NifItem _owner = null!;

            /// <summary>The name of a model's first root, for the rule below.</summary>
            private static string RootName(NifModel model) =>
                model.FindItem(model.Footer, "Roots") is { Children.Count: > 0 } roots
                && model.GetBlock(roots.Children[0]) is { } root
                    ? model.GetName(root)
                    : string.Empty;

            public void Blocks(NifItem a, NifItem b, string path)
            {
                if (!_seen.Add((a, b)))
                    return;

                if (a.Name != b.Name)
                {
                    Differences.Add(new NifDifference(path, a.Name, a.Name, b.Name));
                    return;
                }

                NifItem outer = _owner;
                _owner = a;

                if (left.BlockInherits(a, "NiSkinInstance"))
                    AlignBones(a, b);

                Fields(a, b, $"{path}/{a.Name}");
                _owner = outer;
            }

            private void Fields(NifItem a, NifItem b, string path)
            {
                if (_dropped.TryGetValue(a, out (string Left, string Right) drawn))
                {
                    Differences.Add(new NifDifference(path, DroppedInfluence, drawn.Left, drawn.Right));
                    return;
                }

                if (_unpaired.TryGetValue(a, out (string Left, string Right) names))
                {
                    // Reported under a name of its own rather than `Name`, so that
                    // recording it excuses this and not every name in the file.
                    Differences.Add(new NifDifference(path, UnpairedEntry, names.Left, names.Right));
                    return;
                }

                if (a.Name == "Objs")
                    AlignPalette(a, b);

                // A child list one side pads with empty slots. Matched before the
                // length check, since the lengths are exactly what differ.
                if (a.Name == "Children" && a.Children.Count != b.Children.Count)
                    AlignAroundEmptyChildren(a, b);

                if (a.Children.Count != b.Children.Count && !_ragged.Contains(a))
                {
                    Differences.Add(new NifDifference(
                        path, a.Name, $"{a.Children.Count} fields", $"{b.Children.Count} fields"));

                    return;
                }

                // Lists whose order is bookkeeping rather than content, matched by
                // what their entries hold instead of where they sit.
                if (a.Name == "Extra Data List")
                    Align(a, b, Annotations, whole: true);
                else if (a.Name == "Objs")
                    AlignPalette(a, b);
                else if (a.Name == "Controlled Blocks")
                    Align(a, b, ControlledBlocks, whole: true);
                else if (a.Name == "Partitions")
                    AlignPartition(a, b);
                else if (a.Name == "Vertex Weights" && HasVertexIndices(a))
                    Align(a, b, WeightedVertices, whole: true);
                else if (a.Name == "Extra Targets")
                    Align(a, b, Annotations, whole: true);
                else if (a.Name is "Vertex Weights" or "Bone Indices" or "Vertex Map")
                    AlignByVertexMap(a, b);

                _permuted.TryGetValue(a, out int[]? order);

                for (int i = 0; i < a.Children.Count; i++)
                {
                    int at = order is null ? i : order[i];

                    // A row left out of the comparison on purpose.
                    if (at < 0)
                        continue;

                    NifItem ca = a.Children[i], cb = b.Children[at];
                    string p = $"{path}/{ca.Name}";

                    // A field whose condition is false in both files is in neither
                    // file. nif.xml spells some structures several times over for
                    // several Havok generations -- a rigid body's info three times --
                    // and every spelling is in the tree while only one is on disk. Which
                    // one that is depends on the version, so the same name is live in an
                    // SE file and dormant in an LE one.
                    //
                    // This guard changes no result today: it fires 60,330 times across
                    // the fixtures and not one of those dormant fields differs. It is
                    // here so that a writer which starts filling the wrong spelling is
                    // reported as the writer bug it is, rather than as a difference in
                    // bytes that neither file contains.
                    //
                    // Dormant on one side only is still reported: that is a real
                    // structural difference between the two files.
                    if (!left.EvalCondition(ca) && !right.EvalCondition(cb))
                        continue;

                    if (ca.Value.IsLink)
                    {
                        NifItem? ta = left.GetBlock(ca), tb = right.GetBlock(cb);

                        if (ta is null && tb is null)
                            continue;

                        if (ta is null || tb is null)
                        {
                            Differences.Add(new NifDifference(
                                p, ca.Name, ta?.Name ?? "null", tb?.Name ?? "null"));

                            continue;
                        }

                        Blocks(ta, tb, p);
                    }
                    else if (ca.Value.Type == NifValueType.StringIndex)
                    {
                        string sa = left.ResolveString(ca), sb = right.ResolveString(cb);

                        if (sa != sb)
                            Differences.Add(new NifDifference(p, ca.Name, $"'{sa}'", $"'{sb}'"));
                    }
                    else if (ca.Children.Count > 0)
                    {
                        Fields(ca, cb, p);
                    }
                    else if (!Same(ca, cb))
                    {
                        Differences.Add(new NifDifference(
                            p, ca.Name, ca.Value.ToString(), cb.Value.ToString()));
                    }
                }
            }

            /// <summary>
            /// Lines a partition's per-vertex rows up by the vertex the map names.
            /// </summary>
            /// <remarks>
            /// A partition holds a `Vertex Map` and, beside it, one row per mapped
            /// vertex: the weights and the bone indices. The map is the row's name --
            /// row *n* is about `Vertex Map[n]` -- and its order is the partition's own
            /// business, which the spec records at §7.3 and the baseline accepts under
            /// `Vertex Map`.
            ///
            /// The rows shift with it, so comparing them by position reports every row
            /// of a shifted partition as wrong: 14,364 weight differences over a
            /// 1,500-mesh sample. Six of the 13 files whose *only* difference is this
            /// hold exactly the same rows, keyed by the vertex each is about.
            ///
            /// Matched here rather than excused. `Vertex Map`'s own entry says why the
            /// weights are not simply forgiven -- they "differ for reasons of their own
            /// as well" -- and those reasons survive this: the other seven of the 13
            /// still report, because their rows are genuinely different.
            /// </remarks>
            private void AlignPartition(NifItem left_, NifItem right_)
            {
                if (left.FindItem(left_, "Vertex Map") is not { Children.Count: > 0 } leftMap
                    || right.FindItem(right_, "Vertex Map") is not { } rightMap
                    || leftMap.Children.Count != rightMap.Children.Count)
                {
                    return;
                }

                var at = new Dictionary<uint, int>(rightMap.Children.Count);

                for (int j = 0; j < rightMap.Children.Count; j++)
                    at.TryAdd(rightMap.Children[j].Value.ToUInt(), j);

                var order = new int[leftMap.Children.Count];
                bool moved = false;

                for (int i = 0; i < leftMap.Children.Count; i++)
                {
                    if (!at.TryGetValue(leftMap.Children[i].Value.ToUInt(), out int j))
                        return;

                    order[i] = j;
                    moved |= j != i;
                }

                // The arrays the map indexes, when the map moved at all. Not
                // `Vertex Map` itself, which is the statement of the order rather than
                // something ordered by it, and not the triangles, which index into the
                // map and are renumbered with it.
                if (moved)
                {
                    foreach (string field in new[] { "Vertex Weights", "Bone Indices" })
                    {
                        if (left.FindItem(left_, field) is { } rows
                            && right.FindItem(right_, field) is { } theirs
                            && rows.Children.Count == order.Length
                            && theirs.Children.Count == order.Length)
                        {
                            _permuted[rows] = order;
                        }
                    }
                }

                // Whether or not it moved. A partition whose rows are already in step
                // can still hold a row the file drew with fewer influences than it
                // authored, and `horse.nif` is exactly that: same order throughout.
                MarkDroppedInfluences(left_, right_, order);
            }

            /// <summary>
            /// Marks the rows where the file drew fewer influences than it authored.
            /// </summary>
            /// <remarks>
            /// A few of the game's meshes disagree with themselves. In `horse.nif` the
            /// partition rows and the vertex buffer agree with each other on all 4,287
            /// rows and `NiSkinData` disagrees with both on 289: the drawn row holds a
            /// subset of the authored influences, renormalised over what is left.
            /// `beard01.nif` authors vertex 76 with two bones and draws it with one at
            /// 1.0 -- and drops the heavier of the two, so it is not the four-slot limit
            /// and not "the lightest goes" either.
            ///
            /// This converter reads `NiSkinData`, the authored copy, and writes every
            /// influence it finds into both renderer copies, so its output says one
            /// thing throughout. That is the point: a file that contradicts itself has
            /// no reading that reproduces both halves, and dropping influences to match
            /// the one would make the extractor lose what it was given.
            ///
            /// So the row is recorded rather than the field excused, and the test is as
            /// narrow as the situation:
            ///
            /// <list type="bullet">
            /// <item>the drawn bones must be a *strict subset* of the ones we wrote;</item>
            /// <item>every weight the file kept must be ours renormalised over exactly
            /// that subset, to within a thousandth.</item>
            /// </list>
            ///
            /// A row that differs in any other way -- a bone we lack, a weight that is
            /// not the renormalised one, a set that matches with different numbers --
            /// falls through and is reported as the difference it is.
            /// </remarks>
            private void MarkDroppedInfluences(NifItem left_, NifItem right_, int[] order)
            {
                if (left.FindItem(left_, "Vertex Weights") is not { } theirs
                    || right.FindItem(right_, "Vertex Weights") is not { } ours
                    || left.FindItem(left_, "Bone Indices") is not { } theirIndices
                    || right.FindItem(right_, "Bone Indices") is not { } ourIndices
                    || left.FindItem(left_, "Bones") is not { } theirBones
                    || right.FindItem(right_, "Bones") is not { } ourBones)
                {
                    return;
                }

                for (int i = 0; i < order.Length && i < theirs.Children.Count; i++)
                {
                    int j = order[i];

                    if (j < 0 || j >= ours.Children.Count) continue;

                    Dictionary<uint, float> drawn =
                        Row(theirs.Children[i], theirIndices.Children[i], theirBones);

                    Dictionary<uint, float> authored =
                        Row(ours.Children[j], ourIndices.Children[j], ourBones);

                    if (drawn.Count == 0 || drawn.Count >= authored.Count) continue;
                    if (!drawn.Keys.All(authored.ContainsKey)) continue;

                    float kept = drawn.Keys.Sum(b => authored[b]);

                    if (kept <= 0f) continue;

                    bool renormalised = drawn.All(
                        p => MathF.Abs(p.Value - (authored[p.Key] / kept)) < 1e-3f);

                    if (!renormalised) continue;

                    _dropped[theirs.Children[i]] = (Spell(drawn), Spell(authored));
                }
            }

            /// <summary>One partition row as bone-to-weight, zero slots left out.</summary>
            private static Dictionary<uint, float> Row(NifItem weights, NifItem indices, NifItem bones)
            {
                var row = new Dictionary<uint, float>();

                for (int k = 0; k < weights.Children.Count && k < indices.Children.Count; k++)
                {
                    float w = weights.Children[k].Value.ToFloat();

                    if (w <= 0f)
                        continue;

                    var local = (int)indices.Children[k].Value.ToUInt();

                    if (local >= 0 && local < bones.Children.Count)
                        row[bones.Children[local].Value.ToUInt()] = w;
                }

                return row;
            }

            private static string Spell(Dictionary<uint, float> row) =>
                string.Join(
                    " ",
                    row.OrderBy(p => p.Key).Select(p => string.Create(
                        System.Globalization.CultureInfo.InvariantCulture, $"{p.Key}:{p.Value:F3}")));

            /// <summary>
            /// Lines a skin's bone list up by bone name rather than by position.
            /// </summary>
            /// <remarks>
            /// A skin names its bones in an array, and everything else about a bone --
            /// its bind transform in <c>NiSkinData</c>, the weights in each partition --
            /// is found by that array's index. The order is therefore internal
            /// bookkeeping: a file listing the head before the spine and a file listing
            /// the spine before the head describe the same skin, so long as each bone
            /// keeps its own transform and its own weights.
            ///
            /// The rebuild does not preserve it. Bones are discovered while walking the
            /// scene's deformers, which is a different traversal from the one that wrote
            /// the file, and 141 of 2,184 vanilla skins come back with the same bones in
            /// a different order.
            ///
            /// Compared position by position that reads as every bone being wrong: a
            /// list rotated by one reports a difference on each entry, and on each
            /// entry of the parallel <c>Bone List</c> beside it -- some 7,400 field
            /// differences from 141 skins, drowning whatever else those files had to
            /// say. So the two sides are matched by name here, and the same matching is
            /// applied to <c>Bone List</c>, which is ordered by the same index.
            ///
            /// A bone the rebuild does not have is left alone: that is a real
            /// difference, and it stays reported as one.
            /// </remarks>
            private void AlignBones(NifItem skinLeft, NifItem skinRight)
            {
                if (left.FindItem(skinLeft, "Bones") is not { Children.Count: > 0 } bonesLeft
                    || right.FindItem(skinRight, "Bones") is not { } bonesRight
                    || bonesLeft.Children.Count != bonesRight.Children.Count)
                {
                    return;
                }

                List<string> wanted = Names(left, bonesLeft), have = Names(right, bonesRight);

                var order = new int[wanted.Count];
                var taken = new bool[have.Count];
                bool moved = false;

                for (int i = 0; i < wanted.Count; i++)
                {
                    int found = -1;

                    for (int j = 0; j < have.Count && found < 0; j++)
                    {
                        if (!taken[j] && have[j] == wanted[i])
                            found = j;
                    }

                    if (found < 0)
                        return;

                    taken[found] = true;
                    order[i] = found;
                    moved |= found != i;
                }

                if (!moved)
                    return;

                _permuted[bonesLeft] = order;

                // The bind transforms, which the same index addresses.
                if (left.FindItem(skinLeft, "Data") is { } dataLeft
                    && right.FindItem(skinRight, "Data") is { } dataRight
                    && left.GetBlock(dataLeft) is { } skinDataLeft
                    && right.GetBlock(dataRight) is { } skinDataRight
                    && left.FindItem(skinDataLeft, "Bone List") is { } listLeft
                    && right.FindItem(skinDataRight, "Bone List") is { } listRight
                    && listLeft.Children.Count == order.Length
                    && listRight.Children.Count == order.Length)
                {
                    _permuted[listLeft] = order;
                }
            }

            /// <summary>
            /// Each controlled block, by what it drives.
            /// </summary>
            /// <remarks>
            /// A sequence's `Controlled Blocks` say which node, which property of it,
            /// which controller and which interpolator a track belongs to. Together
            /// those name the thing being driven, and the row's place in the array names
            /// nothing: the engine walks the list and binds each entry by what it says.
            ///
            /// The rebuild does not preserve the order -- the tracks come back grouped
            /// the way the scene stores them -- and compared by position that reports
            /// every field of every row after the first shift. It arrives as neat
            /// symmetric pairs, which is the giveaway: 442 rows reporting
            /// `BSEffectShaderProperty vs ''` against 430 reporting `'' vs
            /// BSEffectShaderProperty`, 327 `NiTransformController vs NiVisController`
            /// against 318 the other way about.
            ///
            /// Matched whole or not at all. A sequence that gained or lost a controlled
            /// block is a different sequence, and pairing what is left over would say
            /// one row is wrong where the truth is that a row is missing.
            /// </remarks>
            private static List<string> ControlledBlocks(NifModel model, NifItem array)
            {
                var keys = new List<string>(array.Children.Count);

                foreach (NifItem entry in array.Children)
                {
                    keys.Add(string.Join(
                        "\u0000",
                        model.GetString(entry, "Node Name"),
                        model.GetString(entry, "Property Type"),
                        model.GetString(entry, "Controller Type"),
                        model.GetString(entry, "Controller ID"),
                        model.GetString(entry, "Interpolator ID")));
                }

                return keys;
            }

            /// <summary>
            /// Each entry of an object palette, by the name it is looked up under.
            /// </summary>
            /// <remarks>
            /// A `NiDefaultAVObjectPalette` is a lookup table: a sequence names a node
            /// and the palette says which block that is. The order is the table's own
            /// business and Bethesda's is not one this reproduces --
            /// `dlc1protoswingingbridge.nif` lists Bone00, Bone01, Bone05, Bone04,
            /// Bone06, Bone03, Bone02.
            ///
            /// Worth matching rather than reporting, because the comparison does not
            /// stop at the entry: it follows `AV Object` into the block, so one shifted
            /// palette pairs a billboard with a particle system and reports every field
            /// of both, down to the vertices of a mesh hanging off the wrong entry.
            ///
            /// Note the name is a `SizedString` and not an index into the header's
            /// table -- the palette is meant to be readable without it -- so resolving
            /// it as an index gives nothing, and every entry keys alike.
            /// </remarks>
            private static List<string> PaletteNames(NifModel model, NifItem array)
            {
                var names = new List<string>(array.Children.Count);

                foreach (NifItem entry in array.Children)
                {
                    if (model.FindItem(entry, "Name") is not { } n)
                    {
                        names.Add(string.Empty);
                        continue;
                    }

                    names.Add(n.Value.Type == NifValueType.StringIndex
                        ? model.ResolveString(n)
                        : n.Value.ToString());
                }

                return names;
            }

            /// <summary>
            /// Lines two object palettes up, leaving each side's root row out of it.
            /// </summary>
            /// <remarks>
            /// A palette lists the blocks a sequence may name, and whether the file's
            /// own root is among them is not something the file states. 15 of 329
            /// vanilla palettes hold it and 314 do not, and nothing separates the two
            /// groups -- see `NifAnimWriter.WritePalette`, which records the seven
            /// things measured. A rebuilt palette leaves it out, which is right 314
            /// times, and the 15 differ by exactly that one row.
            ///
            /// So the row is dropped from both sides before matching. Not excused after
            /// the fact -- dropped, so it cannot hide anything either: every other row
            /// is still matched by name and compared, and a palette that differs by
            /// anything more than its root still reports it.
            /// </remarks>
            private void AlignPalette(NifItem left_, NifItem right_)
            {
                if (_permuted.ContainsKey(left_) || _ragged.Contains(left_))
                    return;

                List<string> wanted = PaletteNames(left, left_), have = PaletteNames(right, right_);
                string leftRoot = RootName(left), rightRoot = RootName(right);

                var keptRight = new List<int>();

                for (int j = 0; j < have.Count; j++)
                {
                    if (rightRoot.Length == 0 || have[j] != rightRoot)
                        keptRight.Add(j);
                }

                int dropped = wanted.Count(n => leftRoot.Length > 0 && n == leftRoot);

                if (wanted.Count - dropped != keptRight.Count)
                    return;

                var order = new int[wanted.Count];
                var taken = new bool[have.Count];
                var unmatched = new List<int>();
                bool moved = false;

                for (int i = 0; i < wanted.Count; i++)
                {
                    if (leftRoot.Length > 0 && wanted[i] == leftRoot)
                    {
                        order[i] = -1;
                        moved = true;
                        continue;
                    }

                    int found = -1;

                    foreach (int j in keptRight)
                    {
                        if (found < 0 && !taken[j] && have[j] == wanted[i])
                            found = j;
                    }

                    if (found < 0) { unmatched.Add(i); continue; }

                    taken[found] = true;
                    order[i] = found;
                    moved |= found != i;
                }

                // Rows the two sides genuinely disagree about, paired so their names are
                // reported without either block being walked.
                foreach (int i in unmatched)
                {
                    int spare = keptRight.FirstOrDefault(j => !taken[j], -1);

                    if (spare < 0)
                        return;

                    taken[spare] = true;
                    order[i] = spare;
                    moved = true;
                    _unpaired[left_.Children[i]] = (wanted[i], have[spare]);
                }

                if (!moved)
                    return;

                _permuted[left_] = order;

                if (dropped > 0 || keptRight.Count != have.Count)
                    _ragged.Add(left_);
            }

            /// <summary>
            /// Lines two extra data lists up by what they hold, not by position.
            /// </summary>
            /// <remarks>
            /// The engine looks an annotation up by name -- `BSX`, `INV`, the behaviour
            /// graph's path -- so where it sits in the list says nothing, and the
            /// rebuild does not preserve it: `BSXFlags` is recalculated and appended,
            /// which shifts everything Bethesda had after it.
            ///
            /// Compared position by position that reads as the wrong *block* at each
            /// slot -- 36 files reporting `BSInvMarker vs NiStringExtraData` and 27
            /// `BSInvMarker vs BSXFlags` over a 1,200-mesh sample, none of which has
            /// anything wrong with its inventory marker. The baseline records this
            /// under `NiIntegerExtraData` alone, which excuses the one block that moved
            /// and not the ones it displaced.
            ///
            /// Matched on the block's class and its own name together, since a file can
            /// hold several `NiStringExtraData` telling them apart by name. A list that
            /// gained or lost a block cannot be matched this way and is left to report
            /// itself, which is the case worth keeping.
            /// </remarks>
            /// <param name="whole">
            /// Whether every entry must match for the alignment to be used. An extra
            /// data list either holds the same annotations or is a different list, and
            /// a partial match there would pair unrelated blocks. A palette is a table
            /// of independent rows, so the ones that do match are worth pairing however
            /// the rest turn out.
            /// </param>
            private void Align(
                NifItem left_,
                NifItem right_,
                Func<NifModel, NifItem, List<string>> key,
                bool whole)
            {
                if (left_.Children.Count == 0 || left_.Children.Count != right_.Children.Count)
                    return;

                List<string> wanted = key(left, left_), have = key(right, right_);

                var order = new int[wanted.Count];
                var taken = new bool[have.Count];
                var unmatched = new List<int>();
                bool moved = false;

                for (int i = 0; i < wanted.Count; i++)
                {
                    int found = -1;

                    for (int j = 0; j < have.Count && found < 0; j++)
                    {
                        if (!taken[j] && have[j] == wanted[i])
                            found = j;
                    }

                    if (found < 0)
                    {
                        if (whole)
                            return;

                        unmatched.Add(i);
                        continue;
                    }

                    taken[found] = true;
                    order[i] = found;
                    moved |= found != i;
                }

                // Whatever is left over on each side, paired in the order it sits in
                // and marked as the disagreement it is: the names are reported and
                // neither block behind them is walked.
                int spare = 0;

                foreach (int i in unmatched)
                {
                    while (spare < taken.Length && taken[spare])
                        spare++;

                    if (spare >= taken.Length)
                        return;

                    taken[spare] = true;
                    order[i] = spare;
                    moved |= spare != i;

                    _unpaired[left_.Children[i]] = (wanted[i], have[spare]);
                }

                if (moved)
                    _permuted[left_] = order;
            }

            /// <summary>
            /// Whether this is the `NiSkinData` kind of `Vertex Weights` -- the kind
            /// whose rows name the vertex they weight.
            /// </summary>
            /// <remarks>
            /// `NiSkinPartition` has a field of the same name holding four weights per
            /// vertex, addressed by position and with no index in it. That one is not
            /// a list addressed by name and must not be realigned.
            /// </remarks>
            private static bool HasVertexIndices(NifItem array) =>
                array.Children.Count > 0
                && array.Children[0].Children.Any(c => c.Name == "Index");

            /// <summary>Which vertex each of a bone's weights is for.</summary>
            /// <remarks>
            /// A bone's weight list is addressed by the vertex it names, not by where
            /// the entry sits: `NiSkinData` says "this bone moves vertex 412 by 0.6",
            /// and it says the same thing wherever in the list it says it. Compared by
            /// position, a list holding the same weights in another order reads as every
            /// entry being wrong.
            ///
            /// This is the seventh list in this format to need it, after the skin bones,
            /// the extra data, the object palette, the partition rows and the controlled
            /// blocks. The shape of the mistake does not change: a list whose entries
            /// carry their own identity is being compared by index.
            ///
            /// Measured before assuming it: on `0000282d` and `hair13`, every bone whose
            /// list differed held exactly the same (vertex, weight) pairs in a different
            /// order -- 6 bones reordered, 11 identical, none actually different.
            ///
            /// Keyed on the vertex alone, and matched whole, so a bone that really does
            /// weight a different set of vertices still fails rather than being paired
            /// up somehow.
            /// </remarks>
            private static List<string> WeightedVertices(NifModel model, NifItem array)
            {
                var keys = new List<string>(array.Children.Count);

                foreach (NifItem row in array.Children)
                {
                    keys.Add(row.Children.FirstOrDefault(c => c.Name == "Index") is { } index
                        ? index.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty);
                }

                return keys;
            }

            /// <summary>
            /// Each entry of an extra data list -- or of a controller's extra targets --
            /// as class and name.
            /// </summary>
            /// <remarks>
            /// `NiMultiTargetTransformController` names the nodes it drives besides its
            /// own target, and the list is a set: the controller drives all of them, and
            /// which one it names first changes nothing. Compared by position, a file
            /// whose targets come back in another order reads as every entry pointing at
            /// the wrong node -- `intperkskydome` swaps `WarGas` and `CameraPosition` and
            /// reads as two faults.
            ///
            /// Matched whole, so a controller that really drives a different set of nodes
            /// still fails rather than being paired up somehow.
            /// </remarks>
            private static List<string> Annotations(NifModel model, NifItem array)
            {
                var names = new List<string>(array.Children.Count);

                foreach (NifItem link in array.Children)
                {
                    names.Add(model.GetBlock(link) is { } block
                        ? $"{block.Name}\u0000{model.GetName(block)}"
                        : string.Empty);
                }

                return names;
            }

            /// <summary>The names of the blocks an array of links points at.</summary>
            private static List<string> Names(NifModel model, NifItem array)
            {
                var names = new List<string>(array.Children.Count);

                foreach (NifItem link in array.Children)
                    names.Add(model.GetBlock(link) is { } block ? model.GetName(block) : string.Empty);

                return names;
            }

            /// <summary>
            /// The transform the exporter bakes into a block's geometry, if any.
            /// </summary>
            /// <remarks>
            /// An unskinned shape's own transform is baked into its vertices and its
            /// transform reset, which the spec records at §2 and ck-cmd does the same.
            /// A skinned shape keeps its transform -- the skin applies it -- so nothing
            /// is baked and there is nothing to undo.
            ///
            /// For LE the geometry is a block of its own and the transform is on the
            /// `NiTriShape` above it, so the owner is looked up through whoever points
            /// at it.
            /// </remarks>
            private NifTransform? BakedTransform(NifItem owner)
            {
                NifItem? shape = owner;

                if (owner.Name is "NiTriShapeData" or "NiTriStripsData")
                {
                    shape = left.Blocks.FirstOrDefault(
                        b => left.BlockInherits(b, "NiGeometry") && left.GetRef(b, "Data") == owner);
                }

                if (shape is null || !left.BlockInherits(shape, "NiAVObject"))
                    return null;

                // Skinned: identity, so a difference is a real one.
                return left.GetRef(shape, "Skin") is not null || left.GetRef(shape, "Skin Instance") is not null
                    ? null
                    : left.GetTransform(shape);
            }

            private static uint SNormToByte(float value) =>
                (uint)Math.Clamp(MathF.Round((value + 1f) / 2f * 255f), 0f, 255f);

            private static float ByteToSNorm(NifItem? item) =>
                item is null ? 0f : (float)(item.Value.ToUInt() / 255.0 * 2.0 - 1.0);

            /// <summary>
            /// Whether the two differ by exactly the shape transform that was baked in.
            /// </summary>
            /// <remarks>
            /// This replaces excusing the fields outright. `Vertex` and `Normal` were
            /// listed as known gaps, and the tangent frame was covered by a blanket
            /// "tangent space regenerated" entry that hid the same thing -- so a shape
            /// that lost its geometry for any *other* reason was excused along with it.
            ///
            /// Applying the transform and requiring the result to match exactly says
            /// what the gap actually is: the geometry is in the parent's space, and
            /// nothing else about it has moved. On `TestNifFile_OrderedNode_SE` and
            /// `TestNifFile_DeepGraph_SE` the error is 0 on every vertex of every shape,
            /// not merely small.
            ///
            /// The bitangent is reconstructed from its three lanes before the rotation
            /// and re-quantised afterwards, since Y and Z travel as bytes.
            /// </remarks>
            /// <summary>
            /// For a particle array entry, the shape vertex it was copied from.
            /// </summary>
            /// <remarks>
            /// `Particle Vertices` and `Particle Normals` sit beside the vertex buffer
            /// and hold the same geometry at half precision, one entry per vertex, in
            /// the same order (§5A). So entry *i* answers to `Vertex Data[i]`.
            /// </remarks>
            private NifVector3? ParticleSource(NifItem item)
            {
                if (item.Name is not ("Particle Vertices" or "Particle Normals"))
                    return null;

                if (item.Parent is not { } array || _owner is null)
                    return null;

                int at = array.Children.IndexOf(item);

                if (at < 0 || left.FindItem(_owner, "Vertex Data") is not { } vertices
                    || at >= vertices.Children.Count)
                {
                    return null;
                }

                string field = item.Name == "Particle Vertices" ? "Vertex" : "Normal";

                return left.FindItem(vertices.Children[at], field) is { } source
                    ? source.Value.Get<NifVector3>()
                    : null;
            }

            private bool BakedTransformExplains(NifItem a, NifItem b)
            {
                if (_owner is null)
                    return false;

                // `Particle Vertices` and `Particle Normals` are the second, plainer
                // copy of the same geometry that a mesh emitter scatters over, so the
                // transform baked into the first is baked into them too.
                bool position = a.Name is "Vertex" or "Vertices" or "Particle Vertices";
                bool direction = a.Name is "Normal" or "Normals" or "Particle Normals"
                                        or "Tangent" or "Tangents" or "Bitangents";
                bool lane = a.Name is "Bitangent X" or "Bitangent Y" or "Bitangent Z";

                if (!position && !direction && !lane)
                    return false;

                if (BakedTransform(_owner) is not { } transform)
                    return false;

                if (lane)
                {
                    if (a.Parent is not { } row || b.Parent is null)
                        return false;

                    var bitangent = new NifVector3(
                        row.Children.FirstOrDefault(c => c.Name == "Bitangent X")?.Value.ToFloat() ?? 0f,
                        ByteToSNorm(row.Children.FirstOrDefault(c => c.Name == "Bitangent Y")),
                        ByteToSNorm(row.Children.FirstOrDefault(c => c.Name == "Bitangent Z")));

                    NifVector3 turned = transform.ApplyDirection(bitangent);

                    return a.Name switch
                    {
                        "Bitangent X" => turned.X == b.Value.ToFloat(),
                        "Bitangent Y" => SNormToByte(turned.Y) == b.Value.ToUInt(),
                        _ => SNormToByte(turned.Z) == b.Value.ToUInt(),
                    };
                }

                // Every spelling of a three-vector, which is four of them: a position is
                // a `Vector3`, a normal and a tangent are `ByteVector3`, and a particle
                // copy's positions and normals are `HalfVector3`. Each has been added
                // here only after a field went silently unchecked for want of it, so the
                // set is taken from `NifValue` itself -- these are exactly the types
                // `Get<NifVector3>` reads.
                if (a.Value.Type != b.Value.Type
                    || a.Value.Type is not (NifValueType.Vector3 or NifValueType.HalfVector3
                                            or NifValueType.UshortVector3 or NifValueType.ByteVector3))
                {
                    return false;
                }

                // A particle copy is compared against the geometry it is a copy of, not
                // against itself. The emitter's arrays are `HalfVector3`, and vanilla
                // rounded them from the authoring data once; a rebuild can only round
                // what the vertex buffer holds. Comparing half against half would be
                // comparing one rounding with two, and the shapes disagree in the last
                // digit for no reason either side could fix. Taking the source's own
                // vertex -- the full-precision one the copy came from -- rounds once on
                // each side and lands exactly.
                NifVector3 from = ParticleSource(a) ?? a.Value.Get<NifVector3>();
                NifVector3 expected = position ? transform.Apply(from) : transform.ApplyDirection(from);

                // Put the turned vector through the same encoding before comparing, so a
                // byte-quantised field is judged on the bytes it would actually be
                // written as rather than on a float that cannot be stored.
                var encoded = new NifValue(b.Value.Type);
                encoded.Set(expected);

                return encoded.Get<NifVector3>().Equals(b.Value.Get<NifVector3>());
            }

            /// <summary>
            /// Whether the shape being compared declares a tangent frame at all.
            /// </summary>
            /// <remarks>
            /// nif.xml conditions the three bitangent lanes unevenly, and the middle one
            /// is the odd one out:
            ///
            /// | Field | Condition | Needs |
            /// | --- | --- | --- |
            /// | `Bitangent X` | `(ARG &amp; 0x11) == 0x11` | Vertex and Tangent |
            /// | `Bitangent Z` | `(ARG &amp; 0x18) == 0x18` | Normal and Tangent |
            /// | `Bitangent Y` | `ARG &amp; 0x8` | Normal alone |
            ///
            /// So on a shape with normals and no tangents, `Bitangent Y` stays live while
            /// everything else about the frame goes away. That is a statement about the
            /// layout rather than about the data: a normal occupies four bytes, and the
            /// fourth is this lane whether or not there is a bitangent to put in it.
            ///
            /// What the game's files leave there is whatever the exporter had lying
            /// around. `camcraneup256x200`'s editor marker holds 255 on two of its 296
            /// vertices and 0 on the rest, with every tangent (0, 0, 0) and both other
            /// lanes dead. There is nothing to carry and nothing a converter could be
            /// right about, so the comparison passes over it -- 49 files in a 3,000-mesh
            /// sample differ in this and nothing else.
            ///
            /// Narrow on purpose: only this lane, and only where the shape's own
            /// `Vertex Desc` says it has no tangents. A shape that declares a frame is
            /// held to all three lanes as before.
            /// </remarks>
            private bool DeclaresTangents()
            {
                if (_owner is null || left.FindItem(_owner, "Vertex Desc") is not { } desc)
                    return true;

                ulong attributes = (desc.Value.ToUInt64() >> BSVertexDesc.Member.VertexAttributes) & 0xFFF;

                return ((VertexFlags)attributes & VertexFlags.Tangent) != 0;
            }

            /// <summary>
            /// Pairs two child lists that hold the same nodes and differ only in empty
            /// slots.
            /// </summary>
            /// <remarks>
            /// A `Children` array may carry a link pointing at nothing. 123 of the
            /// 16,483 `NiNode`s in a 4,000-mesh sample do, 188 slots between them, and
            /// `treepineforest05`'s multi-bound node is one of them. Nothing reads an
            /// empty slot -- it is the absence of a child rather than a child -- and a
            /// rebuilt file writes only the children it has, so the lengths disagree.
            ///
            /// Excusing `Children` outright would excuse a node genuinely losing one,
            /// which is among the worst things this converter could do quietly. So the
            /// two lists are matched only when the blocks they actually name are the
            /// same, in the same order; the empty slots are then dropped from the walk
            /// and everything else is compared as usual. A list that has really lost a
            /// child still fails on its length.
            /// </remarks>
            private void AlignAroundEmptyChildren(NifItem left_, NifItem right_)
            {
                var filled = new List<int>();

                for (int i = 0; i < left_.Children.Count; i++)
                    if (left.GetBlock(left_.Children[i]) is not null)
                        filled.Add(i);

                var theirs = new List<int>();

                for (int i = 0; i < right_.Children.Count; i++)
                    if (right.GetBlock(right_.Children[i]) is not null)
                        theirs.Add(i);

                if (filled.Count != theirs.Count)
                    return;

                // The same blocks, named in the same order. Compared by class and name,
                // which is what identifies a block across two files.
                for (int i = 0; i < filled.Count; i++)
                {
                    NifItem mine = left.GetBlock(left_.Children[filled[i]])!;
                    NifItem yours = right.GetBlock(right_.Children[theirs[i]])!;

                    if (mine.Name != yours.Name || left.GetName(mine) != right.GetName(yours))
                        return;
                }

                var order = new int[left_.Children.Count];
                Array.Fill(order, -1);

                for (int i = 0; i < filled.Count; i++)
                    order[filled[i]] = theirs[i];

                _permuted[left_] = order;
                _ragged.Add(left_);
            }

            /// <summary>
            /// Whether a partition weight differs because the file disagrees with itself.
            /// </summary>
            /// <remarks>
            /// A skinned mesh states its weights twice: `NiSkinData` holds what was
            /// authored, and `NiSkinPartition` holds the four-slot copy the renderer
            /// reads. A rebuild has only one of them to work from and rebuilds the cache
            /// from the authored weights, so where a file's two copies disagree, ours
            /// matches the authored one and the file's cache does not.
            ///
            /// The files really do disagree. `hair13` has 1,711 weights in its partitions;
            /// 1,667 match its own `NiSkinData` and 44 do not, by up to 0.0068 -- and 44
            /// is exactly the number the sweep reports for it.
            ///
            /// So this is not "weights may differ": it is "this weight may differ from the
            /// cache when it equals what the file itself says was authored". Anything else
            /// -- a weight that matches neither copy, a renormalisation of our own, a
            /// dropped influence -- still fails. Reaching the authored value means walking
            /// from the slot back out to the partition, through `Vertex Map` for the
            /// vertex and `Bones` for the bone, and into the `NiSkinData` beside it.
            /// </remarks>
            private bool AuthoredWeightExplains(NifItem slot, NifItem theirs)
            {
                // slot -> row -> array -> partition, on our side and theirs alike.
                if (slot.Parent is not { } row
                    || row.Parent is not { } array
                    || array.Parent is not { } partition
                    || array.Name != "Vertex Weights"
                    || theirs.Parent is not { } theirRow
                    || theirRow.Parent is not { } theirArray
                    || theirArray.Parent is not { } theirPartition)
                {
                    return false;
                }

                int at = array.Children.IndexOf(row);
                int which = row.Children.IndexOf(slot);
                int theirAt = theirArray.Children.IndexOf(theirRow);

                if (at < 0 || which < 0 || theirAt < 0)
                    return false;

                // The vertex, which the rows have already been paired on.
                if (Child(partition, "Vertex Map") is not { } map || at >= map.Children.Count)
                    return false;

                uint vertex = map.Children[at].Value.ToUInt();

                // **The bone is read from the side the weight came from.** Reading it
                // from the source instead was the flaw here: the four slots of a row are
                // not ordered, so slot 2 on one side need not be the bone slot 2 names on
                // the other, and the lookup then asked about a bone this weight was never
                // for. That is the same mistake as comparing partition rows in place, one
                // level further down.
                if (BoneNameOf(right, theirPartition, theirAt, which) is not { } name)
                    return false;

                // What the file itself says was authored for that bone and vertex. Only
                // when the skin names the bone once: a list naming it twice -- a tree's
                // does, one set per level of detail -- cannot say which entry is meant.
                if (_owner is null
                    || left.Blocks.FirstOrDefault(
                           b => left.GetRef(b, "Skin Partition") == _owner) is not { } instance
                    || left.GetRef(instance, "Data") is not { } skinData
                    || Child(skinData, "Bone List") is not { } list)
                {
                    return false;
                }

                var bones = left.GetRefArray(instance, "Bones").Select(left.GetName).ToList();
                int bone = bones.IndexOf(name);

                if (bone < 0 || bones.LastIndexOf(name) != bone || bone >= list.Children.Count)
                    return false;

                if (Child(list.Children[bone], "Vertex Weights") is not { } authored)
                    return false;

                // How much of the vertex's authored weight the row can actually hold. A
                // row has four slots and `NiSkinData` is not bound by that: `falmervampire
                // feral` authors 141 vertices with five influences apiece, all of them
                // drawn by a single partition. So the four heaviest are kept and scaled
                // back up to one -- a derivable answer, and the one this port writes.
                //
                // Without this the comparison asked whether our value *equals* the
                // authored one, which for such a vertex it cannot: vertex 81's five
                // influences leave 0.93522 after the smallest goes, and every kept weight
                // is 1.0693 times what was authored.
                float scale = RenormalisationFor(list, vertex, row.Children.Count);
                bool trimmed = InfluenceCount(list, vertex) > row.Children.Count;

                foreach (NifItem entry in authored.Children)
                {
                    if (entry.Children.FirstOrDefault(c => c.Name == "Index") is not { } index
                        || index.Value.ToUInt() != vertex
                        || entry.Children.FirstOrDefault(c => c.Name == "Weight") is not { } weight)
                    {
                        continue;
                    }

                    if (scale != 1f)
                    {
                        // The row could not hold everything the vertex was authored with,
                        // so the file had to choose -- and it does not record what it
                        // chose. Ours keeps the four heaviest and scales them back to one,
                        // which is checked first because it is checkable.
                        var scaled = new NifValue(weight.Value.Type);
                        scaled.SetFloat(weight.Value.ToFloat() * scale);

                        if (scaled.ToString() == theirs.Value.ToString())
                            return true;

                        // Everything fitted, so the scaling is the only thing that
                        // happened to this weight and it had a right answer. Not
                        // matching it is a real difference.
                        if (!trimmed)
                            return false;

                        // And where it does not match, the row is passed over rather than
                        // reported, because there is nothing to be right about.
                        // `falmervampireferal`'s cache keeps `L UpperArm` as a slot at
                        // weight zero while dropping a heavier influence, and moves
                        // `Spine1` from 0.37286 to 0.48826 -- ratios of 1.0846, 1.0949,
                        // 1.0498 and 0.9546 against its own authored weights, so no
                        // scaling of any kind reaches it. Which four a tool keeps, and
                        // what it does to them afterwards, is a decision taken before the
                        // file was written.
                        //
                        // Only a row the file itself had to trim. A vertex whose
                        // influences all fit is still held to the authored value exactly,
                        // which is every vertex in all but one mesh of a 2,000-mesh
                        // sample.
                        return true;
                    }

                    // Ours has to *be* the authored weight. That it merely differs from
                    // the cache is not enough.
                    //
                    // Compared as `NifValue` prints them, which is `G6` -- six
                    // significant digits, the same comparison the walk uses for every
                    // other field. Tightening this to the float itself, or to within one
                    // unit in the last place, was tried and is wrong: the weight makes
                    // the trip as a double and comes back through a renormalisation, so
                    // it agrees with the authored value to about six digits and not to
                    // the bit. The sample went from 20 divergent meshes to 27.
                    //
                    // The cost of the crude comparison is the odd weight sitting on a
                    // rounding boundary, which prints either side of it from a one-bit
                    // difference: `hair13`'s vertex 921 is authored 0.790237606 and
                    // rebuilt a shade below, and reads as 0.790238 against 0.790237.
                    // One mesh in the sample keeps a difference for that reason.
                    return weight.Value.ToString() == theirs.Value.ToString();
                }

                return false;
            }

            /// <summary>How many influences a vertex was authored with.</summary>
            private int InfluenceCount(NifItem boneList, uint vertex)
            {
                int count = 0;

                foreach (NifItem entry in boneList.Children)
                {
                    if (Child(entry, "Vertex Weights") is not { } list)
                        continue;

                    foreach (NifItem row in list.Children)
                    {
                        if (row.Children.FirstOrDefault(c => c.Name == "Index") is { } index
                            && index.Value.ToUInt() == vertex)
                        {
                            count++;
                        }
                    }
                }

                return count;
            }

            /// <summary>
            /// What the kept weights are scaled by when a vertex has more influences than
            /// a row has slots.
            /// </summary>
            /// <remarks>
            /// One when everything fits, which is the ordinary case. Otherwise the four
            /// heaviest are kept and the rest dropped, and the total is brought back to
            /// one -- the same rule `FbxToNif.WriteVertexSkinning` follows, so this asks
            /// whether the rebuild did what it says it does rather than whether it
            /// matched the file's own cached answer, which for these vertices is not
            /// derivable from anything: `falmervampireferal` keeps a slot at weight zero
            /// in one row while dropping a heavier influence from it.
            /// </remarks>
            private float RenormalisationFor(NifItem boneList, uint vertex, int slots)
            {
                var weights = new List<float>();

                foreach (NifItem entry in boneList.Children)
                {
                    if (Child(entry, "Vertex Weights") is not { } list)
                        continue;

                    foreach (NifItem row in list.Children)
                    {
                        if (row.Children.FirstOrDefault(c => c.Name == "Index") is { } index
                            && index.Value.ToUInt() == vertex
                            && row.Children.FirstOrDefault(c => c.Name == "Weight") is { } weight)
                        {
                            weights.Add(weight.Value.ToFloat());
                        }
                    }
                }

                // The weights that survive into the row, which is all of them when they
                // fit. Scaled so they total one, which is what the writer does and what
                // the renderer expects -- and a no-op when they already do.
                float kept = weights.OrderByDescending(w => w).Take(slots).Sum();

                return kept > 0f ? 1f / kept : 1f;
            }

            /// <summary>The bone a partition's weight slot names, by node name.</summary>
            private static string? BoneNameOf(NifModel model, NifItem partition, int row, int slot)
            {
                if (partition.Children.FirstOrDefault(
                        c => c.Name == "Bone Indices" && model.EvalCondition(c)) is not { } indices
                    || row >= indices.Children.Count
                    || slot >= indices.Children[row].Children.Count
                    || partition.Children.FirstOrDefault(
                        c => c.Name == "Bones" && model.EvalCondition(c)) is not { } bones)
                {
                    return null;
                }

                int local = (int)indices.Children[row].Children[slot].Value.ToUInt();

                if (local >= bones.Children.Count)
                    return null;

                int index = (int)bones.Children[local].Value.ToUInt();

                if (partition.Parent?.Parent is not { } block)
                    return null;

                NifItem? instance = model.Blocks.FirstOrDefault(b => model.GetRef(b, "Skin Partition") == block);

                if (instance is null)
                    return null;

                var list = model.GetRefArray(instance, "Bones").ToList();

                return index < list.Count ? model.GetName(list[index]) : null;
            }

            /// <summary>A block's live child of that name.</summary>
            private NifItem? Child(NifItem parent, string name) =>
                parent.Children.FirstOrDefault(c => c.Name == name && left.EvalCondition(c));

            /// <summary>
            /// Pairs a partition's per-vertex rows by the vertex they stand for.
            /// </summary>
            /// <remarks>
            /// A partition addresses its vertices through `Vertex Map`, and everything
            /// else in it -- `Vertex Weights`, `Bone Indices` -- is a row per entry of
            /// that map. The order of the map is an accepted gap (§7.3): it carries no
            /// meaning, ours is ascending, and vanilla's follows no rule that can be
            /// re-derived.
            ///
            /// **An accepted gap is not inert.** With the two maps in different orders,
            /// row 823 on one side is a different vertex from row 823 on the other, and
            /// comparing the rows in place compares two unrelated vertices. `hair13`
            /// reads as though two vertices swapped weights, and they did -- they are in
            /// different places in the two maps.
            ///
            /// Worse, the exception for a file that contradicts its own weights reaches
            /// the authored weight through the *source's* map and compares it against our
            /// value at the same row, so where the orders diverge its verdict meant
            /// nothing in either direction.
            ///
            /// So the rows are paired by the vertex each stands for, and everything
            /// inside them is then compared vertex against the same vertex. A partition
            /// whose maps do not name the same vertices is left alone and fails as
            /// before.
            /// </remarks>
            private void AlignByVertexMap(NifItem left_, NifItem right_)
            {
                if (left_.Parent is not { } mine || right_.Parent is not { } theirs)
                    return;

                if (Child(mine, "Vertex Map") is not { } ours
                    || theirs.Children.FirstOrDefault(
                           c => c.Name == "Vertex Map" && right.EvalCondition(c)) is not { } yours)
                {
                    return;
                }

                if (ours.Children.Count != yours.Children.Count
                    || ours.Children.Count != left_.Children.Count)
                {
                    return;
                }

                var at = new Dictionary<uint, int>(yours.Children.Count);

                for (int i = 0; i < yours.Children.Count; i++)
                {
                    // A map naming one vertex twice is not one this can pair.
                    if (!at.TryAdd(yours.Children[i].Value.ToUInt(), i))
                        return;
                }

                var order = new int[left_.Children.Count];
                bool moved = false;

                for (int i = 0; i < order.Length; i++)
                {
                    if (!at.TryGetValue(ours.Children[i].Value.ToUInt(), out int found))
                        return;

                    order[i] = found;
                    moved |= found != i;
                }

                if (moved)
                    _permuted[left_] = order;
            }

            /// <summary>
            /// Whether a rotation key differs by exactly what the trip through FBX costs.
            /// </summary>
            /// <remarks>
            /// A NIF stores a rotation track either as quaternion keys or as three
            /// `XYZ Rotations` groups; **FBX has only the second**, as `AnimTrack`
            /// records. So a quaternion key is decomposed to Euler XYZ degrees on the way
            /// out and rebuilt from them on the way back, and what returns is the same
            /// rotation carried to fewer digits: `blacksmithforgemarker` sends
            /// (0.500559, 0.501334, 0.499604, -0.498498) and gets
            /// (0.500082, 0.499918, 0.500082, -0.499918).
            ///
            /// Checked rather than tolerated. The source's own quaternion is put through
            /// the same decomposition and recomposition, and the result has to match
            /// exactly -- so a key that came back as a *different* rotation still fails,
            /// however close, and no threshold has to be invented for how close is close
            /// enough. The same technique settles the particle copy's half-float rounding
            /// and the baked transform's byte-quantised normals.
            /// </remarks>
            /// <summary>
            /// The same question for a node's rotation, which is a matrix rather than a
            /// quaternion and travels the same way.
            /// </summary>
            /// <remarks>
            /// A node's transform rides on FBX's `Lcl Rotation`, which is Euler XYZ in
            /// degrees -- `FbxMeshWriter` writes `ToEulerDegrees` and `FbxToNif` reads it
            /// back through `RotationFromEulerDegrees`. A matrix carrying a component of
            /// about 1e-4 off the axis loses it there: `boarriekling_varianta` sends
            /// -9.04376E-05 and gets -4.37114E-08.
            ///
            /// Put through the same two conversions, the source's own matrix lands on
            /// exactly what came back. A node that really turned somewhere else still
            /// fails.
            /// </remarks>
            private static bool MatrixSurvivesTheEulerTrip(NifItem a, NifItem b)
            {
                var mine = new NifTransform(new NifVector3(), a.Value.Get<NifMatrix33>(), 1f);
                NifVector3 euler = mine.ToEulerDegrees();

                NifMatrix33 expected = NifTransform.RotationFromEulerDegrees(euler.X, euler.Y, euler.Z);

                return expected.Equals(b.Value.Get<NifMatrix33>());
            }

            private static bool SurvivesTheEulerTrip(NifItem a, NifItem b)
            {
                var mine = a.Value.Get<NifQuat>();
                var theirs = b.Value.Get<NifQuat>();

                NifVector3 euler =
                    new NifTransform(new NifVector3(), NifTransform.RotationFromQuaternion(mine), 1f)
                        .ToEulerDegrees();

                NifQuat expected =
                    new NifTransform(
                            new NifVector3(),
                            NifTransform.RotationFromEulerDegrees(euler.X, euler.Y, euler.Z),
                            1f)
                        .ToQuaternion();

                return expected.Equals(theirs) || NegatedQuaternion(expected.ToString(), theirs.ToString());
            }

            private bool Same(NifItem a, NifItem b)
            {
                if (a.Name == "Flags" && left.BlockInherits(_owner, "NiAVObject"))
                    return (a.Value.ToUInt() & ~IgnoredAvFlag) == (b.Value.ToUInt() & ~IgnoredAvFlag);

                // Geometry displaced by exactly the transform the exporter bakes in.
                if (BakedTransformExplains(a, b))
                    return true;

                // A bitangent lane on a shape that declares no tangent frame.
                if (a.Name == "Bitangent Y" && !DeclaresTangents())
                    return true;

                // A cached partition weight the file's own authored weights contradict.
                if (a.Name == "Vertex Weights" && AuthoredWeightExplains(a, b))
                    return true;

                // A quaternion and its negation are the same rotation: q and -q turn a
                // body to exactly the same place, and which one a decomposition hands
                // back is an accident of the arithmetic. Reported as a difference it is
                // pure noise, and noise in this comparison is what lets a real
                // difference hide in a long list.
                //
                // Only for a field actually spelled `Rotation`. A negated normal is not
                // the same normal -- it is the surface pointing the other way.
                // Any quaternion, not only a field spelled `Rotation`: a rotation key's
                // is called `Value`, and q and -q are the same rotation wherever they
                // are written. Keyed on the type rather than the name, which is what
                // makes it safe -- the concern above is a Vector3 normal, and a negated
                // normal really is a different normal.
                if ((a.Name == "Rotation" || a.Value.Type is NifValueType.Quat or NifValueType.QuatXYZW)
                    && NegatedQuaternion(a.Value.ToString(), b.Value.ToString()))
                {
                    return true;
                }

                // A rotation that went out as three Euler angles and came back.
                if (a.Value.Type is NifValueType.Quat or NifValueType.QuatXYZW
                    && SurvivesTheEulerTrip(a, b))
                {
                    return true;
                }

                if (a.Value.Type is NifValueType.Matrix && MatrixSurvivesTheEulerTrip(a, b))
                    return true;

                // A half is compared as a half. The source's value came out of the file
                // and so is already rounded to the sixteen bits the field holds; the
                // rebuilt one is a full float that will be rounded only when it is
                // written. Compared as they stand, every half field in the file differs
                // in its fifth digit -- 1,084 of them on one FaceGen head -- and none of
                // it is loss: the saved bytes are identical, which the byte-for-byte
                // sweep has been saying all along.
                //
                // This is the rule this comparison keeps relearning: where a value
                // passes through a known transformation, compare against that
                // transformation's own output rather than against its input.
                if (a.Value.Type is NifValueType.Hfloat && b.Value.Type is NifValueType.Hfloat
                    && NifPack.FloatToHalf(a.Value.ToFloat()) == NifPack.FloatToHalf(b.Value.ToFloat()))
                {
                    return true;
                }

                string sa = a.Value.ToString(), sb = b.Value.ToString();

                if (sa == sb)
                    return true;

                // Everything with a float in it prints as text, so the numbers are
                // pulled back out rather than the types enumerated.
                return NumbersMatch(sa, sb);
            }

            /// <summary>Whether two four-component values differ only by sign throughout.</summary>
            private static bool NegatedQuaternion(string left, string right)
            {
                string[] a = Split(left), b = Split(right);

                if (a.Length != 4 || b.Length != 4)
                    return false;

                for (int i = 0; i < 4; i++)
                {
                    if (!float.TryParse(a[i], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(b[i], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out float y))
                    {
                        return false;
                    }

                    if (!Close(x, -y))
                        return false;
                }

                return true;
            }

            private static bool NumbersMatch(string left, string right)
            {
                string[] a = Split(left), b = Split(right);

                if (a.Length != b.Length || a.Length == 0)
                    return false;

                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] == b[i])
                        continue;

                    if (!float.TryParse(a[i], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float x)
                        || !float.TryParse(b[i], System.Globalization.NumberStyles.Float,
                                           System.Globalization.CultureInfo.InvariantCulture, out float y))
                    {
                        return false;
                    }

                    if (!Close(x, y))
                        return false;
                }

                return true;
            }

            private static string[] Split(string text) =>
                text.Split([' ', ',', '(', ')', '[', ']', ';'], StringSplitOptions.RemoveEmptyEntries);

            private static bool Close(float x, float y)
            {
                // Catches negative zero against zero, which is the same number written
                // two ways, as well as the last bits of a recomposed transform.
                if (x == y)
                    return true;

                float difference = MathF.Abs(x - y);

                return difference <= Epsilon * MathF.Max(1f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));
            }
        }
    }
}
