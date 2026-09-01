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

            private NifItem _owner = null!;

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
                if (a.Children.Count != b.Children.Count)
                {
                    Differences.Add(new NifDifference(
                        path, a.Name, $"{a.Children.Count} fields", $"{b.Children.Count} fields"));

                    return;
                }

                // A list whose order is bookkeeping rather than content, matched by
                // what its entries are instead of where they sit.
                if (a.Name == "Extra Data List")
                    AlignExtraData(a, b);

                _permuted.TryGetValue(a, out int[]? order);

                for (int i = 0; i < a.Children.Count; i++)
                {
                    NifItem ca = a.Children[i], cb = b.Children[order is null ? i : order[i]];
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
            private void AlignExtraData(NifItem left_, NifItem right_)
            {
                if (left_.Children.Count == 0 || left_.Children.Count != right_.Children.Count)
                    return;

                List<string> wanted = Annotations(left, left_), have = Annotations(right, right_);

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

                if (moved)
                    _permuted[left_] = order;
            }

            /// <summary>Each entry of an extra data list, as class and name.</summary>
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

            private bool Same(NifItem a, NifItem b)
            {
                if (a.Name == "Flags" && left.BlockInherits(_owner, "NiAVObject"))
                    return (a.Value.ToUInt() & ~IgnoredAvFlag) == (b.Value.ToUInt() & ~IgnoredAvFlag);

                // A quaternion and its negation are the same rotation: q and -q turn a
                // body to exactly the same place, and which one a decomposition hands
                // back is an accident of the arithmetic. Reported as a difference it is
                // pure noise, and noise in this comparison is what lets a real
                // difference hide in a long list.
                //
                // Only for a field actually spelled `Rotation`. A negated normal is not
                // the same normal -- it is the surface pointing the other way.
                if (a.Name == "Rotation" && NegatedQuaternion(a.Value.ToString(), b.Value.ToString()))
                    return true;

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
