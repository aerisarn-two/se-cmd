using LeanMeshIO.Formats.Fbx;
using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Writing animation into an FBX scene.
    /// </summary>
    /// <remarks>
    /// The binding between a track and the node it moves is made of connections,
    /// not containment, and a missing one loses the animation without losing
    /// anything visible: the stacks, layers and curves are all still in the file.
    /// Most of what follows is therefore about the edges.
    /// </remarks>
    public class AnimationExportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        private static (FbxScene Scene, List<string> Warnings) Export(string name = "TestNifFile_Animated_LE.nif")
        {
            NifModel model = Load(name);
            var converter = new NifToFbx(model);
            FbxDocument document = converter.Convert();

            return (new FbxScene(document), converter.Warnings);
        }

        [Fact]
        public void EverySequenceBecomesAStack()
        {
            (FbxScene scene, _) = Export();

            Assert.Equal(
                ["mBegin", "mLoop", "mEnd"],
                scene.OfClass("AnimationStack").Select(o => o.Name));
        }

        [Fact]
        public void EveryStackHasALayer()
        {
            (FbxScene scene, _) = Export();

            foreach (FbxObject stack in scene.OfClass("AnimationStack"))
            {
                FbxObject layer = Assert.Single(scene.ChildrenOf(stack.Id));

                Assert.Equal("AnimationLayer", layer.Class);
                Assert.Equal(FbxAnimWriter.LayerName, layer.Name);
            }
        }

        [Fact]
        public void NothingIsLeftUnbound()
        {
            (FbxScene scene, List<string> warnings) = Export();

            Assert.Empty(warnings);

            // Every curve node must reach a model, and every curve a curve node.
            // An unbound one is animation the importer will never apply.
            foreach (FbxObject node in scene.OfClass("AnimationCurveNode"))
            {
                Assert.Contains(scene.Connections, c =>
                    c.Kind == FbxConnectionKind.ObjectProperty && c.SourceId == node.Id);
            }

            foreach (FbxObject curve in scene.OfClass("AnimationCurve"))
            {
                Assert.Contains(scene.Connections, c =>
                    c.Kind == FbxConnectionKind.ObjectProperty && c.SourceId == curve.Id);
            }
        }

        [Fact]
        public void CurveNodesDriveTheRightPropertyOfTheRightModel()
        {
            (FbxScene scene, _) = Export();

            var bound = new List<(string Model, string Property)>();

            foreach (FbxObject node in scene.OfClass("AnimationCurveNode"))
            {
                foreach (FbxConnection c in scene.Connections.Where(
                    c => c.Kind == FbxConnectionKind.ObjectProperty && c.SourceId == node.Id))
                {
                    Assert.NotNull(scene[c.DestinationId]);
                    bound.Add((scene[c.DestinationId]!.Name, c.PropertyName));
                }
            }

            // Low02 is the only node whose transform moves; the rest of the fixture
            // animates shader and emitter properties instead.
            Assert.Contains(("Low02", "Lcl Rotation"), bound);
            Assert.Contains(("Low02", "Lcl Translation"), bound);
            Assert.DoesNotContain(("Low02", "Lcl Scaling"), bound);

            Assert.All(
                bound.Where(b => b.Property.StartsWith("Lcl ", StringComparison.Ordinal)),
                b => Assert.Equal("Low02", b.Model));

            // The particle system's emitter is switched on and off by name.
            Assert.Contains(bound, b => b.Model == "PCloud06"
                && b.Property.Contains("EmitterActive", StringComparison.Ordinal));
        }

        [Fact]
        public void CurvesAreBoundToASingleAxis()
        {
            (FbxScene scene, _) = Export();

            foreach (FbxObject curve in scene.OfClass("AnimationCurve"))
            {
                FbxConnection c = Assert.Single(
                    scene.Connections,
                    c => c.Kind == FbxConnectionKind.ObjectProperty && c.SourceId == curve.Id);

                FbxObject? node = scene[c.DestinationId];

                Assert.Equal("AnimationCurveNode", node?.Class);

                // A curve names its channel with a "d|" prefix, whether that is one
                // axis of a vector or the whole of a scalar property. Anything else
                // is read as driving nothing.
                Assert.StartsWith("d|", c.PropertyName);
                Assert.True(node!.Properties.Contains(c.PropertyName),
                    $"curve node has no channel {c.PropertyName}");
            }
        }

        [Fact]
        public void KeysSurviveWithTheirTimesAndValues()
        {
            NifModel model = Load("TestNifFile_Animated_LE.nif");

            AnimSequence source = model.ReadAnimations().First(s => s.Name == "mBegin");
            AnimCurve expected = source.Tracks[0].Translation[0];

            (FbxScene scene, _) = Export();

            // The X translation curve: found by following the edges back, since that
            // is the only thing that identifies which component a curve is.
            FbxObject curve = scene.OfClass("AnimationCurve").First(o =>
                scene.Connections.Any(c => c.Kind == FbxConnectionKind.ObjectProperty
                    && c.SourceId == o.Id
                    && c.PropertyName == "d|X"
                    && scene[c.DestinationId]?.Name == "T"));

            var times = (long[])curve.Child("KeyTime")!.Properties[0]!;
            var values = (float[])curve.Child("KeyValueFloat")!.Properties[0]!;

            Assert.Equal(expected.Keys.Count, times.Length);

            for (int i = 0; i < times.Length; i++)
            {
                Assert.Equal(expected.Keys[i].Time, FbxAnimWriter.FromFbxTime(times[i]), 4);
                Assert.Equal(expected.Keys[i].Value, values[i], 4);
            }
        }

        [Fact]
        public void StackSpansTheSequence()
        {
            NifModel model = Load("TestNifFile_Animated_LE.nif");
            var sequences = model.ReadAnimations().ToDictionary(s => s.Name, StringComparer.Ordinal);

            (FbxScene scene, _) = Export();

            foreach (FbxObject stack in scene.OfClass("AnimationStack"))
            {
                AnimSequence sequence = sequences[stack.Name];

                long stop = System.Convert.ToInt64(stack.Properties.ValuesOf("LocalStop")[0]);

                // A stack whose span does not cover its keys plays a fraction of the
                // animation and then stops.
                Assert.Equal(sequence.Stop, FbxAnimWriter.FromFbxTime(stop), 4);
                Assert.True(stop > 0);
            }
        }

        [Fact]
        public void InterpolationIsRunLengthEncoded()
        {
            (FbxScene scene, _) = Export();

            foreach (FbxObject curve in scene.OfClass("AnimationCurve"))
            {
                var flags = (int[])curve.Child("KeyAttrFlags")!.Properties[0]!;
                var refCounts = (int[])curve.Child("KeyAttrRefCount")!.Properties[0]!;
                var times = (long[])curve.Child("KeyTime")!.Properties[0]!;
                var data = (float[])curve.Child("KeyAttrDataFloat")!.Properties[0]!;

                // The attribute arrays are run-length encoded against the keys, and
                // an importer reading past the end of them takes whatever follows.
                Assert.Equal(flags.Length, refCounts.Length);
                Assert.Equal(flags.Length * 4, data.Length);
                Assert.Equal(times.Length, refCounts.Sum());
            }
        }

        [Fact]
        public void AnimationCanBeTurnedOff()
        {
            NifModel model = Load("TestNifFile_Animated_LE.nif");

            FbxDocument document = new NifToFbx(model, new NifToFbxOptions { ExportAnimation = false })
                .Convert();

            var scene = new FbxScene(document);

            Assert.Empty(scene.OfClass("AnimationStack"));
            Assert.Empty(scene.OfClass("AnimationCurve"));
        }

        [Fact]
        public void AnimationSurvivesBeingWrittenAndReadBack()
        {
            (FbxScene before, _) = Export();
            before.Flush();

            using var stream = new MemoryStream();
            before.Document.Save(stream);
            stream.Position = 0;

            var after = new FbxScene(FbxDocument.Load(stream));

            // Key times are 64-bit and values 32-bit floats, in typed arrays. If the
            // writer could not carry those the animation would only exist in memory.
            Assert.Equal(
                before.OfClass("AnimationStack").Select(o => o.Name),
                after.OfClass("AnimationStack").Select(o => o.Name));

            Assert.Equal(
                before.OfClass("AnimationCurve").Count(),
                after.OfClass("AnimationCurve").Count());

            FbxObject curve = after.OfClass("AnimationCurve").First();
            Assert.IsType<long[]>(curve.Child("KeyTime")!.Properties[0]);
            Assert.IsType<float[]>(curve.Child("KeyValueFloat")!.Properties[0]);
        }

        [Fact]
        public void UnanimatedFilesGetNoStacks()
        {
            (FbxScene scene, List<string> warnings) = Export("TestNifFile_Static_SE.nif");

            Assert.Empty(scene.OfClass("AnimationStack"));
            Assert.Empty(warnings);
        }
    }
}
