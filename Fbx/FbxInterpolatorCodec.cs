using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Moves a whole interpolator through FBX, as flat name/value text.
    /// </summary>
    /// <remarks>
    /// The animation layer models four kinds of interpolator — transform, float,
    /// boolean, point — because those are the four a curve on an FBX property can
    /// express (§5A.7). A file may hold others. A <c>NiPathInterpolator</c> walks a node
    /// along a spline; a <c>NiLookAtInterpolator</c> aims one node at another. Neither
    /// is a curve, and neither can be rebuilt from one.
    ///
    /// So they are not converted at all: the block travels as its own fields, two levels
    /// deep — the interpolator, then whatever data block it points at, which is where
    /// its keys are. Nothing is interpreted, so nothing needs to be understood, and a
    /// class this port has never heard of travels as well as one it has.
    ///
    /// This is the same shape §5.2.2 uses for node → bound → volume, and the same codec.
    /// Both routes that can carry an interpolator use it: the structural carrier for one
    /// hanging on a controller, and the animation carrier for one named by a sequence.
    /// </remarks>
    public static class FbxInterpolatorCodec
    {
        /// <summary>The key a carried block's class is stored under.</summary>
        public const string TypeSuffix = "type";

        /// <summary>The key a carried block's identity is stored under.</summary>
        /// <remarks>
        /// Which block it was in the source, so blocks that shared one still do.
        /// `fxambwaterfallsalmon02` points six path interpolators at two `NiPosData`
        /// blocks — the same two fish paths, played by three sequences — and rebuilding
        /// one per interpolator turned two blocks into six.
        ///
        /// Identity rather than content, as a texture set, an alpha property and a skin
        /// data already are (§5.2.1).
        /// </remarks>
        public const string IdSuffix = "id";

        /// <summary>Fields rebuilt from context rather than carried.</summary>
        private static bool Rebuilt(NifItem child) => child.Name is "Next Controller" or "Target";

        /// <summary>
        /// Writes an interpolator and its data block under a prefix.
        /// </summary>
        /// <remarks>
        /// References only. A pointer is the upward half of a two-way link — a
        /// <c>NiLookAtInterpolator</c>'s <c>Look At</c> names the node it aims at — and
        /// following one carries a copy of that node, which comes back attached to
        /// nothing.
        /// </remarks>
        public static void Write(
            NifModel model, NifItem? block, string prefix, Action<string, string> sink, int depth = 0)
        {
            if (block is null || depth > 2)
                return;

            sink($"{prefix}{TypeSuffix}", block.Name);
            sink($"{prefix}{IdSuffix}", model.IndexOf(block).ToString(
                System.Globalization.CultureInfo.InvariantCulture));

            NifFieldCodec.Write(
                model, block, prefix, sink, Rebuilt,
                (field, link) =>
                {
                    if (link.Value.Type != NifValueType.UpLink)
                        Write(model, model.GetBlock(link), $"{field}_", sink, depth + 1);
                });
        }

        /// <summary>
        /// Rebuilds what <see cref="Write"/> stored, or null when nothing was stored.
        /// </summary>
        /// <param name="ancestor">
        /// What the carried class has to be, checked against the schema. A name this
        /// build does not know, or one of the wrong family, is refused rather than
        /// inserted.
        /// </param>
        public static NifItem? Read(
            NifModel model, string prefix, Func<string, string?> source, string ancestor, int depth = 0)
        {
            if (depth > 2
                || source($"{prefix}{TypeSuffix}") is not { Length: > 0 } type
                || !model.KnowsBlock(type)
                || !model.Database.Inherits(type, ancestor))
            {
                return null;
            }

            // A block two shapes shared is rebuilt once. Keyed on which block it was,
            // not on what is in it: the game ships identical ones side by side too.
            var built = Shared.GetOrCreateValue(model);
            string identity = $"{type}#{source($"{prefix}{IdSuffix}") ?? string.Empty}";

            if (source($"{prefix}{IdSuffix}") is { Length: > 0 }
                && built.TryGetValue(identity, out NifItem? already))
            {
                return already;
            }

            NifItem block = model.InsertBlock(type);

            if (source($"{prefix}{IdSuffix}") is { Length: > 0 })
                built[identity] = block;

            NifFieldCodec.Read(
                model, block, prefix, source, Rebuilt,
                (field, link) =>
                {
                    if (link.Value.Type == NifValueType.UpLink)
                        return;

                    if (Read(model, $"{field}_", source, "NiObject", depth + 1) is { } data)
                        link.Value.SetLink(model.IndexOf(data));
                });

            return block;
        }

        /// <summary>
        /// Blocks rebuilt so far, per model being written.
        /// </summary>
        /// <remarks>
        /// Held here rather than threaded through every caller: this codec is reached
        /// from two routes and several depths, and the answer is a property of the
        /// model being built rather than of any one call.
        /// </remarks>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            NifModel, Dictionary<string, NifItem>> Shared = [];

        /// <summary>Captures an interpolator as a flat dictionary.</summary>
        public static Dictionary<string, string> Capture(NifModel model, NifItem interpolator)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            Write(model, interpolator, string.Empty, (name, value) => fields[name] = value);

            return fields;
        }

        /// <summary>Rebuilds an interpolator from what <see cref="Capture"/> produced.</summary>
        public static NifItem? Rebuild(NifModel model, IReadOnlyDictionary<string, string> fields) =>
            Read(model, string.Empty, name => fields.GetValueOrDefault(name), "NiInterpolator");
    }
}
