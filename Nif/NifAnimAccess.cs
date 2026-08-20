using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Reads transform animation out of a model.
    /// </summary>
    /// <remarks>
    /// A NIF keeps its animations in <c>NiControllerSequence</c> blocks, each a list
    /// of <c>ControlledBlock</c>s pairing a target node with an interpolator. The
    /// interpolator's <c>NiTransformData</c> holds the keys, grouped by component:
    /// translations as Vector3 keys, scales as floats, and rotations either as
    /// quaternions or — when the rotation type says XYZ — as three separate float
    /// groups.
    ///
    /// The same sequences also drive named scalars and colours — a shader's alpha,
    /// an emitter's birth rate, whether something is visible — through float, boolean
    /// and point interpolators. Those become properties on the node's track rather
    /// than moving it, and what they drive is recorded by the four identifying
    /// strings beside them.
    /// </remarks>
    public static class NifAnimAccess
    {
        /// <summary>
        /// The name given to the sequence holding controllers that belong to no
        /// sequence of their own.
        /// </summary>
        public const string DefaultSequenceName = "Take 001";

        /// <summary>Every animation in the model, in block order.</summary>
        public static List<AnimSequence> ReadAnimations(this NifModel model)
        {
            var sequences = new List<AnimSequence>();

            foreach (NifItem block in model.Blocks)
            {
                if (!model.BlockInherits(block, "NiSequence"))
                    continue;

                if (model.ReadSequence(block) is { } sequence)
                    sequences.Add(sequence);
            }

            ReadStandaloneControllers(model, sequences);
            return sequences;
        }

        /// <summary>
        /// Picks up property controllers attached straight to a node.
        /// </summary>
        /// <remarks>
        /// A controller does not have to belong to a sequence: one hung off a node's
        /// controller chain plays for as long as the model is loaded. FBX has no
        /// equivalent of that, so they are gathered into one sequence named
        /// <see cref="DefaultSequenceName"/> — which is what FBXWrangler calls the
        /// stack it invents for the same reason (spec §4.7.3).
        ///
        /// Controllers a sequence already drives are left alone. In a file like
        /// Bethesda's animated effects the same controller block is both attached to
        /// its target and named by every sequence, and reading it twice would play it
        /// twice.
        /// </remarks>
        private static void ReadStandaloneControllers(NifModel model, List<AnimSequence> sequences)
        {
            HashSet<NifItem> claimed = SequencedControllers(model);

            var tracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);

            foreach (NifItem block in model.Blocks)
            {
                if (!model.BlockInherits(block, "NiAVObject"))
                    continue;

                // The name the export gives the node, which for a block with none of
                // its own is its class. Skipping those lost every camera's frustum
                // controller, since a NiCamera in the game's files is unnamed.
                string name = TrackName(model, block);

                if (name.Length == 0)
                    continue;

                // A node's own chain, and the chains of the properties hanging off
                // it: a shader's alpha or a texture's flipbook is controlled from the
                // property, not from the node, but it is the node an FBX track can
                // bind to.
                foreach (NifItem owner in ControllerHosts(model, block))
                {
                    for (NifItem? controller = model.GetRef(owner, "Controller");
                         controller is not null;
                         controller = model.GetRef(controller, "Next Controller"))
                    {
                        if (claimed.Contains(controller))
                            continue;

                        // A transform controller moves the node rather than naming
                        // something on it, so its keys are the track's own curves. It
                        // is read here and not in ReadStandaloneController, which
                        // judges a controller by what its interpolator drives and has
                        // nothing to return for one that drives the node itself.
                        if (model.GetRef(controller, "Interpolator") is { } interpolator
                            && model.BlockInherits(interpolator, "NiTransformInterpolator"))
                        {
                            ReadTransform(model, interpolator, TrackFor(tracks, name));
                            continue;
                        }

                        foreach (AnimProperty property in ReadStandaloneController(model, controller))
                            TrackFor(tracks, name).Properties.Add(property);
                    }
                }
            }

            var keyed = tracks.Values.Where(t => t.Says).ToList();

            if (keyed.Count == 0)
                return;

            var sequence = new AnimSequence { Name = DefaultSequenceName };
            sequence.Tracks.AddRange(keyed);
            (sequence.Start, sequence.Stop) = sequence.KeySpan();

            sequences.Add(sequence);
        }

        /// <summary>
        /// Whether this layer can carry what an interpolator drives.
        /// </summary>
        /// <remarks>
        /// Four kinds, and the list is the whole of what a track can be: a transform,
        /// a float, a boolean, a point. Anything else — a <c>NiPathInterpolator</c>
        /// walking a curve, a <c>NiLookAtInterpolator</c> aiming a node at another one
        /// — is not something a curve on an FBX property can express, so this layer
        /// declines it and the structural carrier takes the whole controller instead.
        ///
        /// Declining is not the same as dropping. Before this was asked, such a
        /// controller fell between the two routes: the animation layer would not carry
        /// it because it could not read the interpolator, and the structural carrier
        /// would not carry it because it *had* one.
        /// </remarks>
        public static bool ReadsInterpolator(NifModel model, NifItem interpolator) =>
            model.BlockInherits(interpolator, "NiTransformInterpolator")
            || model.BlockInherits(interpolator, "NiFloatInterpolator")
            || model.BlockInherits(interpolator, "NiBoolInterpolator")
            || model.BlockInherits(interpolator, "NiPoint3Interpolator");

        /// <summary>
        /// The controllers a sequence names, which the sequence rebuilds.
        /// </summary>
        /// <remarks>
        /// A controller named by a <c>NiControlledBlock</c> is one half of a pair: the
        /// sequence holds the keys and the controller holds the blend slot they mix
        /// into. Anything else that carried it would rebuild it a second time, so both
        /// the animation route and the structural carrier ask this first.
        /// </remarks>
        public static HashSet<NifItem> SequencedControllers(NifModel model)
        {
            var claimed = new HashSet<NifItem>();

            foreach (NifItem block in model.Blocks.Where(b => model.BlockInherits(b, "NiSequence")))
            {
                if (model.FindItem(block, "Controlled Blocks") is not { } controlled)
                    continue;

                foreach (NifItem entry in controlled.Children)
                {
                    if (model.GetRef(entry, "Controller") is { } c)
                        claimed.Add(c);
                }
            }

            return claimed;
        }

        /// <summary>
        /// The name an animation track binds a block by.
        /// </summary>
        /// <remarks>
        /// A track names a node, and the node it names is the FBX object — so this has
        /// to be what the export calls it, which for a block with no name of its own
        /// is its class. The name itself travels separately (`nif_name`), so an
        /// unnamed node still comes back unnamed.
        /// </remarks>
        public static string TrackName(NifModel model, NifItem block) =>
            UniqueNames(model).GetValueOrDefault(block, model.GetName(block));

        /// <summary>
        /// A name per block that no other block shares.
        /// </summary>
        /// <remarks>
        /// A track binds to a node by name, and a NIF is free to give two nodes the
        /// same one. `impactfrosticestorm` has five nodes called `AddOnNode66`, each
        /// with a transform controller of its own, and keying by the shared name kept
        /// the first and dropped four.
        ///
        /// So a repeat gets a numbered suffix, and the block's real name travels
        /// separately as `nif_name` — the carrier a nameless block already needed
        /// (§5.2.5). The order is the block order, which is a property of the file, so
        /// the export and the animation reader work it out separately and agree.
        ///
        /// Cached per model: every node asks, and the answer only changes when the
        /// block list does.
        /// </remarks>
        public static Dictionary<NifItem, string> UniqueNames(NifModel model)
        {
            if (_uniqueNames.TryGetValue(model, out var cached)
                && cached.Count == model.Blocks.Count)
            {
                return cached;
            }

            var names = new Dictionary<NifItem, string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (NifItem block in model.Blocks)
            {
                string name = model.GetName(block);

                // A block with no name of its own is exported under its class, which
                // is shared by construction, so it is numbered on the same rule.
                if (name.Length == 0)
                    name = block.Name;

                int count = seen.GetValueOrDefault(name);
                seen[name] = count + 1;

                names[block] = count == 0 ? name : $"{name}#{count}";
            }

            _uniqueNames.Remove(model);
            _uniqueNames.Add(model, names);

            return names;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            NifModel, Dictionary<NifItem, string>> _uniqueNames = [];

        /// <summary>Everything on a node that can carry a controller chain.</summary>
        public static IEnumerable<NifItem> ControllerHosts(NifModel model, NifItem block)
        {
            yield return block;

            foreach (string field in new[] { "Shader Property", "Alpha Property" })
            {
                if (model.GetRef(block, field) is { } property)
                    yield return property;
            }

            // Older files list their properties instead of naming them.
            foreach (NifItem property in model.GetRefArray(block, "Properties"))
                yield return property;
        }

        /// <summary>
        /// The named values one attached controller drives, which may be none.
        /// </summary>
        /// <remarks>
        /// Judged by its interpolator, as a controlled block is (see
        /// <see cref="ReadControlledBlock"/>): anything driving a float, a boolean or
        /// a point is a named scalar or colour, whatever the controller class is
        /// called. Transform controllers are left alone, since they move the node
        /// rather than name something on it.
        ///
        /// A controller can drive **two** values. `NiPSysEmitterCtlr` holds a second
        /// interpolator in `Visibility Interpolator`, and reading only the first lost
        /// every emitter's on/off track — and, because the track then had no keys, the
        /// controller with it.
        /// </remarks>
        private static IEnumerable<AnimProperty> ReadStandaloneController(
            NifModel model, NifItem controller)
        {
            string id = ControllerIdOf(model, controller);

            foreach ((string field, string interpolatorId) in InterpolatorSlots(model, controller))
            {
                if (model.GetRef(controller, field) is not { } interpolator)
                    continue;

                bool colour = model.BlockInherits(interpolator, "NiPoint3Interpolator");
                bool boolean = model.BlockInherits(interpolator, "NiBoolInterpolator");

                if (!colour && !boolean && !model.BlockInherits(interpolator, "NiFloatInterpolator"))
                    continue;

                var property = new AnimProperty(colour ? 3 : 1)
                {
                    Name = AnimProperty.ToPropertyName(controller.Name, id, interpolatorId, string.Empty),
                    IsBoolean = boolean,
                    ControllerType = controller.Name,
                    InterpolatorType = interpolator.Name,
                    ControllerId = id,
                    InterpolatorId = interpolatorId
                };

                if (ReadValueKeys(model, interpolator, property))
                    yield return property;
            }
        }

        /// <summary>
        /// Which of several same-typed controllers on a target this one is.
        /// </summary>
        /// <remarks>
        /// nif.xml states the rule per class, as the string
        /// <c>NiInterpController::GetCtlrID()</c> returns: for a
        /// <c>NiPSysModifierCtlr</c> it is the <c>Modifier Name</c>, for a
        /// <c>NiFloatExtraDataController</c> the <c>Extra Data Name</c>.
        ///
        /// It is not decoration. A particle system carries several modifier
        /// controllers of the same class, and with no id to tell them apart the import
        /// keys them all to one slot and rebuilds one controller where there were
        /// four — which is what halved the bool interpolators of every effect mesh
        /// that has more than one emitter.
        /// </remarks>
        private static string ControllerIdOf(NifModel model, NifItem controller)
        {
            if (model.BlockInherits(controller, "NiFloatExtraDataController"))
                return model.GetString(controller, "Extra Data Name");

            return model.BlockInherits(controller, "NiPSysModifierCtlr")
                ? model.GetString(controller, "Modifier Name")
                : string.Empty;
        }

        /// <summary>The interpolator fields a controller holds, and what each drives.</summary>
        /// <remarks>
        /// nif.xml names the pair outright for the one class that has two:
        /// <c>NiPSysEmitterCtlr</c>'s are <c>['BirthRate', 'EmitterActive']</c>, "for
        /// `Interpolator` and `Visibility Interpolator` respectively". Those are the
        /// same spellings a <c>NiControlledBlock</c> uses in its `Interpolator ID`, so
        /// a controller read here and one read through a sequence name the same track.
        /// </remarks>
        private static IEnumerable<(string Field, string InterpolatorId)> InterpolatorSlots(
            NifModel model, NifItem controller)
        {
            if (model.FindItem(controller, "Visibility Interpolator") is null)
            {
                yield return ("Interpolator", string.Empty);
                yield break;
            }

            yield return ("Interpolator", "BirthRate");
            yield return ("Visibility Interpolator", "EmitterActive");
        }

        /// <summary>One sequence, or null when it animates nothing this reads.</summary>
        public static AnimSequence? ReadSequence(this NifModel model, NifItem block)
        {
            var sequence = new AnimSequence
            {
                Name = model.GetString(block, "Name"),
                Start = FloatOf(model, block, "Start Time"),
                Stop = FloatOf(model, block, "Stop Time")
            };

            // One track per node, however many controlled blocks turn out to name it:
            // a node's transform and its properties are separate blocks here and one
            // track there.
            var tracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);

            if (model.FindItem(block, "Controlled Blocks") is { } controlled)
            {
                foreach (NifItem entry in controlled.Children)
                    ReadControlledBlock(model, entry, tracks);
            }

            sequence.Tracks.AddRange(tracks.Values.Where(t => t.Says));

            if (sequence.Tracks.Count == 0)
                return null;

            // Bethesda's files often leave the float sentinels in the declared span,
            // which would import as an animation lasting no time at all.
            if (!(sequence.Stop > sequence.Start) || !float.IsFinite(sequence.Start)
                || !float.IsFinite(sequence.Stop) || MathF.Abs(sequence.Stop) > 1e9f)
            {
                (sequence.Start, sequence.Stop) = sequence.KeySpan();
            }

            return sequence;
        }

        /// <summary>
        /// Files one controlled block under the node it targets.
        /// </summary>
        /// <remarks>
        /// The interpolator's type is what says which kind of track this is. A
        /// transform interpolator drives the node itself; a float or boolean one
        /// drives some named scalar on it, and the four identifying strings beside it
        /// are the only record of which.
        /// </remarks>
        private static void ReadControlledBlock(
            NifModel model, NifItem controlled, Dictionary<string, AnimTrack> tracks)
        {
            NifItem? interpolator = model.GetRef(controlled, "Interpolator");

            if (interpolator is null)
                return;

            string name = ReadTargetName(model, controlled);

            if (name.Length == 0)
                return;

            if (model.BlockInherits(interpolator, "NiTransformInterpolator"))
            {
                ReadTransform(model, interpolator, TrackFor(tracks, name));
                return;
            }

            bool boolean = model.BlockInherits(interpolator, "NiBoolInterpolator");
            bool colour = model.BlockInherits(interpolator, "NiPoint3Interpolator");

            if (!boolean && !colour && !model.BlockInherits(interpolator, "NiFloatInterpolator"))
                return;

            var property = new AnimProperty(colour ? 3 : 1)
            {
                Name = AnimProperty.ToPropertyName(
                    model.GetString(controlled, "Controller Type"),
                    model.GetString(controlled, "Controller ID"),
                    model.GetString(controlled, "Interpolator ID"),
                    model.GetString(controlled, "Property Type")),
                IsBoolean = boolean,
                InterpolatorType = interpolator.Name,
                ControllerType = model.GetString(controlled, "Controller Type"),
                ControllerId = model.GetString(controlled, "Controller ID"),
                InterpolatorId = model.GetString(controlled, "Interpolator ID"),
                PropertyType = model.GetString(controlled, "Property Type")
            };

            if (ReadValueKeys(model, interpolator, property))
            {
                TrackFor(tracks, name).Properties.Add(property);
                return;
            }

            // An interpolator that holds neither keys nor a pose still exists, and so
            // does the controlled block naming it. The game's lightning effects are
            // full of them: a "loop" sequence that drives nothing, spelled out rather
            // than left out. Dropping it lost both blocks.
            TrackFor(tracks, name).Properties.Add(new AnimProperty(colour ? 3 : 1)
            {
                Name = property.Name,
                IsBoolean = property.IsBoolean,
                InterpolatorType = property.InterpolatorType,
                ControllerType = property.ControllerType,
                ControllerId = property.ControllerId,
                InterpolatorId = property.InterpolatorId,
                PropertyType = property.PropertyType,
                Empty = true
            });
        }

        /// <summary>
        /// Reads a transform interpolator, keyed or posed.
        /// </summary>
        /// <remarks>
        /// One with no data block is not empty: its own <c>Transform</c> is the pose
        /// the node holds for the whole sequence. Reading only the data block lost
        /// every such controller — the track came out with no keys and was discarded,
        /// and nothing else in the file carries a transform controller.
        /// </remarks>
        private static void ReadTransform(NifModel model, NifItem interpolator, AnimTrack track)
        {
            if (model.GetRef(interpolator, "Data") is { } data)
            {
                ReadTransformTrack(model, data, track);
                return;
            }

            var pose = new AnimPose(
                model.FindItem(interpolator, @"Transform\Translation")?.Value.Get<NifVector3>()
                    ?? new NifVector3(),
                model.FindItem(interpolator, @"Transform\Rotation")?.Value.Get<NifQuat>()
                    ?? new NifQuat(),
                model.FindItem(interpolator, @"Transform\Scale")?.Value.ToFloat() ?? 1f);

            if (!pose.IsEmpty)
                track.Pose = pose;
        }

        private static AnimTrack TrackFor(Dictionary<string, AnimTrack> tracks, string name)
        {
            if (!tracks.TryGetValue(name, out AnimTrack? track))
                tracks[name] = track = new AnimTrack { NodeName = name };

            return track;
        }

        private static void ReadTransformTrack(NifModel model, NifItem data, AnimTrack track)
        {
            ReadTranslations(model, data, track);
            ReadRotations(model, data, track);
            ReadScales(model, data, track);
        }

        /// <summary>
        /// Reads a float, boolean or point interpolator's keys.
        /// </summary>
        /// <remarks>
        /// All three store their keys the same way — a single key group two blocks
        /// down — and differ only in what a key holds. Boolean values arrive as bytes
        /// and are read as the zero and one they stand for, which is all an FBX curve
        /// can carry anyway; a point's three components become three curves, since
        /// FBX keys each one separately.
        /// </remarks>
        private static bool ReadValueKeys(NifModel model, NifItem interpolator, AnimProperty property)
        {
            if (model.GetRef(interpolator, "Data") is not { } block
                || model.FindItem(block, "Data") is not { } group)
            {
                // No data block. The interpolator's own Value is what it holds for the
                // whole sequence, which is an animation and not a resting value: the
                // next sequence can say something else.
                if (model.FindItem(interpolator, "Value") is not { } constant
                    || IsNoValue(property, constant))
                {
                    return false;
                }

                property.Constant = constant.Value.ToFloat();

                return true;
            }

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                float time = FloatOf(model, key, "Time");
                NifItem? value = model.FindItem(key, "Value");

                if (property.IsColor)
                {
                    NifVector3 point = value?.Value.Get<NifVector3>() ?? new NifVector3();

                    property.Curves[0].Keys.Add(new AnimKey(time, point.X, interpolation));
                    property.Curves[1].Keys.Add(new AnimKey(time, point.Y, interpolation));
                    property.Curves[2].Keys.Add(new AnimKey(time, point.Z, interpolation));
                }
                else
                {
                    property.Curve.Keys.Add(new AnimKey(time, value?.Value.ToFloat() ?? 0f, interpolation));
                }
            }

            return property.Curves.Any(c => c.HasKeys);
        }

        /// <summary>
        /// Whether a pose value is the sentinel that says there is no pose value.
        /// </summary>
        /// <remarks>
        /// nif.xml calls the field "Pose value if lacking NiFloatData" and gives it a
        /// default that means "none": <c>#INV_FLT#</c> for a float interpolator,
        /// <c>2</c> for a boolean one — a bool being 0 or 1, never 2.
        ///
        /// An interpolator with neither data nor a pose value holds nothing, and
        /// reading the sentinel as a constant turns it into an animation that sets
        /// every float it drives to 3.4e38.
        /// </remarks>
        private static bool IsNoValue(AnimProperty property, NifItem constant)
        {
            if (property.IsBoolean)
                return constant.Value.ToUInt() > 1;

            float value = constant.Value.ToFloat();

            return !float.IsFinite(value) || MathF.Abs(value) > 1e30f;
        }

        /// <summary>
        /// The name of the node a controlled block targets.
        /// </summary>
        /// <remarks>
        /// Three spellings, by version. Modern files store the name outright; files
        /// between 10.2 and 20.1 store an offset into a shared
        /// <c>NiStringPalette</c>, which is how a .kf keeps its target names in one
        /// place; older ones name the target directly in the block.
        /// </remarks>
        private static string ReadTargetName(NifModel model, NifItem controlled)
        {
            if (model.FindItem(controlled, "Node Name") is not null)
            {
                string direct = model.GetString(controlled, "Node Name");

                if (direct.Length > 0)
                    return direct;
            }

            if (model.GetRef(controlled, "String Palette") is { } palette
                && model.FindItem(controlled, "Node Name Offset") is { } offset)
            {
                return ReadFromPalette(model, palette, offset.Value.ToUInt());
            }

            return model.FindItem(controlled, "Target Name") is not null
                ? model.GetString(controlled, "Target Name")
                : string.Empty;
        }

        /// <summary>The NUL-terminated string at an offset into a string palette.</summary>
        private static string ReadFromPalette(NifModel model, NifItem palette, uint offset)
        {
            // The unset offset is all ones, not zero -- zero is a real string.
            if (offset == uint.MaxValue)
                return string.Empty;

            string all = model.GetString(palette, @"Palette\Palette");

            if (offset >= all.Length)
                return string.Empty;

            int end = all.IndexOf('\0', (int)offset);
            return end < 0 ? all[(int)offset..] : all[(int)offset..end];
        }

        private static void ReadTranslations(NifModel model, NifItem data, AnimTrack track)
        {
            if (model.FindItem(data, "Translations") is not { } group)
                return;

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                float time = FloatOf(model, key, "Time");
                NifVector3 value = model.FindItem(key, "Value")?.Value.Get<NifVector3>() ?? new NifVector3();

                track.Translation[0].Keys.Add(new AnimKey(time, value.X, interpolation));
                track.Translation[1].Keys.Add(new AnimKey(time, value.Y, interpolation));
                track.Translation[2].Keys.Add(new AnimKey(time, value.Z, interpolation));
            }
        }

        /// <summary>
        /// Rotation keys, which come one of two ways.
        /// </summary>
        /// <remarks>
        /// Rotation type 4 means the file already stores X, Y and Z as separate
        /// float groups — in radians — and the quaternion array is then empty
        /// regardless of what the key count says. Any other type means quaternion
        /// keys, which have to be decomposed into the same Euler XYZ degrees a
        /// node's static rotation uses.
        /// </remarks>
        private static void ReadRotations(NifModel model, NifItem data, AnimTrack track)
        {
            const uint XyzRotation = 4;
            const float ToDegrees = 180f / MathF.PI;

            if (model.GetUInt(data, "Rotation Type") == XyzRotation)
            {
                if (model.FindItem(data, "XYZ Rotations") is not { } groups)
                    return;

                for (int axis = 0; axis < 3 && axis < groups.Children.Count; axis++)
                {
                    NifItem group = groups.Children[axis];
                    AnimInterpolation interpolation = InterpolationOf(model, group);

                    foreach (NifItem key in KeysOf(model, group))
                    {
                        track.Rotation[axis].Keys.Add(new AnimKey(
                            FloatOf(model, key, "Time"),
                            FloatOf(model, key, "Value") * ToDegrees,
                            interpolation));
                    }
                }

                return;
            }

            if (model.FindItem(data, "Quaternion Keys") is not { } keys)
                return;

            foreach (NifItem key in keys.Children)
            {
                float time = FloatOf(model, key, "Time");
                NifQuat value = model.FindItem(key, "Value")?.Value.Get<NifQuat>() ?? NifQuat.Identity;

                NifVector3 euler = new NifTransform(
                    new NifVector3(), NifTransform.RotationFromQuaternion(value), 1f).ToEulerDegrees();

                // A quaternion carries no tangents, so the smooth reading is the
                // only one that reproduces the slerp it stood for.
                track.Rotation[0].Keys.Add(new AnimKey(time, euler.X, AnimInterpolation.Cubic));
                track.Rotation[1].Keys.Add(new AnimKey(time, euler.Y, AnimInterpolation.Cubic));
                track.Rotation[2].Keys.Add(new AnimKey(time, euler.Z, AnimInterpolation.Cubic));
            }
        }

        private static void ReadScales(NifModel model, NifItem data, AnimTrack track)
        {
            if (model.FindItem(data, "Scales") is not { } group)
                return;

            AnimInterpolation interpolation = InterpolationOf(model, group);

            foreach (NifItem key in KeysOf(model, group))
            {
                float time = FloatOf(model, key, "Time");
                float value = FloatOf(model, key, "Value");

                // NIF scales uniformly; FBX has three axes and wants all of them.
                for (int axis = 0; axis < 3; axis++)
                    track.Scale[axis].Keys.Add(new AnimKey(time, value, interpolation));
            }
        }

        private static IEnumerable<NifItem> KeysOf(NifModel model, NifItem group) =>
            model.FindItem(group, "Keys")?.Children ?? Enumerable.Empty<NifItem>();

        private static AnimInterpolation InterpolationOf(NifModel model, NifItem group) =>
            FromKeyType(model.GetUInt(group, "Interpolation"));

        /// <summary>Maps a NIF key type onto the interpolation FBX understands.</summary>
        private static AnimInterpolation FromKeyType(uint keyType) => keyType switch
        {
            1 => AnimInterpolation.Linear,
            2 => AnimInterpolation.Cubic,

            // Tension/bias/continuity keys are curves too; FBX just describes their
            // handles differently, so the curve survives and the handles do not.
            3 => AnimInterpolation.Cubic,
            5 => AnimInterpolation.Constant,
            _ => AnimInterpolation.Linear
        };

        private static float FloatOf(NifModel model, NifItem parent, string field) =>
            model.FindItem(parent, field)?.Value.ToFloat() ?? 0f;
    }
}
