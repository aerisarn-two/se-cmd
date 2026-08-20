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
        private static class KeyFlags
        {
            public const int Constant = 0x00000002;
            public const int Linear = 0x00000004;
            public const int Cubic = 0x00000008;

            /// <summary>Let the importer choose tangents; NIF quadratic keys carry
            /// tangents FBX cannot express directly, so this reproduces the shape.</summary>
            public const int TangentAuto = 0x00000100;
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

            FbxObject layer = scene.AddObject("AnimationLayer", LayerName, string.Empty);
            scene.Connect(layer, stack);

            foreach (AnimTrack track in sequence.Tracks)
            {
                if (!models.TryGetValue(track.NodeName, out FbxObject? model))
                {
                    missing.Add(track.NodeName);
                    continue;
                }

                AddPose(stack, track);

                AddChannel(scene, layer, model, "T", "Lcl Translation", track.Translation);
                AddChannel(scene, layer, model, "R", "Lcl Rotation", track.Rotation);
                AddChannel(scene, layer, model, "S", "Lcl Scaling", track.Scale);

                foreach (AnimProperty property in track.Properties)
                {
                    if (property.Empty)
                        AddEmpty(stack, track.NodeName, property);
                    else if (property.Constant is { } value)
                        AddConstant(stack, track.NodeName, property, value);
                    else
                        AddPropertyChannel(scene, layer, model, property);

                    AddInterpolatorType(stack, track.NodeName, property);
                }
            }

            return missing;
        }

        /// <summary>Prefix on a stack property holding a track's constant value.</summary>
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

        /// <summary>Prefix on a stack property naming a track's interpolator class.</summary>
        /// <remarks>
        /// On the stack rather than in the property's name, because the name is what
        /// FBX animates and changing its shape would change every track's identity.
        /// One entry per node and property, as constants are.
        /// </remarks>
        public const string InterpolatorPrefix = "interp_";

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

            foreach (AnimKey key in curve.Keys)
            {
                int flag = FlagsOf(key.Interpolation);

                if (flags.Count > 0 && flags[^1] == flag)
                    refCounts[^1]++;
                else
                {
                    flags.Add(flag);
                    refCounts.Add(1);
                }
            }

            node.Nodes.Add(new FbxNode("Default", (double)(values.Length > 0 ? values[0] : 0f)));
            node.Nodes.Add(new FbxNode("KeyVer", KeyVersion));
            node.Nodes.Add(new FbxNode("KeyTime", times));
            node.Nodes.Add(new FbxNode("KeyValueFloat", values));
            node.Nodes.Add(new FbxNode("KeyAttrFlags", flags.ToArray()));

            // Four floats per attribute entry: the slopes and weights of the
            // tangents. Zero means "work them out", which is what auto tangents ask
            // for and the only honest answer for keys that arrived without any.
            node.Nodes.Add(new FbxNode("KeyAttrDataFloat", new float[flags.Count * 4]));

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
