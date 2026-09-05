using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Texture flipbook controllers.
    /// </summary>
    /// <remarks>
    /// A <c>NiFlipController</c> cycles one texture slot through a list of images,
    /// animating the index with a float interpolator. FBX has layered textures and
    /// animated texture transforms but nothing that swaps one image for another over
    /// time, so this is carried rather than converted.
    ///
    /// It is also a pre-Skyrim construct — its `Delta` and `Accum Time` fields stop at
    /// 10.1.0.103, its usual host `NiTexturingProperty` is not something Skyrim uses,
    /// and nothing in the corpus has one. Everything below is therefore built here.
    /// </remarks>
    public class FlipControllerTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const uint GlowMap = 4;

        private static readonly string[] Sources =
        [
            @"textures\fx\fxflame01.dds",
            @"textures\fx\fxflame02.dds",
            @"textures\fx\fxflame03.dds"
        ];

        /// <summary>A shape whose shader property carries a flipbook controller.</summary>
        private static NifModel Build(bool withCurve = true)
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem shape = model.InsertBlock("NiTriShape");
            model.SetString(shape, "Name", "Flame");

            NifItem data = model.InsertBlock("NiTriShapeData");
            model.SetRef(shape, "Data", data);
            BuildTriangle(model, data);

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(shape));

            // A lit shader with a texture set, so the export produces a real FBX
            // material and the import rebuilds a shader property for it to hang off.
            NifItem shader = model.InsertBlock("BSLightingShaderProperty");
            model.SetRef(shape, "Shader Property", shader);

            NifItem textures = model.InsertBlock("BSShaderTextureSet");

            if (model.SetArraySize(textures, "Num Textures", "Textures", 1) is { } list)
                model.SetString(list.Children[0], string.Empty, @"textures\fx\fxflame01.dds");

            model.SetRef(shader, "Texture Set", textures);

            NifItem controller = model.InsertBlock("NiFlipController");
            model.FindItem(controller, "Texture Slot")!.Value.SetCount(GlowMap);

            if (model.SetArraySize(controller, "Num Sources", "Sources", Sources.Length) is { } array)
            {
                for (int i = 0; i < Sources.Length; i++)
                {
                    NifItem texture = model.InsertBlock("NiSourceTexture");
                    model.SetString(texture, "File Name", Sources[i]);
                    array.Children[i].Value.SetLink(model.IndexOf(texture));
                }
            }

            if (withCurve)
                model.SetRef(controller, "Interpolator", BuildInterpolator(model));

            model.SetRef(controller, "Target", shader);
            model.SetRef(shader, "Controller", controller);

            return model;
        }

        /// <summary>The smallest shape that survives export.</summary>
        private static void BuildTriangle(NifModel model, NifItem data)
        {
            model.FindItem(data, "Num Vertices")!.Value.SetCount(3);
            model.FindItem(data, "Has Vertices")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem vertices = model.FindItem(data, "Vertices")!;
            model.UpdateArraySize(vertices);

            vertices.Children[0].Value.Set(new NifVector3(0f, 0f, 0f));
            vertices.Children[1].Value.Set(new NifVector3(1f, 0f, 0f));
            vertices.Children[2].Value.Set(new NifVector3(0f, 1f, 0f));

            model.FindItem(data, "Num Triangles")!.Value.SetCount(1);
            model.FindItem(data, "Num Triangle Points")!.Value.SetCount(3);
            model.FindItem(data, "Has Triangles")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem triangles = model.FindItem(data, "Triangles")!;
            model.UpdateArraySize(triangles);
            triangles.Children[0].Value.Set(new NifTriangle(0, 1, 2));
        }

        /// <summary>A float interpolator stepping through the source indices.</summary>
        private static NifItem BuildInterpolator(NifModel model)
        {
            const uint Constant = 5;

            NifItem data = model.InsertBlock("NiFloatData");

            model.FindItem(data, @"Data\Num Keys")!.Value.SetCount(3);
            data.InvalidateConditionsRecursive();
            model.FindItem(data, @"Data\Interpolation")!.Value.SetCount(Constant);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Data\Keys")!;
            model.UpdateArraySize(keys);

            for (int i = 0; i < 3; i++)
            {
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat(i * 0.1f);
                model.FindItem(keys.Children[i], "Value")!.Value.SetFloat(i);
            }

            NifItem interpolator = model.InsertBlock("NiFloatInterpolator");
            model.SetRef(interpolator, "Data", data);
            return interpolator;
        }

        private static FbxScene Export(NifModel model) => new(new NifToFbx(model).Convert());

        private static FbxObject FlameNode(FbxScene scene) =>
            scene.OfClass("Model").First(o => o.Name.StartsWith("Flame", StringComparison.Ordinal));

        private static (NifModel Model, List<string> Warnings) RoundTrip(NifModel source)
        {
            FbxDocument document = new NifToFbx(source).Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var converter = new FbxToNif(
                new FbxScene(FbxDocument.Load(stream)),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true });

            return (converter.Convert(Db), converter.Warnings);
        }

        // --- exporting ---------------------------------------------------------

        [Fact]
        public void TheControllerIsCarriedOnTheShapesNode()
        {
            FbxObject node = FlameNode(Export(Build()));

            Assert.Equal("1", node.Properties.GetString(FbxFlipWriter.CountProperty));
            Assert.Equal("NiFlipController", node.Properties.GetString($"{FbxFlipWriter.Prefix}0_type"));

            // The slot is the whole point: a flipbook on the wrong one animates a
            // texture nobody is looking at.
            Assert.Equal(
                GlowMap.ToString(),
                node.Properties.GetString($"{FbxFlipWriter.Prefix}0_texture_slot"));
        }

        [Fact]
        public void TheImagesAreCarriedByNameAndInOrder()
        {
            FbxObject node = FlameNode(Export(Build()));

            Assert.Equal("3", node.Properties.GetString($"{FbxFlipWriter.Prefix}0_sources"));

            // The index the interpolator animates is an index into this list, so the
            // order is data and not a way of telling them apart.
            for (int i = 0; i < Sources.Length; i++)
                Assert.Equal(Sources[i], node.Properties.GetString($"{FbxFlipWriter.Prefix}0_source_{i}"));
        }

        [Fact]
        public void ShapesWithoutOneCarryNothing()
        {
            NifModel model = Build();

            // Strip the controller back off and the node should say nothing at all,
            // rather than say it has none.
            NifItem shader = model.Blocks.First(b => b.Name == "BSLightingShaderProperty");
            model.SetRef(shader, "Controller", null);

            FbxObject node = FlameNode(Export(model));

            Assert.False(node.Properties.Contains(FbxFlipWriter.CountProperty));
        }

        // --- the curve rides the existing property tracks -----------------------

        [Fact]
        public void TheAnimatedIndexBecomesAPropertyTrack()
        {
            AnimProperty property = Assert.Single(
                Assert.Single(Assert.Single(Build().ReadAnimations()).Tracks).Properties);

            // The interpolator drives a number, and a number is what the property
            // tracks already carry -- nothing about flipbooks is needed for that.
            Assert.Equal("NiFlipController", property.ControllerType);
            Assert.Equal([0f, 1f, 2f], property.Curve.Keys.Select(k => k.Value));

            // Stepping, not sliding: an index halfway between two images is not an
            // image.
            Assert.All(property.Curve.Keys, k => Assert.Equal(AnimInterpolation.Constant, k.Interpolation));
        }

        [Fact]
        public void ControllersOnPropertiesUsedToBeInvisible()
        {
            // The controller hangs off the shader property, not off the shape, and
            // only node chains were walked before. A shape with no controller of its
            // own would have reported no animation at all.
            NifModel model = Build();

            Assert.Null(model.GetRef(model.Blocks.First(b => b.Name == "NiTriShape"), "Controller"));
            Assert.NotEmpty(model.ReadAnimations());
        }

        // --- round trip --------------------------------------------------------

        [Fact]
        public void TheControllerComesBackOnTheShaderProperty()
        {
            (NifModel model, List<string> warnings) = RoundTrip(Build());

            NifItem controller = Assert.Single(model.Blocks, b => b.Name == "NiFlipController");

            NifItem shape = model.Blocks.First(b => model.GetName(b) == "Flame");
            NifItem shader = model.GetRef(shape, "Shader Property")!;

            // It changes what a property draws, not where a node is, so the property
            // is where it belongs — whichever shader block the material rebuilt, which
            // is not necessarily the class the source used.
            Assert.Equal(controller, model.GetRef(shader, "Controller"));
            Assert.Equal(shader, model.GetRef(controller, "Target"));
            Assert.Empty(warnings);
        }

        [Fact]
        public void TheSlotAndImagesSurvive()
        {
            (NifModel model, _) = RoundTrip(Build());

            NifItem controller = model.Blocks.First(b => b.Name == "NiFlipController");

            Assert.Equal(GlowMap, model.GetUInt(controller, "Texture Slot"));

            var sources = model.GetRefArray(controller, "Sources")
                .Select(t => model.GetString(t, "File Name"))
                .ToList();

            Assert.Equal(Sources, sources);
            Assert.Equal((uint)Sources.Length, model.GetUInt(controller, "Num Sources"));
        }

        [Fact]
        public void RebuiltSourcesAreExternalFiles()
        {
            (NifModel model, _) = RoundTrip(Build());

            // Every Bethesda texture is a file on disk. Left unset, the block claims
            // to hold pixel data it has not got.
            Assert.All(
                model.GetRefArray(model.Blocks.First(b => b.Name == "NiFlipController"), "Sources"),
                t => Assert.Equal(1u, model.GetUInt(t, "Use External")));
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            (NifModel model, _) = RoundTrip(Build());

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            NifItem controller = Assert.Single(reloaded.Blocks, b => b.Name == "NiFlipController");
            Assert.Equal(3, reloaded.GetRefArray(controller, "Sources").Count());
        }

        [Fact]
        public void AControllerWithoutACurveStillComesBack()
        {
            (NifModel model, _) = RoundTrip(Build(withCurve: false));

            // The slot and the images are state, not animation, and are worth keeping
            // whether or not anything is driving the index.
            NifItem controller = Assert.Single(model.Blocks, b => b.Name == "NiFlipController");

            Assert.Equal(GlowMap, model.GetUInt(controller, "Texture Slot"));
            Assert.Equal(3, model.GetRefArray(controller, "Sources").Count());
        }
    }
}
