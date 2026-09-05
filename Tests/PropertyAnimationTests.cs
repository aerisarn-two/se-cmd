using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Animation of named scalars rather than of transforms.
    /// </summary>
    /// <remarks>
    /// A NIF needs four strings to say which controller on which sub-object a track
    /// drives — an emitter's birth rate is not an emitter's on/off switch, and
    /// neither is a shader's alpha. FBX has one way to express all of them, a
    /// custom property on the model, so those strings ride through the property's
    /// name. Most of what follows is about whether they survive.
    /// </remarks>
    public class PropertyAnimationTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        private static List<AnimSequence> Animated() =>
            Load("TestNifFile_Animated_LE.nif").ReadAnimations();

        private static IEnumerable<AnimProperty> PropertiesOf(IEnumerable<AnimSequence> sequences) =>
            sequences.SelectMany(s => s.Tracks).SelectMany(t => t.Properties);

        // --- names -------------------------------------------------------------

        [Theory]
        [InlineData("NiPSysEmitterCtlr", "NiPSysCylinderEmitter:0", "BirthRate", "")]
        [InlineData("BSEffectShaderPropertyFloatController", "5", "", "BSEffectShaderProperty")]
        [InlineData("NiFloatExtraDataController", "hkSomething", "", "")]
        [InlineData("NiPSysUpdateCtlr", "", "", "")]
        public void IdentitySurvivesTheNameItRidesIn(string type, string id, string interp, string property)
        {
            string name = AnimProperty.ToPropertyName(type, id, interp, property);

            Assert.Equal((type, id, interp, property), AnimProperty.FromPropertyName(name));
        }

        [Fact]
        public void VisibilityKeepsItsPlainName()
        {
            // The one case worth spelling out: Visibility is a standard FBX property,
            // so a DCC tool given it actually hides the object. An encoded name would
            // be a number nobody reads.
            Assert.Equal(
                AnimProperty.VisibilityName,
                AnimProperty.ToPropertyName(AnimProperty.VisibilityController, "", "", ""));

            Assert.Equal(
                (AnimProperty.VisibilityController, "", "", ""),
                AnimProperty.FromPropertyName(AnimProperty.VisibilityName));
        }

        // --- reading -----------------------------------------------------------

        [Fact]
        public void ReadsPropertyTracksAlongsideTransforms()
        {
            AnimSequence sequence = Animated().First(s => s.Name == "mBegin");

            // The fixture drives a shader property on two glow meshes and both an
            // emitter rate and its on/off switch on the particle system.
            Assert.Equal(
                ["Low02", "GlowCenter:0", "Glow:0", "PCloud06"],
                sequence.Tracks.Select(t => t.NodeName));

            AnimTrack particles = sequence.Tracks.First(t => t.NodeName == "PCloud06");

            Assert.Equal(
                ["BirthRate", "EmitterActive"],
                particles.Properties.Select(p => p.InterpolatorId));
        }

        [Fact]
        public void OneNodeGetsOneTrackHoweverManyBlocksNameIt()
        {
            foreach (AnimSequence sequence in Animated())
            {
                // The particle system is named by two controlled blocks. Two tracks
                // for one node would bind twice to the same model on export.
                Assert.Equal(
                    sequence.Tracks.Select(t => t.NodeName).Distinct(),
                    sequence.Tracks.Select(t => t.NodeName));
            }
        }

        [Fact]
        public void BooleanTracksAreDistinguishedFromFloatOnes()
        {
            AnimTrack particles = Animated().First(s => s.Name == "mBegin")
                .Tracks.First(t => t.NodeName == "PCloud06");

            AnimProperty active = particles.Properties.First(p => p.InterpolatorId == "EmitterActive");
            AnimProperty rate = particles.Properties.First(p => p.InterpolatorId == "BirthRate");

            // Losing this turns a switch into a rate: the two are stored differently
            // and read back at different widths.
            Assert.True(active.IsBoolean);
            Assert.False(rate.IsBoolean);

            Assert.All(active.Curve.Keys, k => Assert.Contains(k.Value, new[] { 0f, 1f }));
        }

        [Fact]
        public void TransformTracksAreUnaffected()
        {
            AnimTrack track = Animated().First(s => s.Name == "mBegin")
                .Tracks.First(t => t.NodeName == "Low02");

            Assert.Empty(track.Properties);
            Assert.Equal([1, 3, 1], track.Rotation.Select(c => c.Keys.Count));
        }

        // --- exporting ---------------------------------------------------------

        [Fact]
        public void PropertiesAreDeclaredOnTheModelTheyDrive()
        {
            var scene = new FbxScene(new NifToFbx(Load("TestNifFile_Animated_LE.nif")).Convert());

            foreach (FbxObject node in scene.OfClass("AnimationCurveNode"))
            {
                foreach (FbxConnection c in scene.Connections.Where(
                    c => c.Kind == FbxConnectionKind.ObjectProperty && c.SourceId == node.Id))
                {
                    FbxObject model = scene[c.DestinationId]!;

                    if (c.PropertyName.StartsWith("Lcl ", StringComparison.Ordinal))
                        continue;

                    // A curve bound to a property the model does not have is dropped
                    // by most importers, silently, because there is nothing to drive.
                    Assert.True(model.Properties.Contains(c.PropertyName),
                        $"{model.Name} has no property {c.PropertyName}");
                }
            }
        }

        [Fact]
        public void BooleanPropertiesAreDeclaredAsBooleans()
        {
            var scene = new FbxScene(new NifToFbx(Load("TestNifFile_Animated_LE.nif")).Convert());

            FbxObject particles = scene.OfClass("Model").First(o => o.Name == "PCloud06");

            string active = particles.Properties.All
                .Select(p => p.Name)
                .First(n => n.Contains("EmitterActive", StringComparison.Ordinal));

            // The declared type is the only record of it: an FBX curve carries floats
            // whatever the property means.
            Assert.Equal("bool", particles.Properties.Find(active)!.Value.Type);

            string rate = particles.Properties.All
                .Select(p => p.Name)
                .First(n => n.Contains("BirthRate", StringComparison.Ordinal));

            Assert.Equal("Number", particles.Properties.Find(rate)!.Value.Type);
        }

        // --- round trip --------------------------------------------------------

        private static NifModel RoundTrip()
        {
            FbxDocument document = new NifToFbx(Load("TestNifFile_Animated_LE.nif")).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            return new FbxToNif(
                new FbxScene(FbxDocument.Load(stream)),
                new FbxToNifOptions { RootName = "animated", LegendaryEdition = true }).Convert(Db);
        }

        [Fact]
        public void EveryPropertyTrackComesBackWithItsIdentity()
        {
            var before = PropertiesOf(Animated())
                .Select(p => (p.ControllerType, p.ControllerId, p.InterpolatorId, p.PropertyType, p.IsBoolean))
                .ToList();

            var after = PropertiesOf(RoundTrip().ReadAnimations())
                .Select(p => (p.ControllerType, p.ControllerId, p.InterpolatorId, p.PropertyType, p.IsBoolean))
                .ToList();

            Assert.NotEmpty(before);
            Assert.Equal(before, after);
        }

        [Fact]
        public void PropertyKeysKeepTheirTimesAndValues()
        {
            var before = PropertiesOf(Animated()).ToList();
            var after = PropertiesOf(RoundTrip().ReadAnimations()).ToList();

            Assert.Equal(before.Count, after.Count);

            for (int i = 0; i < before.Count; i++)
            {
                Assert.Equal(before[i].Curve.Keys.Count, after[i].Curve.Keys.Count);

                for (int k = 0; k < before[i].Curve.Keys.Count; k++)
                {
                    Assert.Equal(before[i].Curve.Keys[k].Time, after[i].Curve.Keys[k].Time, 3);
                    Assert.Equal(before[i].Curve.Keys[k].Value, after[i].Curve.Keys[k].Value, 3);
                }
            }
        }

        [Fact]
        public void BooleanTracksAreRebuiltAsBooleanBlocks()
        {
            NifModel model = RoundTrip();

            // Written as floats, the engine would read four bytes per key where it
            // expects one, and every key after the first would be wrong.
            Assert.Contains(model.Blocks, b => b.Name == "NiBoolData");
            Assert.Contains(model.Blocks, b => b.Name == "NiBoolInterpolator");
            Assert.Contains(model.Blocks, b => b.Name == "NiFloatData");
            Assert.Contains(model.Blocks, b => b.Name == "NiFloatInterpolator");
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            NifModel model = RoundTrip();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Equal(
                PropertiesOf(model.ReadAnimations()).Select(p => p.Name),
                PropertiesOf(reloaded.ReadAnimations()).Select(p => p.Name));
        }

        // --- controllers attached straight to a node ---------------------------

        /// <summary>A node carrying one property controller and nothing else.</summary>
        private static NifModel BuildStandalone(string controllerType, string extraDataName)
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            bool boolean = controllerType == AnimProperty.VisibilityController;

            NifItem data = model.InsertBlock(boolean ? "NiBoolData" : "NiFloatData");

            model.FindItem(data, @"Data\Num Keys")!.Value.SetCount(2);
            data.InvalidateConditionsRecursive();
            model.FindItem(data, @"Data\Interpolation")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Data\Keys")!;
            model.UpdateArraySize(keys);

            for (int i = 0; i < 2; i++)
            {
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat(i);
                model.FindItem(keys.Children[i], "Value")!.Value.SetFloat(i);
            }

            NifItem interpolator = model.InsertBlock(
                boolean ? "NiBoolInterpolator" : "NiFloatInterpolator");

            model.SetRef(interpolator, "Data", data);

            NifItem controller = model.InsertBlock(controllerType);
            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", root);

            if (extraDataName.Length > 0)
                model.SetString(controller, "Extra Data Name", extraDataName);

            model.SetRef(root, "Controller", controller);
            return model;
        }

        [Fact]
        public void ControllerOnANodeBecomesItsOwnSequence()
        {
            NifModel model = BuildStandalone("NiFloatExtraDataController", "hkThing");

            AnimSequence sequence = Assert.Single(model.ReadAnimations());

            // FBX has no equivalent of a controller that plays for as long as the
            // model is loaded, so it is gathered into the stack FBXWrangler invents
            // for the same reason.
            Assert.Equal(NifAnimAccess.DefaultSequenceName, sequence.Name);

            AnimProperty property = Assert.Single(Assert.Single(sequence.Tracks).Properties);

            Assert.Equal("NiFloatExtraDataController", property.ControllerType);
            Assert.Equal("hkThing", property.ControllerId);
            Assert.Equal(2, property.Curve.Keys.Count);
        }

        [Fact]
        public void VisibilityControllerBecomesTheVisibilityProperty()
        {
            NifModel model = BuildStandalone(AnimProperty.VisibilityController, string.Empty);

            AnimProperty property = Assert.Single(
                Assert.Single(Assert.Single(model.ReadAnimations()).Tracks).Properties);

            Assert.Equal(AnimProperty.VisibilityName, property.Name);
            Assert.True(property.IsBoolean);
        }

        [Fact]
        public void ControllersASequenceAlreadyDrivesAreNotReadTwice()
        {
            // Bethesda's animated effects attach a controller to its target *and*
            // name it from every sequence. Reading both would play it twice.
            var sequences = Animated();

            Assert.DoesNotContain(sequences, s => s.Name == NifAnimAccess.DefaultSequenceName);
        }
    }
}
