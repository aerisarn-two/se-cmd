using SECmd.Nif;
using MeshIO.Formats.Fbx;
using SECmd.Conversion;

namespace SECmd.Fbx
{
    /// <summary>
    /// Reads animation back out of an FBX scene.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="FbxAnimWriter"/>, and like it mostly a matter of
    /// following connections: a curve is only identifiable as "the X component of
    /// this model's rotation" by the two object-to-property edges above it. Nothing
    /// in a curve's own record says what it drives.
    /// </remarks>
    public static class FbxAnimReader
    {
        /// <summary>Every animation stack in the scene, in file order.</summary>
        public static List<AnimSequence> ReadAnimations(this FbxScene scene)
        {
            var sequences = new List<AnimSequence>();

            foreach (FbxObject stack in scene.OfClass("AnimationStack"))
            {
                if (ReadStack(scene, stack) is { } sequence)
                    sequences.Add(sequence);
            }

            return sequences;
        }

        /// <summary>
        /// Reads the tracks that hold one value for the whole take.
        /// </summary>
        /// <remarks>
        /// These live on the stack rather than as curves, because a curve with no keys
        /// is not a curve and the model's resting value is one per model where this is
        /// one per take. See <see cref="FbxAnimWriter.AddConstant"/>.
        /// </remarks>
        private static void ReadConstants(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxProperty70 property in stack.Properties.All)
            {
                string name = property.Name;

                if (!name.StartsWith(FbxAnimWriter.ConstantPrefix, StringComparison.Ordinal))
                    continue;

                // <node>|<property name>, where the property name has bars of its own,
                // so the split is on the first only.
                string rest = name[FbxAnimWriter.ConstantPrefix.Length..];
                int bar = rest.IndexOf(AnimProperty.Separator);

                if (bar <= 0)
                    continue;

                string nodeName = rest[..bar];
                string propertyName = rest[(bar + 1)..];

                // Read as the number it is, not as text. `AddConstant` writes a real
                // double, and taking `.ToString()` of it formats in the *current*
                // culture while the parse below expected an invariant one: on any
                // comma-decimal machine a stored 0.5 came back as "0,5", failed to
                // parse, and the track was dropped without a word. If it was the
                // sequence's only content the whole sequence went with it -- which for
                // these is a loop that hides a mesh outright.
                //
                // Only non-integral constants were affected, because "1" formats the
                // same everywhere. That is why every fixture passed.
                if (property.Values.Count == 0)
                    continue;

                var value = (float)FbxProperties.ToDouble(property.Values[0], double.NaN);

                if (float.IsNaN(value))
                    continue;

                if (!tracks.TryGetValue(nodeName, out AnimTrack? track))
                    tracks[nodeName] = track = new AnimTrack { NodeName = nodeName };

                (string type, string id, string interpolatorId, string propertyType) =
                    AnimProperty.FromPropertyName(propertyName);

                // The declared type is what says which kind of animation this is; the
                // value alone cannot, since a boolean constant and a float one look
                // the same.
                bool colour = property.Type == "ColorRGB";

                track.Properties.Add(new AnimProperty(colour ? 3 : 1)
                {
                    Name = propertyName,
                    IsBoolean = property.Type == "bool",
                    ControllerType = type,
                    ControllerId = id,
                    InterpolatorId = interpolatorId,
                    PropertyType = propertyType,
                    Constant = value
                });
            }
        }

        /// <summary>Reads the interpolators carried whole.</summary>
        /// <remarks>See <see cref="FbxAnimWriter.AddCarried"/>.</remarks>
        private static void ReadCarried(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            if (!int.TryParse(
                    stack.Properties.GetString(FbxAnimWriter.CarriedCountProperty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int count))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{FbxAnimWriter.CarriedPrefix}{i}_";
                string nodeName = stack.Properties.GetString($"{prefix}node");
                string propertyName = stack.Properties.GetString($"{prefix}property");

                if (nodeName.Length == 0)
                    continue;

                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                string fieldPrefix = $"{prefix}f_";

                foreach (FbxProperty70 property in stack.Properties.All)
                {
                    if (property.Name.StartsWith(fieldPrefix, StringComparison.Ordinal)
                        && property.Values.FirstOrDefault() is string value)
                    {
                        fields[property.Name[fieldPrefix.Length..]] = value;
                    }
                }

                if (fields.Count == 0)
                    continue;

                if (!tracks.TryGetValue(nodeName, out AnimTrack? track))
                    tracks[nodeName] = track = new AnimTrack { NodeName = nodeName };

                (string type, string id, string interpolatorId, string propertyType) =
                    AnimProperty.FromPropertyName(propertyName);

                track.Properties.Add(new AnimProperty
                {
                    Name = propertyName,
                    InterpolatorType = fields.GetValueOrDefault(
                        FbxInterpolatorCodec.TypeSuffix, string.Empty),
                    ControllerType = type,
                    ControllerId = id,
                    InterpolatorId = interpolatorId,
                    PropertyType = propertyType,
                    CarriedInterpolator = fields
                });
            }
        }

        /// <summary>Reads the tracks whose interpolator holds nothing.</summary>
        /// <remarks>See <see cref="FbxAnimWriter.AddEmpty"/>.</remarks>
        private static void ReadEmpties(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxProperty70 property in stack.Properties.All)
            {
                if (!property.Name.StartsWith(FbxAnimWriter.EmptyPrefix, StringComparison.Ordinal))
                    continue;

                string rest = property.Name[FbxAnimWriter.EmptyPrefix.Length..];
                int bar = rest.IndexOf(AnimProperty.Separator);

                if (bar <= 0)
                    continue;

                string nodeName = rest[..bar];
                string propertyName = rest[(bar + 1)..];

                if (!tracks.TryGetValue(nodeName, out AnimTrack? track))
                    tracks[nodeName] = track = new AnimTrack { NodeName = nodeName };

                (string type, string id, string interpolatorId, string propertyType) =
                    AnimProperty.FromPropertyName(propertyName);

                string interpolator = property.Values.FirstOrDefault() as string ?? string.Empty;

                track.Properties.Add(new AnimProperty
                {
                    Name = propertyName,
                    IsBoolean = interpolator.Contains("Bool", StringComparison.Ordinal),
                    InterpolatorType = interpolator,
                    ControllerType = type,
                    ControllerId = id,
                    InterpolatorId = interpolatorId,
                    PropertyType = propertyType,
                    Empty = true
                });
            }
        }

        /// <summary>Reads the tracks that hold one transform for the whole take.</summary>
        /// <remarks>See <see cref="FbxAnimWriter.AddPose"/>.</remarks>
        private static void ReadPoses(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxProperty70 property in stack.Properties.All)
            {
                if (!property.Name.StartsWith(FbxAnimWriter.PosePrefix, StringComparison.Ordinal))
                    continue;

                string nodeName = property.Name[FbxAnimWriter.PosePrefix.Length..];
                string[] parts = (property.Values.FirstOrDefault()?.ToString() ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length != 8)
                    continue;

                var numbers = new float[8];
                bool ok = true;

                for (int i = 0; i < 8 && ok; i++)
                {
                    ok = float.TryParse(
                        parts[i],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out numbers[i]);
                }

                if (!ok)
                    continue;

                if (!tracks.TryGetValue(nodeName, out AnimTrack? track))
                    tracks[nodeName] = track = new AnimTrack { NodeName = nodeName };

                track.Pose = new AnimPose(
                    new NifVector3(numbers[0], numbers[1], numbers[2]),
                    new NifQuat(numbers[3], numbers[4], numbers[5], numbers[6]),
                    numbers[7]);
            }
        }

        /// <summary>
        /// Puts each track's interpolator class back.
        /// </summary>
        /// <remarks>
        /// Applied after the curves are read, since it names tracks that already
        /// exist rather than creating any. A name for a track that is not there is
        /// ignored: the curve it belonged to may have been removed in a DCC tool.
        /// </remarks>
        private static void ReadInterpolatorTypes(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxProperty70 property in stack.Properties.All)
            {
                if (!property.Name.StartsWith(FbxAnimWriter.InterpolatorPrefix, StringComparison.Ordinal))
                    continue;

                string rest = property.Name[FbxAnimWriter.InterpolatorPrefix.Length..];
                int bar = rest.IndexOf(AnimProperty.Separator);

                if (bar <= 0 || !tracks.TryGetValue(rest[..bar], out AnimTrack? track))
                    continue;

                string name = rest[(bar + 1)..];
                string type = property.Values.FirstOrDefault()?.ToString() ?? string.Empty;

                foreach (AnimProperty p in track.Properties.Where(p => p.Name == name))
                    p.InterpolatorType = type;
            }
        }

        /// <summary>Puts back which data block each track's keys shared.</summary>
        /// <remarks>See <see cref="FbxAnimWriter.AddDataId"/>.</remarks>
        private static void ReadDataIds(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxProperty70 property in stack.Properties.All)
            {
                if (!property.Name.StartsWith(FbxAnimWriter.DataIdPrefix, StringComparison.Ordinal))
                    continue;

                string rest = property.Name[FbxAnimWriter.DataIdPrefix.Length..];
                int bar = rest.IndexOf(AnimProperty.Separator);

                if (bar <= 0 || !tracks.TryGetValue(rest[..bar], out AnimTrack? track))
                    continue;

                if (!int.TryParse(
                        property.Values.FirstOrDefault()?.ToString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int id))
                {
                    continue;
                }

                string name = rest[(bar + 1)..];

                // Written only when the name picks out one track, and applied on the
                // same terms: sharing the wrong data block is worse than not sharing.
                if (track.Properties.Count(p => p.Name == name) == 1)
                    track.Properties.First(p => p.Name == name).DataId = id;
            }
        }

        /// <summary>Puts back the flags of the controller each property belongs to.</summary>
        /// <remarks>See <see cref="FbxAnimWriter.ControllerFlagsKey"/>.</remarks>
        private static void ReadControllerFlags(FbxObject stack, Dictionary<string, AnimTrack> tracks)
        {
            foreach ((string nodeName, AnimTrack track) in tracks)
            {
                foreach (AnimProperty property in track.Properties)
                {
                    string text = stack.Properties.GetString(
                        FbxAnimWriter.ControllerFlagsKey(nodeName, property));

                    if (uint.TryParse(
                            text,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out uint flags))
                    {
                        property.ControllerFlags = flags;
                    }
                }
            }
        }

        private static AnimSequence? ReadStack(FbxScene scene, FbxObject stack)
        {
            var sequence = new AnimSequence
            {
                Name = stack.Name,
                Start = TimeProperty(stack, "LocalStart"),
                Stop = TimeProperty(stack, "LocalStop"),
                CycleType = uint.TryParse(
                    stack.Properties.GetString(FbxAnimWriter.CyclePropertyName),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint cycle)
                    ? cycle
                    : 0,
                AccumRootName = stack.Properties.GetString(FbxAnimWriter.AccumRootPropertyName)
            };

            // One track per model, however many channels turn out to drive it.
            var tracks = new Dictionary<string, AnimTrack>(StringComparer.Ordinal);

            foreach (FbxObject layer in scene.ChildrenOf(stack.Id).Where(o => o.Class == "AnimationLayer"))
            {
                foreach (FbxObject node in scene.ChildrenOf(layer.Id).Where(o => o.Class == "AnimationCurveNode"))
                    ReadCurveNode(scene, node, tracks);
            }

            ReadConstants(stack, tracks);
            ReadEmpties(stack, tracks);
            ReadCarried(stack, tracks);
            ReadPoses(stack, tracks);
            ReadInterpolatorTypes(stack, tracks);
            ReadDataIds(stack, tracks);
            ReadControllerFlags(stack, tracks);

            // A track with no keys is kept only when it holds a constant, which is an
            // animation with nothing to draw rather than an empty one.
            sequence.Tracks.AddRange(
                tracks.Values.Where(t => t.Says));

            if (sequence.Tracks.Count == 0)
                return null;

            if (!(sequence.Stop > sequence.Start))
                (sequence.Start, sequence.Stop) = sequence.KeySpan();

            return sequence;
        }

        /// <summary>Reads one channel and files its curves under the model it drives.</summary>
        private static void ReadCurveNode(
            FbxScene scene, FbxObject node, Dictionary<string, AnimTrack> tracks)
        {
            foreach (FbxConnection binding in scene.Connections)
            {
                if (binding.Kind != FbxConnectionKind.ObjectProperty || binding.SourceId != node.Id)
                    continue;

                if (scene[binding.DestinationId] is not { Class: "Model" } model)
                    continue;

                AnimTrack track = TrackFor(tracks, model.Name);

                if (ChannelOf(track, binding.PropertyName) is { } channel)
                {
                    ReadCurvesInto(scene, node, channel);
                    continue;
                }

                // Anything else the curve node drives is a named scalar, and the
                // property's own name is what says which NIF controller it came from.
                ReadPropertyInto(scene, node, model, track, binding.PropertyName);
            }
        }

        /// <summary>
        /// Reads a scalar property's curve, recovering the NIF identity from its name.
        /// </summary>
        /// <remarks>
        /// The declared type on the model is what distinguishes a boolean track from a
        /// float one. Losing it would turn an emitter's on/off switch into a rate.
        /// </remarks>
        private static void ReadPropertyInto(
            FbxScene scene, FbxObject node, FbxObject model, AnimTrack track, string name)
        {
            (string controllerType, string controllerId, string interpolatorId, string propertyType) =
                AnimProperty.FromPropertyName(name);

            if (controllerType.Length == 0)
                return;

            string declared = model.Properties.Find(name)?.Type ?? string.Empty;
            bool colour = declared is "ColorRGB" or "Color";

            var property = new AnimProperty(colour ? 3 : 1)
            {
                Name = name,
                IsBoolean = declared is "bool" or "Visibility",
                ControllerType = controllerType,
                ControllerId = controllerId,
                InterpolatorId = interpolatorId,
                PropertyType = propertyType
            };

            string[] channels = colour ? ["d|X", "d|Y", "d|Z"] : [$"d|{name}"];

            foreach (FbxConnection c in scene.Connections)
            {
                if (c.Kind != FbxConnectionKind.ObjectProperty || c.DestinationId != node.Id)
                    continue;

                int axis = Array.IndexOf(channels, c.PropertyName);

                if (axis >= 0 && scene[c.SourceId] is { Class: "AnimationCurve" } curve)
                    ReadCurve(curve, property.Curves[axis]);
            }

            if (property.Curves.Any(c => c.HasKeys))
                track.Properties.Add(property);
        }

        private static AnimTrack TrackFor(Dictionary<string, AnimTrack> tracks, string modelName)
        {
            string name = NameEncoding.Unsanitize(modelName);

            // The holder interposed on export carries the transform but is not a node
            // of its own, so a track bound to it belongs to the shape it wraps.
            if (name.EndsWith("_support", StringComparison.Ordinal))
                name = name[..^"_support".Length];

            if (!tracks.TryGetValue(name, out AnimTrack? track))
                tracks[name] = track = new AnimTrack { NodeName = name };

            return track;
        }

        private static AnimCurve[]? ChannelOf(AnimTrack track, string property) => property switch
        {
            "Lcl Translation" => track.Translation,
            "Lcl Rotation" => track.Rotation,
            "Lcl Scaling" => track.Scale,
            _ => null
        };

        private static void ReadCurvesInto(FbxScene scene, FbxObject node, AnimCurve[] channel)
        {
            foreach (FbxConnection c in scene.Connections)
            {
                if (c.Kind != FbxConnectionKind.ObjectProperty || c.DestinationId != node.Id)
                    continue;

                if (scene[c.SourceId] is not { Class: "AnimationCurve" } curve)
                    continue;

                int axis = c.PropertyName switch
                {
                    "d|X" => 0,
                    "d|Y" => 1,
                    "d|Z" => 2,
                    _ => -1
                };

                if (axis >= 0)
                    ReadCurve(curve, channel[axis]);
            }
        }

        /// <summary>Reads one curve's parallel key arrays into a curve.</summary>
        /// <remarks>
        /// The interpolation arrays are run-length encoded against the keys, so they
        /// are expanded here. A curve whose runs do not add up to its key count is
        /// read as far as they go rather than refused: the keys are still good.
        /// </remarks>
        public static void ReadCurve(FbxObject curve, AnimCurve into)
        {
            long[] times = ReadLongs(curve.Child("KeyTime"));
            float[] values = ReadFloats(curve.Child("KeyValueFloat"));

            var interpolations = ExpandFlags(
                ReadInts(curve.Child("KeyAttrFlags")),
                ReadInts(curve.Child("KeyAttrRefCount")),
                times.Length);

            int count = Math.Min(times.Length, values.Length);

            for (int i = 0; i < count; i++)
            {
                into.Keys.Add(new AnimKey(
                    FbxAnimWriter.FromFbxTime(times[i]),
                    values[i],
                    i < interpolations.Count ? interpolations[i] : AnimInterpolation.Linear));
            }
        }

        private static List<AnimInterpolation> ExpandFlags(int[] flags, int[] refCounts, int keyCount)
        {
            var result = new List<AnimInterpolation>(keyCount);

            for (int i = 0; i < flags.Length && result.Count < keyCount; i++)
            {
                // A run count of zero would loop forever; one key is the least a run
                // can honestly describe.
                int run = i < refCounts.Length ? Math.Max(refCounts[i], 1) : keyCount - result.Count;

                for (int k = 0; k < run && result.Count < keyCount; k++)
                    result.Add(FromFlags(flags[i]));
            }

            return result;
        }

        private static AnimInterpolation FromFlags(int flags)
        {
            const int Constant = 0x00000002;
            const int Linear = 0x00000004;

            if ((flags & Constant) != 0)
                return AnimInterpolation.Constant;

            return (flags & Linear) != 0 ? AnimInterpolation.Linear : AnimInterpolation.Cubic;
        }

        private static float TimeProperty(FbxObject o, string name)
        {
            IReadOnlyList<object?> values = o.Properties.ValuesOf(name);

            return values.Count > 0 && values[0] is not null
                ? FbxAnimWriter.FromFbxTime(System.Convert.ToInt64(values[0]))
                : 0f;
        }

        // --- array readers ----------------------------------------------------
        //
        // FBX picks the narrowest representation that fits, so the same field can
        // arrive as any of several array types depending on who wrote the file.

        private static long[] ReadLongs(FbxNode? node) => node?.Properties.FirstOrDefault() switch
        {
            long[] l => l,
            int[] i => Array.ConvertAll(i, v => (long)v),
            _ => []
        };

        private static float[] ReadFloats(FbxNode? node) => node?.Properties.FirstOrDefault() switch
        {
            float[] f => f,
            double[] d => Array.ConvertAll(d, v => (float)v),
            _ => []
        };

        private static int[] ReadInts(FbxNode? node) => node?.Properties.FirstOrDefault() switch
        {
            int[] i => i,
            long[] l => Array.ConvertAll(l, v => (int)v),
            _ => []
        };
    }
}
