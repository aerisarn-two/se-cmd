using SECmd.Conversion;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Reading transform animation out of a NIF.
    /// </summary>
    /// <remarks>
    /// nifly's animated fixture is a Skyrim LE effect with three sequences —
    /// mBegin, mLoop, mEnd — driving one node through separate X, Y and Z rotation
    /// groups. That covers the common shape but not quaternion keys or scale, so
    /// those are built here.
    /// </remarks>
    public class AnimationReadTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        private static List<AnimSequence> Animated() =>
            Load("TestNifFile_Animated_LE.nif").ReadAnimations();

        [Fact]
        public void ReadsEverySequence()
        {
            Assert.Equal(["mBegin", "mLoop", "mEnd"], Animated().Select(s => s.Name));
        }

        [Fact]
        public void TracksNameANodeInTheFile()
        {
            NifModel model = Load("TestNifFile_Animated_LE.nif");

            var nodes = model.Blocks.Select(model.GetName).ToHashSet(StringComparer.Ordinal);

            // A track whose target is not in the file animates nothing, which is
            // indistinguishable from no animation at all once exported.
            Assert.All(model.ReadAnimations().SelectMany(s => s.Tracks),
                t => Assert.Contains(t.NodeName, nodes));
        }

        [Fact]
        public void EverySequenceCoversItsKeys()
        {
            foreach (AnimSequence sequence in Animated())
            {
                (float start, float stop) = sequence.KeySpan();

                Assert.True(sequence.Stop > sequence.Start, $"{sequence.Name} has an empty span");
                Assert.InRange(start, sequence.Start, sequence.Stop);
                Assert.InRange(stop, sequence.Start, sequence.Stop);
            }
        }

        [Fact]
        public void SeparateAxisGroupsKeepTheirOwnKeyCounts()
        {
            AnimTrack track = Animated().First(s => s.Name == "mBegin").Tracks[0];

            // Rotation stored as three float groups is the one case where the axes
            // are keyed independently; collapsing them to a common count would
            // invent keys on two of the three.
            Assert.Equal([1, 3, 1], track.Rotation.Select(c => c.Keys.Count));
            Assert.Equal([4, 4, 4], track.Translation.Select(c => c.Keys.Count));
        }

        [Fact]
        public void RotationArrivesInDegrees()
        {
            var values = Animated()
                .SelectMany(s => s.Tracks)
                .SelectMany(t => t.Rotation)
                .SelectMany(c => c.Keys)
                .Select(k => k.Value)
                .ToList();

            Assert.NotEmpty(values);

            // This effect spins five full turns, so the largest value is 1800 in
            // degrees and about 31 in radians. Nothing here is bounded by 360:
            // a rotation track is free to wind past a full turn, and clamping it
            // would turn several revolutions into a fraction of one.
            Assert.Equal(1800f, values.Max(MathF.Abs), 1);
            Assert.All(values, v => Assert.True(float.IsFinite(v)));
        }

        // --- built here, for what the fixture does not cover --------------------

        /// <summary>A one-track sequence whose data block the caller fills in.</summary>
        private static NifModel BuildSequence(Action<NifModel, NifItem> fillData)
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem data = model.InsertBlock("NiTransformData");
            fillData(model, data);

            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");
            model.SetRef(interpolator, "Data", data);

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "test");

            NifItem entry = model.SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            model.SetRef(entry, "Interpolator", interpolator);
            model.SetString(entry, "Node Name", "Bone");

            return model;
        }

        /// <summary>Sizes a key group and says which interpolation it uses.</summary>
        private static NifItem KeyGroup(NifModel model, NifItem data, string field, int count, uint keyType)
        {
            model.FindItem(data, $@"{field}\Num Keys")!.Value.SetCount((uint)count);

            // The interpolation field only exists once the group has keys, and the
            // answer to that was cached back when it had none.
            data.InvalidateConditionsRecursive();
            model.FindItem(data, $@"{field}\Interpolation")!.Value.SetCount(keyType);

            data.InvalidateConditionsRecursive();
            NifItem keys = model.FindItem(data, $@"{field}\Keys")!;
            model.UpdateArraySize(keys);
            return keys;
        }

        [Fact]
        public void UniformScaleIsReplicatedToEveryAxis()
        {
            const uint Linear = 1;

            NifModel model = BuildSequence((m, data) =>
            {
                NifItem keys = KeyGroup(m, data, "Scales", 2, Linear);

                m.FindItem(keys.Children[0], "Time")!.Value.SetFloat(0f);
                m.FindItem(keys.Children[0], "Value")!.Value.SetFloat(1f);
                m.FindItem(keys.Children[1], "Time")!.Value.SetFloat(1f);
                m.FindItem(keys.Children[1], "Value")!.Value.SetFloat(2f);
            });

            AnimTrack track = model.ReadAnimations().Single().Tracks.Single();

            // NIF scales uniformly and FBX does not, so a single NIF curve has to
            // become three or the node scales on X alone.
            Assert.All(track.Scale, c => Assert.Equal(
                [(0f, 1f), (1f, 2f)], c.Keys.Select(k => (k.Time, k.Value))));
        }

        [Fact]
        public void QuaternionKeysDecomposeLikeAStaticRotation()
        {
            const uint Linear = 1;

            // A rotation with all three axes involved, so a wrong Euler order or a
            // transposed matrix cannot pass by symmetry.
            NifMatrix33 rotation = NifTransform.RotationFromEulerDegrees(20f, -35f, 50f);
            NifQuat quaternion = new NifTransform(new NifVector3(), rotation, 1f).ToQuaternion();

            NifModel model = BuildSequence((m, data) =>
            {
                m.FindItem(data, "Num Rotation Keys")!.Value.SetCount(1);
                data.InvalidateConditionsRecursive();
                m.FindItem(data, "Rotation Type")!.Value.SetCount(Linear);

                data.InvalidateConditionsRecursive();
                NifItem keys = m.FindItem(data, "Quaternion Keys")!;
                m.UpdateArraySize(keys);

                m.FindItem(keys.Children[0], "Time")!.Value.SetFloat(0.5f);
                m.FindItem(keys.Children[0], "Value")!.Value.Set(quaternion);
            });

            AnimTrack track = model.ReadAnimations().Single().Tracks.Single();

            NifVector3 expected = new NifTransform(new NifVector3(), rotation, 1f).ToEulerDegrees();

            // An animated node has to agree with its own rest pose, so the keys have
            // to be decomposed exactly the way the static transform is.
            Assert.Equal(expected.X, track.Rotation[0].Keys[0].Value, 3);
            Assert.Equal(expected.Y, track.Rotation[1].Keys[0].Value, 3);
            Assert.Equal(expected.Z, track.Rotation[2].Keys[0].Value, 3);

            Assert.All(track.Rotation, c => Assert.Equal(0.5f, c.Keys[0].Time));
        }

        [Fact]
        public void QuaternionKeysAreReadAsSmooth()
        {
            const uint Linear = 1;

            NifModel model = BuildSequence((m, data) =>
            {
                m.FindItem(data, "Num Rotation Keys")!.Value.SetCount(1);
                data.InvalidateConditionsRecursive();
                m.FindItem(data, "Rotation Type")!.Value.SetCount(Linear);

                data.InvalidateConditionsRecursive();
                m.UpdateArraySize(m.FindItem(data, "Quaternion Keys")!);
            });

            AnimTrack track = model.ReadAnimations().Single().Tracks.Single();

            // Whatever the file calls the quaternion group, the rotation it stands
            // for is a slerp; straight lines between Euler angles are not.
            Assert.All(track.Rotation,
                c => Assert.Equal(AnimInterpolation.Cubic, c.Keys[0].Interpolation));
        }

        [Fact]
        public void SequenceWithoutTransformTracksIsSkipped()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "floats only");

            NifItem entry = model.SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            model.SetRef(entry, "Interpolator", model.InsertBlock("NiFloatInterpolator"));
            model.SetString(entry, "Node Name", "Bone");

            // Shader, visibility and emitter controllers share these sequences.
            // Reading their interpolators as transforms would move nodes that were
            // never animated -- so the track exists, and moves nothing.
            AnimTrack track = Assert.Single(Assert.Single(model.ReadAnimations()).Tracks);

            Assert.Empty(track.Curves.SelectMany(c => c.Keys));
            Assert.Null(track.Pose);
        }
    }
}
