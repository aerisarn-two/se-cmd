using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Animation of colours rather than of single numbers.
    /// </summary>
    /// <remarks>
    /// A colour track is a <c>NiPoint3Interpolator</c>, and several controllers use
    /// one — material colour, lighting shader colour, effect shader colour. They
    /// agree about nothing except that, which is why the interpolator rather than the
    /// controller class is what identifies them here.
    ///
    /// No fixture in the corpus has one, so everything below is built.
    /// </remarks>
    public class ColorAnimationTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const uint Linear = 1;

        /// <summary>The colour keys every test uses: three distinct channels.</summary>
        private static readonly (float Time, NifVector3 Value)[] Keys =
        [
            (0f, new NifVector3(1f, 0f, 0f)),
            (0.5f, new NifVector3(0.25f, 0.5f, 0.75f)),
            (1f, new NifVector3(0f, 1f, 0.5f))
        ];

        /// <summary>Sizes a key group and states its interpolation.</summary>
        private static NifItem KeyGroup(NifModel model, NifItem data, string field, int count)
        {
            model.FindItem(data, $@"{field}\Num Keys")!.Value.SetCount((uint)count);
            data.InvalidateConditionsRecursive();

            model.FindItem(data, $@"{field}\Interpolation")!.Value.SetCount(Linear);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, $@"{field}\Keys")!;
            model.UpdateArraySize(keys);
            return keys;
        }

        /// <summary>A <c>NiPoint3Interpolator</c> over <see cref="Keys"/>.</summary>
        private static NifItem BuildInterpolator(NifModel model)
        {
            NifItem data = model.InsertBlock("NiPosData");
            NifItem keys = KeyGroup(model, data, "Data", Keys.Length);

            for (int i = 0; i < Keys.Length; i++)
            {
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat(Keys[i].Time);
                model.FindItem(keys.Children[i], "Value")!.Value.Set(Keys[i].Value);
            }

            NifItem interpolator = model.InsertBlock("NiPoint3Interpolator");
            model.SetRef(interpolator, "Data", data);
            return interpolator;
        }

        /// <summary>A root with one named node under it, ready to be animated.</summary>
        private static (NifModel Model, NifItem Node) BuildScene()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "Bone");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            return (model, node);
        }

        /// <summary>A colour track inside a sequence, as a shader controller writes it.</summary>
        private static NifModel BuildSequence()
        {
            (NifModel model, _) = BuildScene();

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "glow");

            NifItem entry = model.SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            model.SetRef(entry, "Interpolator", BuildInterpolator(model));
            model.SetString(entry, "Node Name", "Bone");
            model.SetString(entry, "Controller Type", "BSEffectShaderPropertyColorController");
            model.SetString(entry, "Property Type", "BSEffectShaderProperty");

            return model;
        }

        // --- reading -----------------------------------------------------------

        [Fact]
        public void ColorTracksBecomeThreeCurves()
        {
            AnimProperty property = Assert.Single(
                Assert.Single(Assert.Single(BuildSequence().ReadAnimations()).Tracks).Properties);

            Assert.True(property.IsColor);
            Assert.Equal(3, property.Curves.Length);

            // FBX keys each channel separately, so a colour that arrived as one
            // vector per key has to come apart into three curves.
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.Equal(
                    Keys.Select(k => k.Time),
                    property.Curves[axis].Keys.Select(k => k.Time));
            }

            Assert.Equal([1f, 0.25f, 0f], property.Curves[0].Keys.Select(k => k.Value));
            Assert.Equal([0f, 0.5f, 1f], property.Curves[1].Keys.Select(k => k.Value));
            Assert.Equal([0f, 0.75f, 0.5f], property.Curves[2].Keys.Select(k => k.Value));
        }

        [Fact]
        public void ColorTracksAreNotMistakenForBooleans()
        {
            AnimProperty property = Assert.Single(
                Assert.Single(Assert.Single(BuildSequence().ReadAnimations()).Tracks).Properties);

            Assert.False(property.IsBoolean);
            Assert.Equal("BSEffectShaderPropertyColorController", property.ControllerType);
            Assert.Equal("BSEffectShaderProperty", property.PropertyType);
        }

        [Fact]
        public void ColorControllersOnANodeAreReadToo()
        {
            (NifModel model, NifItem node) = BuildScene();

            NifItem controller = model.InsertBlock("BSEffectShaderPropertyColorController");
            model.SetRef(controller, "Interpolator", BuildInterpolator(model));
            model.SetRef(node, "Controller", controller);

            // Identified by the interpolator, not the class: material, lighting
            // shader and effect shader controllers share nothing else.
            AnimProperty property = Assert.Single(
                Assert.Single(Assert.Single(model.ReadAnimations()).Tracks).Properties);

            Assert.True(property.IsColor);
            Assert.Equal(NifAnimAccess.DefaultSequenceName, Assert.Single(model.ReadAnimations()).Name);
        }

        // --- exporting ---------------------------------------------------------

        private static FbxScene Export(NifModel model) => new(new NifToFbx(model).Convert());

        [Fact]
        public void ColorsAreDeclaredAsColorProperties()
        {
            FbxScene scene = Export(BuildSequence());

            FbxObject bone = scene.OfClass("Model").First(o => o.Name == "Bone");

            FbxProperty70 property = Assert.Single(
                bone.Properties.All, p => p.Name.Contains("ColorController", StringComparison.Ordinal));

            // The declared type is the only record of it: an FBX curve carries floats
            // whatever the property means, and a colour read back as a scalar would
            // lose two of its three channels.
            Assert.Equal("ColorRGB", property.Type);
            Assert.Equal(3, property.Values.Count);
        }

        [Fact]
        public void ColorsAreDrivenByThreeCurvesAddressedByAxis()
        {
            FbxScene scene = Export(BuildSequence());

            FbxObject node = scene.OfClass("AnimationCurveNode")
                .First(o => o.Name.Contains("ColorController", StringComparison.Ordinal));

            var channels = scene.Connections
                .Where(c => c.Kind == FbxConnectionKind.ObjectProperty && c.DestinationId == node.Id)
                .Select(c => c.PropertyName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            // Addressed by axis as a translation is, not by the property's own name
            // as a scalar is -- that is what says how many curves to expect.
            Assert.Equal(["d|X", "d|Y", "d|Z"], channels);
        }

        // --- round trip --------------------------------------------------------

        private static NifModel RoundTrip(NifModel source)
        {
            FbxDocument document = new NifToFbx(source).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            return new FbxToNif(
                new FbxScene(FbxDocument.Load(stream)),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true }).Convert(Db);
        }

        [Fact]
        public void ColorTracksComeBackAsPointInterpolators()
        {
            NifModel model = RoundTrip(BuildSequence());

            // Written as floats the colour would be one channel; as a transform it
            // would move the node instead of tinting it.
            Assert.Contains(model.Blocks, b => b.Name == "NiPoint3Interpolator");
            Assert.Contains(model.Blocks, b => b.Name == "NiPosData");
        }

        [Fact]
        public void ColorKeysKeepTheirTimesAndChannels()
        {
            NifModel model = RoundTrip(BuildSequence());

            NifItem data = model.Blocks.First(b => b.Name == "NiPosData");
            var keys = model.FindItem(data, @"Data\Keys")!.Children;

            Assert.Equal(Keys.Length, keys.Count);

            for (int i = 0; i < Keys.Length; i++)
            {
                Assert.Equal(Keys[i].Time, model.FindItem(keys[i], "Time")!.Value.ToFloat(), 4);

                NifVector3 value = model.FindItem(keys[i], "Value")!.Value.Get<NifVector3>();

                Assert.Equal(Keys[i].Value.X, value.X, 4);
                Assert.Equal(Keys[i].Value.Y, value.Y, 4);
                Assert.Equal(Keys[i].Value.Z, value.Z, 4);
            }
        }

        [Fact]
        public void ColorTracksKeepTheirIdentity()
        {
            AnimProperty before = BuildSequence().ReadAnimations()[0].Tracks[0].Properties[0];
            AnimProperty after = RoundTrip(BuildSequence()).ReadAnimations()[0].Tracks[0].Properties[0];

            Assert.Equal(before.ControllerType, after.ControllerType);
            Assert.Equal(before.PropertyType, after.PropertyType);
            Assert.True(after.IsColor);
        }

        [Fact]
        public void ColorTracksDoNotMoveTheNode()
        {
            NifModel model = RoundTrip(BuildSequence());

            NifItem controller = model.Blocks.First(b => b.Name == "NiMultiTargetTransformController");

            // Only nodes whose transform moves belong in the target list. A node
            // whose colour is animated would otherwise be driven to nothing.
            Assert.Equal(0u, model.GetUInt(controller, "Num Extra Targets"));
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            NifModel model = RoundTrip(BuildSequence());

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.True(reloaded.ReadAnimations()[0].Tracks[0].Properties[0].IsColor);
        }
    }
}
