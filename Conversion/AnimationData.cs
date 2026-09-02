using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>How a key blends into the next one.</summary>
    /// <remarks>
    /// The three both formats agree on. NIF's TBC keys carry tension, bias and
    /// continuity that FBX expresses differently, so they arrive here as
    /// <see cref="Cubic"/> — the shape of the curve is preserved, the authoring
    /// handles are not.
    /// </remarks>
    public enum AnimInterpolation
    {
        /// <summary>Hold the value until the next key.</summary>
        Constant,

        /// <summary>Straight line to the next key.</summary>
        Linear,

        /// <summary>Smooth, with tangents.</summary>
        Cubic
    }

    /// <summary>One key on one curve. Time is in seconds.</summary>
    public readonly record struct AnimKey(float Time, float Value, AnimInterpolation Interpolation)
    {
        /// <summary>
        /// Tension, bias and continuity, for a key written in that form.
        /// </summary>
        /// <remarks>
        /// A NIF key type of 3 shapes its spline with three numbers instead of with
        /// tangents, and FBX has no equivalent -- so the curve travelled and the three
        /// numbers did not, and every rebuilt TBC key came back with zeroes. Vanilla
        /// means them: `dlc01sebf_blastroof` scales with tensions of 0.388, 1 and 0.468,
        /// and a tension of zero is a different curve through the same points.
        ///
        /// Carried beside the curve rather than derived, because nothing about the
        /// sampled values says what the handles were. Zero for every other key type,
        /// which is what the field holds in a file that does not use them.
        /// </remarks>
        public NifVector3 Tbc { get; init; }
    }

    /// <summary>A single animated scalar over time.</summary>
    public sealed class AnimCurve
    {
        public List<AnimKey> Keys { get; } = [];

        public bool HasKeys => Keys.Count > 0;

        /// <summary>The first and last key times, or zero when empty.</summary>
        public (float Start, float Stop) Span =>
            Keys.Count == 0 ? (0f, 0f) : (Keys[0].Time, Keys[^1].Time);
    }

    /// <summary>
    /// A single named scalar animated on a node: an emitter's birth rate, a
    /// shader's alpha, whether something is visible.
    /// </summary>
    /// <remarks>
    /// FBX has one way to express all of these — a custom property on the model,
    /// driven by a curve — while a NIF needs four strings to say which controller
    /// on which sub-object the track belongs to. Those strings are carried through
    /// the FBX property's *name*, encoded by <see cref="ToPropertyName"/>, because
    /// a property that cannot be traced back is a property that can only be
    /// exported.
    /// </remarks>
    public sealed class AnimProperty(int components = 1)
    {
        /// <summary>The separator between the parts of an encoded name.</summary>
        /// <remarks>
        /// Chosen because NIF names are identifiers and paths; none of them contain
        /// a pipe, so nothing legitimate is ambiguous.
        /// </remarks>
        public const char Separator = '|';

        /// <summary>The FBX property name, encoding the identity below.</summary>
        public required string Name { get; init; }

        /// <summary>
        /// The flags of the controller this property belongs to, when it had one.
        /// </summary>
        /// <remarks>
        /// Null for a property that did not come from a file, where the writer's own
        /// constant is the only answer available.
        ///
        /// A controller's flags say whether it is active and which way it plays, and
        /// they were written from a constant -- 12 for a property controller, 44 for a
        /// transform one -- whatever the source held. The game's files hold 72 and 108
        /// as readily, so a shader controller came back active-and-looping when it was
        /// neither.
        /// </remarks>
        public uint? ControllerFlags { get; set; }

        /// <summary>
        /// The controller's phase, when the file gave it one.
        /// </summary>
        /// <remarks>
        /// The offset that stops every emitter in a scene pulsing together. Zero in
        /// 28,084 of the game's controllers and something else in 1,367, and those
        /// cluster in exactly the two blocks it matters for: NiPSysEmitterCtlr and
        /// BSPSysMultiTargetEmitterCtlr, holding values like 0.125, 19.33 and 56.36.
        ///
        /// Written as a flat zero before this, which is right for all but those 1,367
        /// and turns a staggered set of emitters into a synchronised one.
        /// </remarks>
        public float? ControllerPhase { get; set; }

        /// <summary>
        /// One curve per component: one for a scalar, three for a colour.
        /// </summary>
        /// <remarks>
        /// A colour is three curves for the same reason a translation is: FBX keys
        /// each component separately, while a NIF keys the whole vector at once.
        /// </remarks>
        public AnimCurve[] Curves { get; } =
            Enumerable.Range(0, components).Select(_ => new AnimCurve()).ToArray();

        /// <summary>The only curve, for the scalar tracks that have just one.</summary>
        public AnimCurve Curve => Curves[0];

        /// <summary>Whether this is a colour rather than a single number.</summary>
        public bool IsColor => Curves.Length == 3;

        /// <summary>Whether the source was a boolean track rather than a float one.</summary>
        public bool IsBoolean { get; init; }

        /// <summary>
        /// Whether the source held an interpolator here that itself held nothing.
        /// </summary>
        /// <remarks>
        /// A file can store an interpolator with no data block and no pose value —
        /// nif.xml's sentinels, `#INV_FLT#` for a float and 2 for a bool, both meaning
        /// "none". It says nothing, and it is still a block: the controlled block that
        /// names it and the interpolator itself are in the file and have to come back.
        ///
        /// Distinct from a constant, which says one thing, and from an absent property,
        /// which is not there at all.
        /// </remarks>
        public bool Empty { get; init; }

        /// <summary>
        /// The whole interpolator, when this layer cannot model what it drives.
        /// </summary>
        /// <remarks>
        /// Four kinds of interpolator become curves, because those are the four a curve
        /// can express. A <c>NiPathInterpolator</c> walks a node along a spline and a
        /// <c>NiLookAtInterpolator</c> aims it at another node; neither is a curve, and
        /// converting them would mean inventing one.
        ///
        /// So the block is not converted at all — it is carried, as flat fields, and
        /// put back exactly. See <see cref="Fbx.FbxInterpolatorCodec"/>.
        /// </remarks>
        public IReadOnlyDictionary<string, string>? CarriedInterpolator { get; init; }

        /// <summary>
        /// Which data block this track's keys came from, when it came from a NIF.
        /// </summary>
        /// <remarks>
        /// Two interpolators can share one <c>NiFloatData</c>, and the game's files do
        /// it: `dlceclipsesky` has two such pairs among twenty-five. Rebuilding each
        /// interpolator's keys on its own turns one block into two.
        ///
        /// Identity rather than content, as a texture set, an alpha property, a skin
        /// data and a carried interpolator's data all are (§5.2.1). -1 when the scene
        /// did not say, which is what a track authored in a DCC tool has.
        /// </remarks>
        public int DataId { get; set; } = -1;

        /// <summary>The NIF controller class, e.g. <c>NiPSysEmitterCtlr</c>.</summary>
        public string ControllerType { get; init; } = string.Empty;

        /// <summary>Which of several same-typed controllers on the target this is.</summary>
        public string ControllerId { get; init; } = string.Empty;

        /// <summary>Which value of that controller the track drives, e.g. <c>BirthRate</c>.</summary>
        public string InterpolatorId { get; init; } = string.Empty;

        /// <summary>The property class the controller is attached to, when it is one.</summary>
        public string PropertyType { get; init; } = string.Empty;

        /// <summary>
        /// The value this property holds for the whole sequence, when it has no keys.
        /// </summary>
        /// <remarks>
        /// A NIF interpolator can carry a value and no data block at all, and that is
        /// a real animation: it says "this, for this whole sequence". It is not the
        /// same as the property's resting value, because a different sequence can say
        /// something different, and it is not a curve either — a curve with one key is
        /// a curve, and this is the absence of one.
        /// </remarks>
        public float? Constant { get; set; }

        /// <summary>
        /// The interpolator class this track came from.
        /// </summary>
        /// <remarks>
        /// Not always the obvious one. A <c>NiBoolTimelineInterpolator</c> is a
        /// <c>NiBoolInterpolator</c> that, in nif.xml's words, "ensures that keys have
        /// not been missed between two updates" — so rebuilding it as its base turns a
        /// track that cannot skip an event into one that can, which shows up as an
        /// animation occasionally not firing rather than as anything visibly wrong.
        /// </remarks>
        public string InterpolatorType { get; set; } = string.Empty;

        /// <summary>The FBX name for a visibility track.</summary>
        /// <remarks>
        /// Kept as the plain FBX property rather than an encoded one, because
        /// <c>Visibility</c> is standard: a DCC tool given this actually hides the
        /// object, where an encoded name would only be a number nobody reads.
        /// </remarks>
        public const string VisibilityName = "Visibility";

        /// <summary>The NIF controller that <see cref="VisibilityName"/> stands for.</summary>
        public const string VisibilityController = "NiVisController";

        /// <summary>Builds the FBX property name that carries this track's identity.</summary>
        public string ToPropertyName() => ToPropertyName(
            ControllerType, ControllerId, InterpolatorId, PropertyType);

        /// <inheritdoc cref="ToPropertyName()"/>
        public static string ToPropertyName(
            string controllerType, string controllerId, string interpolatorId, string propertyType)
        {
            if (controllerType == VisibilityController
                && controllerId.Length == 0 && interpolatorId.Length == 0)
            {
                return VisibilityName;
            }

            var parts = new List<string> { controllerType, controllerId, interpolatorId, propertyType };

            // Trailing empties carry nothing, and dropping them keeps the common
            // case -- a controller with no ids at all -- readable.
            while (parts.Count > 1 && parts[^1].Length == 0)
                parts.RemoveAt(parts.Count - 1);

            return string.Join(Separator, parts);
        }

        /// <summary>Recovers the identity from an FBX property name.</summary>
        public static (string ControllerType, string ControllerId, string InterpolatorId, string PropertyType)
            FromPropertyName(string name)
        {
            if (name == VisibilityName)
                return (VisibilityController, string.Empty, string.Empty, string.Empty);

            string[] parts = name.Split(Separator);

            string At(int i) => i < parts.Length ? parts[i] : string.Empty;

            return (At(0), At(1), At(2), At(3));
        }
    }

    /// <summary>
    /// One node's animation, as the nine curves FBX addresses separately.
    /// </summary>
    /// <remarks>
    /// NIF groups a transform's keys by component — a translation key is one
    /// Vector3, a rotation key one quaternion — while FBX animates X, Y and Z as
    /// three independent curves. Splitting on the way in means the two directions
    /// only have to agree about scalars.
    ///
    /// Rotation is in **degrees**, Euler XYZ, matching what a node's static
    /// <c>Lcl Rotation</c> carries. Anything else and an animated node would jump
    /// the moment its first key took effect.
    /// </remarks>
    public sealed class AnimTrack
    {
        public required string NodeName { get; init; }

        public AnimCurve[] Translation { get; } = [new(), new(), new()];

        public AnimCurve[] Rotation { get; } = [new(), new(), new()];

        public AnimCurve[] Scale { get; } = [new(), new(), new()];

        /// <summary>Named scalars animated alongside the transform.</summary>
        public List<AnimProperty> Properties { get; } = [];

        /// <summary>
        /// Which form the source stored this track's rotation in.
        /// </summary>
        /// <remarks>
        /// A NIF keeps rotation either as quaternion keys -- `LINEAR_KEY`, `TBC_KEY` --
        /// or as three `XYZ Rotations` groups, one per Euler axis. FBX has only the
        /// second, so a quaternion track is decomposed on the way out and, without
        /// this, comes back as the XYZ form: 764 of the 3,000 rotation blocks in a
        /// 3,000-mesh sample change their storage that way.
        ///
        /// The XYZ form is the one that survives everything, and is what a track gets
        /// when nothing said otherwise: 716 vanilla blocks keep their three axes on
        /// *different* timelines, which quaternion keys cannot express at all. So this
        /// only ever restores a form the source actually used, and only when the axes
        /// still agree about their key times.
        ///
        /// Null for a track that came from an FBX with nothing to say about it.
        /// </remarks>
        public uint? RotationType { get; set; }

        /// <summary>
        /// The flags of the transform controller that moves this node, when it has one
        /// of its own rather than being run by a manager.
        /// </summary>
        /// <remarks>
        /// A property's controller already carries these; the node's own did not, and
        /// was rebuilt with the standalone constant -- 76, which is `CYCLE_CLAMP`,
        /// `Active` and `Compute Scaled Time`, and so exactly nif.xml's defaults.
        ///
        /// It is the commonest value and it is not the only one. Of the 788 standalone
        /// transform controllers in a 2,500-mesh sample:
        ///
        /// | Flags | Meaning | Count |
        /// | --- | --- | --- |
        /// | 76 | clamp, active — nif.xml's default | 447 |
        /// | 69 | anim type 1, clamp, **inactive** | 210 |
        /// | 72 | **loop**, active | 95 |
        /// | 74 | **reverse**, active | 29 |
        /// | 65 | anim type 1, **loop**, inactive | 7 |
        ///
        /// So 341 of them, 43%, held something the constant is wrong about, over five
        /// distinct values -- which is why this is carried rather than given a better
        /// constant.
        ///
        /// Nor can it be worked out from the content, which was tried before carrying
        /// it. `Active` has no predictor at all: `NiNode` holds 185 inactive against
        /// 537 active, `NiBillboardNode` 25 against 18, a node with one controller 208
        /// against 555. The one clean split is one-way -- every controller in a file
        /// that also has sequences is active, 59 of 59 -- and files without sequences
        /// hold both, 217 against 512, so it decides nothing.
        ///
        /// The cycle type resists the obvious test too. Whether the animation returns
        /// to where it started says little: of the tracks with translation keys, 28
        /// looping ones close and 4 do not, against 63 clamping ones that close and 231
        /// that do not. "Open, therefore clamp" holds 231 times in 235; "closed" is
        /// 63 clamps against 28 loops, so most closed animations do not loop. And all
        /// 29 `CYCLE_REVERSE` controllers have no translation keys for the test to look
        /// at.
        ///
        /// Both halves of the disagreement matter to what the game does. The cycle type
        /// decides whether the animation loops, reverses or stops at its last key:
        /// `benthiclurkerprojectile.nif` holds 72, a looping controller, and came back
        /// clamping. And `Active` decides whether it runs at all -- 217 of the 788 have
        /// it clear, and the constant switched every one of them on.
        ///
        /// Null when the track came from a sequence, where the manager owns the flags.
        /// </remarks>
        public uint? ControllerFlags { get; set; }

        /// <summary>The nine transform curves.</summary>
        public IEnumerable<AnimCurve> Curves => Translation.Concat(Rotation).Concat(Scale);

        /// <summary>Everything keyed on this node, transform and properties alike.</summary>
        public IEnumerable<AnimCurve> AllCurves => Curves.Concat(Properties.SelectMany(p => p.Curves));

        /// <summary>
        /// The fixed transform this track holds, when it holds one rather than keys.
        /// </summary>
        /// <remarks>
        /// A <c>NiTransformInterpolator</c> with no data block still carries a
        /// <c>Transform</c>, and that is the pose the node takes for the whole
        /// sequence. It is the transform equivalent of a property's
        /// <see cref="AnimProperty.Constant"/> and is dropped for the same reason if
        /// nothing looks for it: the track has no keys, so it looks empty.
        ///
        /// The components carry their own "unset" marks, since a file can pose the
        /// translation and leave the rotation to the node's own.
        /// </remarks>
        public AnimPose? Pose { get; set; }

        public bool HasKeys => AllCurves.Any(c => c.HasKeys);

        /// <summary>
        /// Whether this track says anything at all, keys or not.
        /// </summary>
        /// <remarks>
        /// A constant holds one value for the whole sequence rather than none — two
        /// sequences can hold different constants for the same property, which is
        /// exactly what a "loop" sequence that hides a mesh outright does. Filtering
        /// on <see cref="HasKeys"/> alone dropped those tracks, and with them the
        /// controlled blocks and interpolators that carried them.
        /// </remarks>
        public bool Says =>
            HasKeys
            || Pose is not null
            || Properties.Any(p => p.Constant is not null || p.Empty || p.CarriedInterpolator is not null);
    }

    /// <summary>
    /// A transform held for a whole sequence rather than keyed.
    /// </summary>
    /// <remarks>
    /// Carried as the numbers the file holds — a quaternion rather than a matrix —
    /// because this is written straight back into a <c>NiTransformInterpolator</c>'s
    /// <c>Transform</c>, and going through a matrix and back would change the numbers
    /// of a file nobody edited.
    ///
    /// A component may be the "unset" mark rather than a value, meaning the node's own
    /// transform stands for it. That mark is <c>float.MinValue</c>, which is what the
    /// writer already used for a base transform it did not want to override.
    /// </remarks>
    public sealed record AnimPose(NifVector3 Translation, NifQuat Rotation, float Scale)
    {
        /// <summary>The value a component takes when the file poses nothing.</summary>
        public const float Unset = float.MinValue;

        /// <summary>Whether every component is unset, so the pose says nothing at all.</summary>
        public bool IsEmpty =>
            Translation.X == Unset && Translation.Y == Unset && Translation.Z == Unset
            && Rotation.W == Unset && Rotation.X == Unset
            && Rotation.Y == Unset && Rotation.Z == Unset
            && Scale == Unset;
    }

    /// <summary>
    /// One animation: a named span of time and the nodes it moves.
    /// </summary>
    /// <remarks>
    /// A NIF's <c>NiControllerSequence</c> and an FBX's animation stack are the same
    /// idea under different names, so this is what both convert through.
    /// </remarks>
    public sealed class AnimSequence
    {
        public required string Name { get; init; }

        /// <summary>When the sequence begins, in seconds.</summary>
        public float Start { get; set; }

        /// <summary>When it ends, in seconds.</summary>
        public float Stop { get; set; }

        /// <summary>
        /// What the sequence does when it reaches its end.
        /// </summary>
        /// <remarks>
        /// nif.xml's `CycleType`: 0 loops, 1 reverses, 2 clamps. It was not carried and
        /// not read — every rebuilt sequence was written with a constant, and the
        /// constant said `CycleClamp` while holding 0, which is *loop*. So an animation
        /// meant to play once and stop played for ever instead.
        /// </remarks>
        public uint CycleType { get; set; }

        /// <summary>
        /// The node accumulated root motion is measured against.
        /// </summary>
        /// <remarks>
        /// Empty when the sequence did not name one, in which case the import falls back
        /// to the root as it always did. It was being synthesised from whichever block
        /// happened to be first, so a sequence naming `Mesh01` came back naming
        /// `Scene Root`.
        /// </remarks>
        public string AccumRootName { get; set; } = string.Empty;

        /// <summary>
        /// The sequence's text keys, in order: a time and what it marks.
        /// </summary>
        /// <remarks>
        /// Skyrim finds `start` and `end` by name to know where a sequence runs, and
        /// those two were all a rebuilt sequence got. The rest are content: a door's
        /// `Sound: DRSWoodDoubleRough01Open` is what makes it audible, and
        /// `lastFrame` marks where an effect holds. Dropping them leaves a door that
        /// opens in silence.
        ///
        /// Empty for a sequence out of an FBX that carried none, which then gets the
        /// two markers it needs and nothing else.
        /// </remarks>
        public List<(float Time, string Value)> TextKeys { get; } = [];

        public List<AnimTrack> Tracks { get; } = [];

        /// <summary>
        /// The span the keys actually cover, for when the sequence does not say.
        /// </summary>
        /// <remarks>
        /// A sequence whose declared span is empty or inverted — Bethesda's files
        /// leave the float sentinels in place often enough — would otherwise import
        /// as an animation of zero length.
        /// </remarks>
        public (float Start, float Stop) KeySpan()
        {
            float start = float.MaxValue;
            float stop = float.MinValue;

            foreach (AnimCurve curve in Tracks.SelectMany(t => t.AllCurves).Where(c => c.HasKeys))
            {
                (float first, float last) = curve.Span;
                start = MathF.Min(start, first);
                stop = MathF.Max(stop, last);
            }

            return start > stop ? (0f, 0f) : (start, stop);
        }
    }
}
