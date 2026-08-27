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

                for (int i = 0; i < a.Children.Count; i++)
                {
                    NifItem ca = a.Children[i], cb = b.Children[i];
                    string p = $"{path}/{ca.Name}";

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
