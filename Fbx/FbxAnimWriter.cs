using SECmd.Nif;
using MeshIO.Formats.Fbx;
using SECmd.Conversion;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes animation into an FBX scene.
    /// </summary>
    /// <remarks>
    /// FBX splits an animation four ways. A <c>AnimationStack</c> is the take; an
    /// <c>AnimationLayer</c> under it holds the tracks; an
    /// <c>AnimationCurveNode</c> binds one vector-valued property of one model —
    /// its translation, rotation or scaling — and an <c>AnimationCurve</c> under
    /// that holds the keys for a single component.
    ///
    /// The binding is by connection, not by containment: the curve node reaches its
    /// model through an object-to-property edge naming <c>Lcl Translation</c> and
    /// the like, and each curve reaches the curve node through another naming
    /// <c>d|X</c>. Miss either and the file loads with the animation present but
    /// attached to nothing.
    /// </remarks>
    public static class FbxAnimWriter
    {
        /// <summary>
        /// FBX time units per second.
        /// </summary>
        /// <remarks>
        /// One <c>KTime</c> unit is 1/46186158000 of a second — a number chosen to
        /// divide exactly by every frame rate in use, so that no frame time is ever
        /// rounded.
        /// </remarks>
        public const long TimeUnitsPerSecond = 46186158000L;

        /// <summary>The version stamped on a curve's key arrays.</summary>
        private const int KeyVersion = 4009;

        /// <summary>The layer name every stack here uses, as FBXWrangler writes it.</summary>
        public const string LayerName = "Default";

        /// <summary>Interpolation and tangent bits, as the FBX SDK defines them.</summary>
        internal static class KeyFlags
        {
            public const int Constant = 0x00000002;
            public const int Linear = 0x00000004;
            public const int Cubic = 0x00000008;

            /// <summary>Let the importer choose tangents; NIF quadratic keys carry
            /// tangents FBX cannot express directly, so this reproduces the shape.</summary>
            public const int TangentAuto = 0x00000100;

            /// <summary>
            /// Tension, continuity and bias, which FBX describes a spline with too.
            /// </summary>
            /// <remarks>
            /// A NIF key type of 3 is a Kochanek-Bartels key, and so is this: FBX keeps
            /// the three numbers in the key's own data slots rather than deriving
            /// tangents from them, so the curve travels in the form it was authored in
            /// and comes back the same way. There is nothing to approximate and nothing
            /// to carry beside it.
            ///
            /// The order differs. nif.xml's `TBC` struct is tension, bias, continuity;
            /// FBX's data slots are tension, continuity, bias -- the middle two swap.
            /// </remarks>
            public const int TangentTcb = 0x00000200;
        }

        /// <summary>Converts seconds to FBX's integer time.</summary>
        public static long ToFbxTime(float seconds) => (long)MathF.Round(seconds * TimeUnitsPerSecond);

        /// <summary>Converts FBX's integer time back to seconds.</summary>
        public static float FromFbxTime(long time) => (float)((double)time / TimeUnitsPerSecond);

        /// <summary>
        /// Writes one sequence as a stack, binding its tracks to the models named by
        /// <paramref name="models"/>.
        /// </summary>
        /// <returns>The names of tracks with no model, whose animation was dropped.</returns>
        public static List<string> AddSequence(
            FbxScene scene, AnimSequence sequence, IReadOnlyDictionary<string, FbxObject> models)
        {
            var missing = new List<string>();

            FbxObject stack = scene.AddObject("AnimationStack", sequence.Name, string.Empty);

            // Both spans are written: the local one is the take as played, the
            // reference one the range the keys were authored over. Importers differ
            // over which they trust.
            stack.Properties.Set("LocalStart", "KTime", "Time", string.Empty, ToFbxTime(sequence.Start));
            stack.Properties.Set("LocalStop", "KTime", "Time", string.Empty, ToFbxTime(sequence.Stop));
            stack.Properties.Set("ReferenceStart", "KTime", "Time", string.Empty, ToFbxTime(sequence.Start));
            stack.Properties.Set("ReferenceStop", "KTime", "Time", string.Empty, ToFbxTime(sequence.Stop));

            // What the sequence does at its end, and what its root motion is measured
            // against. FBX has a place for neither, so they ride as properties like
            // everything else a take cannot say for itself.
            stack.Properties.SetUserString(
                CyclePropertyName,
                sequence.CycleType.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (sequence.AccumRootName.Length > 0)
                stack.Properties.SetUserString(AccumRootPropertyName, sequence.AccumRootName);

            // Everything the sequence marks. `start` and `end` the rebuild can invent;
            // a door's sound cue it cannot.
            if (sequence.TextKeys.Count > 0)
            {
                stack.Properties.SetUserString(
                    TextKeyCountProperty,
                    sequence.TextKeys.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

                for (int i = 0; i < sequence.TextKeys.Count; i++)
                {
                    (float time, string value) = sequence.TextKeys[i];

                    stack.Properties.SetUserString(
                        $"{TextKeyPrefix}{i}",
                        time.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

                    stack.Properties.SetUserString($"{TextKeyPrefix}{i}_value", value);
                }
            }

            FbxObject layer = scene.AddObject("AnimationLayer", LayerName, string.Empty);
            scene.Connect(layer, stack);

            foreach (AnimTrack track in sequence.Tracks)
            {
                // A track naming a node the scene does not have still travels, as far
                // as it can. Everything that lives on the *stack* -- a carried
                // interpolator, a constant, a pose -- needs no model to hang on, and a
                // sequence entry names its node by string, so the entry comes back
                // even though nothing here can play it. What cannot travel is a curve,
                // which animates a model and so needs one.
                models.TryGetValue(track.NodeName, out FbxObject? model);

                // Keys need a model, whether they move the node itself or one of its
                // properties. Reporting only the second let a whole transform track
                // vanish without a word -- a falmer scorpion lost one from every
                // sequence and nothing said so.
                if (model is null
                    && (track.HasKeys || track.Properties.Any(p => p.Curves.Any(c => c.HasKeys))))
                {
                    missing.Add(track.NodeName);
                }

                AddPose(stack, track);

                if (model is not null)
                {
                    AddChannel(scene, layer, model, "T", "Lcl Translation", track.Translation);
                    AddChannel(scene, layer, model, "R", "Lcl Rotation", track.Rotation);
                    AddChannel(scene, layer, model, "S", "Lcl Scaling", track.Scale);
                }

                foreach (AnimProperty property in track.Properties)
                {
                    if (property.CarriedInterpolator is { } carried)
                        AddCarried(stack, track.NodeName, property, carried);
                    else if (property.Empty)
                        AddEmpty(stack, track.NodeName, property);
                    else if (property.Constant is { } value)
                        AddConstant(stack, track.NodeName, property, value);
                    else if (model is not null)
                        AddPropertyChannel(scene, layer, model, property);

                    AddInterpolatorType(stack, track.NodeName, property);
                    AddDataId(stack, track, property);

                    if (property.ControllerFlags is { } flags)
                    {
                        stack.Properties.SetUserString(
                            ControllerFlagsKey(track.NodeName, property),
                            flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }

                    // Only when it is not the zero every other controller has, so a
                    // scene gains a property per emitter rather than per controller.
                    if (property.ControllerPhase is { } phase && phase != 0f)
                    {
                        stack.Properties.SetUserString(
                            ControllerPhaseKey(track.NodeName, property),
                            phase.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                // The node's own transform controller, which has no property to hang
                // its flags on and was rebuilt with a constant without them.
                if (track.ControllerFlags is { } transformFlags)
                {
                    stack.Properties.SetUserString(
                        TransformFlagsKey(track.NodeName),
                        transformFlags.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                // Which form the rotation was stored in, so a quaternion track is not
                // rebuilt as three Euler groups.
                if (track.RotationType is { } rotation)
                {
                    stack.Properties.SetUserString(
                        RotationTypeKey(track.NodeName),
                        rotation.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }


            }

            return missing;
        }

        /// <summary>Prefix on a stack property holding a track's constant value.</summary>
        /// <summary>Where a sequence's cycle type rides.</summary>
        public const string CyclePropertyName = "nif_cycle_type";

        /// <summary>Where the node a sequence accumulates against rides.</summary>
        public const string AccumRootPropertyName = "nif_accum_root";

        /// <summary>How many text keys the sequence marks.</summary>
        public const string TextKeyCountProperty = "nif_text_keys";

        /// <summary>Prefix on one text key's time, with `_value` beside it.</summary>
        public const string TextKeyPrefix = "nif_text_key_";

        /// <summary>Prefix on a stack property carrying a controller's flags.</summary>
        /// <remarks>
        /// Keyed by node, controller class and controller id, which is what identifies a
        /// controller — not by the property's encoded name, which several properties of
        /// one controller share and which is not always unique within a track.
        /// </remarks>
        public const string ControllerFlagsPrefix = "ctlrflags_";

        /// <summary>The key a controller's flags ride under.</summary>
        public static string ControllerFlagsKey(string nodeName, AnimProperty property) =>
            $"{ControllerFlagsPrefix}{nodeName}{AnimProperty.Separator}"
            + $"{property.ControllerType}{AnimProperty.Separator}{property.ControllerId}";

        /// <summary>The key a track's rotation form rides under.</summary>
        /// <remarks>
        /// See <see cref="Conversion.AnimTrack.RotationType"/>. Keyed by the node, as
        /// the transform flags are: a node has one transform track.
        /// </remarks>
        public static string RotationTypeKey(string nodeName) =>
            $"rotform_{nodeName}";

        /// <summary>The key the node's own transform controller's flags ride under.</summary>
        /// <remarks>
        /// A node has at most one of these, so the node names it: there is no class or
        /// id to tell two apart the way there is for the properties above.
        /// </remarks>
        public static string TransformFlagsKey(string nodeName) =>
            $"{ControllerFlagsPrefix}{nodeName}{AnimProperty.Separator}NiTransformController";

        /// <summary>Prefix on a stack property carrying a controller's phase.</summary>
        /// <remarks>
        /// Keyed as the flags are, and written only when non-zero: 28,084 of the game's
        /// controllers hold zero and 1,367 do not, and those are the particle emitters
        /// whose phase is what keeps them from pulsing in step.
        /// </remarks>
        public const string ControllerPhasePrefix = "ctlrphase_";

        /// <summary>The key a controller's phase rides under.</summary>
        public static string ControllerPhaseKey(string nodeName, AnimProperty property) =>
            $"{ControllerPhasePrefix}{nodeName}{AnimProperty.Separator}"
            + $"{property.ControllerType}{AnimProperty.Separator}{property.ControllerId}";

        public const string ConstantPrefix = "const_";

        /// <summary>Prefix on a stack property holding a track's fixed transform.</summary>
        public const string PosePrefix = "constxf_";

        /// <summary>
        /// Records a track that holds one transform for the whole sequence.
        /// </summary>
        /// <remarks>
        /// A <c>NiTransformInterpolator</c> with no data block holds a pose rather
        /// than keys, and it goes where a constant scalar goes: on the stack, the only
        /// per-take place FBX has. It cannot be the model's own transform — that is
        /// one per model where this is one per take, and two sequences can pose the
        /// same node differently.
        ///
        /// Written as the numbers the file holds, a quaternion and not a matrix, so
        /// that a file nobody edited comes back with the numbers it went out with.
        /// </remarks>
        private static void AddPose(FbxObject stack, AnimTrack track)
        {
            if (track.Pose is not { } pose)
                return;

            float[] parts =
            [
                pose.Translation.X, pose.Translation.Y, pose.Translation.Z,
                pose.Rotation.W, pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z,
                pose.Scale
            ];

            stack.Properties.SetUserString(
                $"{PosePrefix}{track.NodeName}",
                string.Join(
                    ' ',
                    parts.Select(p => p.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
        }

        /// <summary>Prefix on a stack property marking a track that holds nothing.</summary>
        public const string EmptyPrefix = "noval_";

        /// <summary>
        /// Records a track whose interpolator held neither keys nor a pose.
        /// </summary>
        /// <remarks>
        /// It cannot be a curve — there is nothing to key — and it cannot be a
        /// constant, because a constant says one thing and this says none. It is on
        /// the stack for the same reason both of those are: it is one per take.
        ///
        /// The value is the interpolator class, so the mark and the type travel
        /// together; a track that holds nothing is entirely described by what kind of
        /// nothing it is.
        /// </remarks>
        private static void AddEmpty(FbxObject stack, string nodeName, AnimProperty property)
        {
            stack.Properties.SetUserString(
                $"{EmptyPrefix}{nodeName}{AnimProperty.Separator}{property.Name}",
                property.InterpolatorType);
        }

        /// <summary>Prefix on the stack properties holding a carried interpolator.</summary>
        /// <remarks>
        /// Numbered rather than keyed by node and property, as the other stack
        /// carriers are: the fields have names of their own and would need a third
        /// separator inside a name that already has two.
        /// </remarks>
        public const string CarriedPrefix = "xi_";

        /// <summary>The property counting the carried interpolators on a stack.</summary>
        public const string CarriedCountProperty = "xi_count";

        /// <summary>
        /// Records an interpolator this layer cannot model, whole.
        /// </summary>
        /// <remarks>
        /// A `NiPathInterpolator` walks a node along a spline; nothing about that is a
        /// curve on an FBX property, so the block is carried rather than converted
        /// (see <see cref="FbxInterpolatorCodec"/>). It sits on the stack because it
        /// belongs to one sequence's entry, not to the model.
        /// </remarks>
        private static void AddCarried(
            FbxObject stack, string nodeName, AnimProperty property,
            IReadOnlyDictionary<string, string> fields)
        {
            int at = 0;

            if (int.TryParse(
                    stack.Properties.GetString(CarriedCountProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int count))
            {
                at = count;
            }

            string prefix = $"{CarriedPrefix}{at}_";

            stack.Properties.SetUserString($"{prefix}node", nodeName);
            stack.Properties.SetUserString($"{prefix}property", property.Name);

            foreach ((string name, string value) in fields)
                stack.Properties.SetUserString($"{prefix}f_{name}", value);

            stack.Properties.SetUserString(
                CarriedCountProperty,
                (at + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Prefix on a stack property naming a track's interpolator class.</summary>
        /// <remarks>
        /// On the stack rather than in the property's name, because the name is what
        /// FBX animates and changing its shape would change every track's identity.
        /// One entry per node and property, as constants are.
        /// </remarks>
        public const string InterpolatorPrefix = "interp_";

        /// <summary>Prefix on a stack property naming which data block a track shared.</summary>
        public const string DataIdPrefix = "datid_";

        /// <summary>
        /// Records which data block a track's keys came from.
        /// </summary>
        /// <remarks>
        /// Two interpolators can share one, and the game's files do it — twenty-five
        /// `NiFloatData` in `dlceclipsesky` serve twenty-seven interpolators. Without
        /// this each gets its own on the way back and the file gains blocks.
        /// </remarks>
        private static void AddDataId(FbxObject stack, AnimTrack track, AnimProperty property)
        {
            if (property.DataId < 0)
                return;

            // Only when the name picks out one track. A node can carry several
            // controllers whose encoded names are identical -- a shader with four
            // float controllers of the same kind -- and there is nothing in the name
            // to say which is which. Recorded anyway, the one id would be read back
            // onto all of them and they would collapse onto a single data block:
            // dlceclipsesky went from twenty-five to eleven that way.
            if (track.Properties.Count(p => p.Name == property.Name) != 1)
                return;

            stack.Properties.SetUserString(
                $"{DataIdPrefix}{track.NodeName}{AnimProperty.Separator}{property.Name}",
                property.DataId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Records which interpolator class a track came from.</summary>
        private static void AddInterpolatorType(FbxObject stack, string nodeName, AnimProperty property)
        {
            if (property.InterpolatorType.Length == 0)
                return;

            stack.Properties.SetUserString(
                $"{InterpolatorPrefix}{nodeName}{AnimProperty.Separator}{property.Name}",
                property.InterpolatorType);
        }

        /// <summary>
        /// Records a track that holds one value for the whole sequence.
        /// </summary>
        /// <remarks>
        /// This cannot be a curve: a curve with no keys is not a curve, and a curve
        /// with one invented key is a different animation that happens to look the
        /// same. Nor can it be the model's resting value, because that is one value
        /// per model where this is one per *take* — two sequences can hold different
        /// constants for the same property, which is exactly what the file this was
        /// found in does.
        ///
        /// The stack is the only per-take place in FBX, so it goes there, keyed by the
        /// node and the property it belongs to.
        /// </remarks>
        private static void AddConstant(
            FbxObject stack, string nodeName, AnimProperty property, float value)
        {
            // Typed rather than stringly, so the kind survives with the value: a
            // boolean constant and a float one are the same number and different
            // animations, and nothing else on the stack says which this is.
            stack.Properties.Set(
                $"{ConstantPrefix}{nodeName}{AnimProperty.Separator}{property.Name}",
                property switch
                {
                    { IsColor: true } => "ColorRGB",
                    { IsBoolean: true } => "bool",
                    _ => "Number"
                },
                string.Empty,
                FbxProperties.UserFlags,
                (double)value);
        }

        /// <summary>
        /// Writes a named scalar track as an animated property of a model.
        /// </summary>
        /// <remarks>
        /// The property has to be declared on the model as well as animated. A curve
        /// bound to a property the model does not have is dropped by most importers
        /// without complaint, since there is nothing for it to drive.
        ///
        /// Scalar properties differ from vector ones in how the curve node addresses
        /// them: the channel is <c>d|</c> plus the property's own name rather than
        /// one of <c>d|X</c>, <c>d|Y</c>, <c>d|Z</c>.
        /// </remarks>
        private static void AddPropertyChannel(
            FbxScene scene, FbxObject layer, FbxObject model, AnimProperty property)
        {
            if (!property.Curves.Any(c => c.HasKeys))
                return;

            string name = property.Name;

            double First(int i) =>
                property.Curves[i].HasKeys ? property.Curves[i].Keys[0].Value : 0d;

            if (property.IsColor)
            {
                // A colour is three channels of one property, addressed by axis just
                // as a translation is -- not three properties that happen to be
                // named alike.
                model.Properties.Set(name, "ColorRGB", "Color", "A+U", First(0), First(1), First(2));
            }
            else if (name == AnimProperty.VisibilityName)
            {
                // Standard rather than user-defined, so a DCC tool given this
                // actually hides the object.
                model.Properties.Set(name, "Visibility", string.Empty, "A", First(0));
            }
            else
            {
                model.Properties.Set(
                    name, property.IsBoolean ? "bool" : "Number", string.Empty, "A+U", First(0));
            }

            FbxObject node = scene.AddObject("AnimationCurveNode", name, string.Empty);

            // Scalar properties are addressed by their own name and vector ones by
            // axis, which is the only thing that says how many curves to expect.
            string[] channels = property.IsColor
                ? ["d|X", "d|Y", "d|Z"]
                : [$"d|{name}"];

            for (int i = 0; i < channels.Length; i++)
                node.Properties.Set(channels[i], "Number", string.Empty, "A", First(i));

            scene.Connect(node, layer);
            scene.ConnectToProperty(node, model, name);

            for (int i = 0; i < channels.Length; i++)
            {
                if (!property.Curves[i].HasKeys)
                    continue;

                FbxObject curve = AddCurve(scene, property.Curves[i]);
                scene.ConnectToProperty(curve, node, channels[i]);
            }
        }

        /// <summary>
        /// Writes one vector-valued channel — translation, rotation or scaling — of
        /// one model.
        /// </summary>
        /// <remarks>
        /// The curve node exists even when only one axis is keyed, because it is what
        /// holds the value the unkeyed axes rest at. Its defaults are taken from the
        /// model's own property, so an axis without keys keeps the pose the file
        /// already gives it rather than snapping to zero.
        /// </remarks>
        private static void AddChannel(
            FbxScene scene, FbxObject layer, FbxObject model,
            string channel, string property, AnimCurve[] curves)
        {
            if (!curves.Any(c => c.HasKeys))
                return;

            (double x, double y, double z) = model.Properties.GetVector3(
                property, property == "Lcl Scaling" ? 1 : 0);

            double[] defaults = [x, y, z];
            string[] axes = ["d|X", "d|Y", "d|Z"];

            FbxObject node = scene.AddObject("AnimationCurveNode", channel, string.Empty);

            for (int axis = 0; axis < 3; axis++)
            {
                // A keyed axis starts at its first key, so that the curve node's
                // default and the curve agree at time zero.
                double value = curves[axis].HasKeys ? curves[axis].Keys[0].Value : defaults[axis];
                node.Properties.Set(axes[axis], "Number", string.Empty, "A", value);
            }

            scene.Connect(node, layer);
            scene.ConnectToProperty(node, model, property);

            for (int axis = 0; axis < 3; axis++)
            {
                if (!curves[axis].HasKeys)
                    continue;

                FbxObject curve = AddCurve(scene, curves[axis]);
                scene.ConnectToProperty(curve, node, axes[axis]);
            }
        }

        /// <summary>Writes a single component's keys as an <c>AnimationCurve</c>.</summary>
        /// <remarks>
        /// The keys are stored as parallel arrays rather than as records. The
        /// attribute arrays are the awkward part: they are run-length encoded, with
        /// <c>KeyAttrRefCount</c> saying how many consecutive keys share each entry,
        /// so a curve whose keys all interpolate the same way carries exactly one.
        /// </remarks>
        public static FbxObject AddCurve(FbxScene scene, AnimCurve curve)
        {
            FbxObject o = scene.AddObject("AnimationCurve", string.Empty, string.Empty);
            FbxNode node = o.Node;

            var times = new long[curve.Keys.Count];
            var values = new float[curve.Keys.Count];

            for (int i = 0; i < curve.Keys.Count; i++)
            {
                times[i] = ToFbxTime(curve.Keys[i].Time);
                values[i] = curve.Keys[i].Value;
            }

            var flags = new List<int>();
            var refCounts = new List<int>();

            // Four floats an entry, and the entries are runs of like keys rather than
            // keys -- which is why the data has to be built alongside the flags and
            // broken into a new run whenever either changes.
            var attributes = new List<float>();

            foreach (AnimKey key in curve.Keys)
            {
                bool tcb = key.Tbc.X != 0f || key.Tbc.Y != 0f || key.Tbc.Z != 0f;

                int flag = tcb ? KeyFlags.Cubic | KeyFlags.TangentTcb : FlagsOf(key.Interpolation);

                // Tension, continuity, bias -- FBX's order, not nif.xml's.
                float tension = tcb ? key.Tbc.X : 0f;
                float continuity = tcb ? key.Tbc.Z : 0f;
                float bias = tcb ? key.Tbc.Y : 0f;

                bool same = flags.Count > 0
                    && flags[^1] == flag
                    && attributes[^4] == tension
                    && attributes[^3] == continuity
                    && attributes[^2] == bias;

                if (same)
                {
                    refCounts[^1]++;
                    continue;
                }

                flags.Add(flag);
                refCounts.Add(1);
                attributes.AddRange([tension, continuity, bias, 0f]);
            }

            node.Nodes.Add(new FbxNode("Default", (double)(values.Length > 0 ? values[0] : 0f)));
            node.Nodes.Add(new FbxNode("KeyVer", KeyVersion));
            node.Nodes.Add(new FbxNode("KeyTime", times));
            node.Nodes.Add(new FbxNode("KeyValueFloat", values));
            node.Nodes.Add(new FbxNode("KeyAttrFlags", flags.ToArray()));

            // Four floats per attribute entry: the slopes and weights of a tangent, or
            // the three TCB numbers for a key shaped that way. Zero means "work them
            // out", which is what auto tangents ask for and the only honest answer for
            // keys that arrived without any.
            node.Nodes.Add(new FbxNode("KeyAttrDataFloat", attributes.ToArray()));

            node.Nodes.Add(new FbxNode("KeyAttrRefCount", refCounts.ToArray()));

            return o;
        }

        private static int FlagsOf(AnimInterpolation interpolation) => interpolation switch
        {
            AnimInterpolation.Constant => KeyFlags.Constant,
            AnimInterpolation.Linear => KeyFlags.Linear,
            _ => KeyFlags.Cubic | KeyFlags.TangentAuto
        };
    }
}
