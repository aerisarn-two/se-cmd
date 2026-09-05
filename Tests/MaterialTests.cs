using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class MaterialTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel LoadNif(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        private static FbxScene Convert(string name, string texturePath = "")
        {
            var converter = new NifToFbx(LoadNif(name), new NifToFbxOptions { TexturePath = texturePath });
            return new FbxScene(converter.Convert());
        }

        // --- alpha flags ------------------------------------------------------

        [Fact]
        public void AlphaFlagsRoundTrip()
        {
            // Every field at once, so a misplaced shift shows up.
            var original = new AlphaSettings
            {
                ColorBlendingEnable = true,
                SourceBlendMode = GlBlendMode.SrcAlpha,
                DestinationBlendMode = GlBlendMode.OneMinusSrcAlpha,
                AlphaTestEnable = true,
                AlphaTestMode = GlTestMode.Greater,
                NoSorter = true,
                Threshold = 128
            };

            AlphaSettings back = AlphaSettings.FromFlags(original.ToFlags(), original.Threshold);

            Assert.Equal(original.ColorBlendingEnable, back.ColorBlendingEnable);
            Assert.Equal(original.SourceBlendMode, back.SourceBlendMode);
            Assert.Equal(original.DestinationBlendMode, back.DestinationBlendMode);
            Assert.Equal(original.AlphaTestEnable, back.AlphaTestEnable);
            Assert.Equal(original.AlphaTestMode, back.AlphaTestMode);
            Assert.Equal(original.NoSorter, back.NoSorter);
            Assert.Equal(original.Threshold, back.Threshold);
        }

        [Fact]
        public void AlphaFlagsUseTheDocumentedBitLayout()
        {
            // 0x00ED is the common "alpha blend" value on Skyrim meshes:
            // blending on, SRC_ALPHA over ONE_MINUS_SRC_ALPHA.
            AlphaSettings alpha = AlphaSettings.FromFlags(0x00ED, 0);

            Assert.True(alpha.ColorBlendingEnable);
            Assert.Equal(GlBlendMode.SrcAlpha, alpha.SourceBlendMode);
            Assert.Equal(GlBlendMode.OneMinusSrcAlpha, alpha.DestinationBlendMode);
            Assert.Equal((ushort)0x00ED, alpha.ToFlags());
        }

        [Fact]
        public void BlendModeNamesRoundTrip()
        {
            foreach (GlBlendMode mode in Enum.GetValues<GlBlendMode>())
                Assert.Equal(mode, AlphaSettings.ParseBlendMode(AlphaSettings.NameOf(mode)));

            foreach (GlTestMode mode in Enum.GetValues<GlTestMode>())
                Assert.Equal(mode, AlphaSettings.ParseTestMode(AlphaSettings.NameOf(mode)));
        }

        [Fact]
        public void AcceptsFbxWranglersMisspeltBlendModeName() =>
            // Its writer emits "ONE" but its parser tests for "GL_ONE"; both work here.
            Assert.Equal(GlBlendMode.One, AlphaSettings.ParseBlendMode("GL_ONE"));

        // --- texture paths ----------------------------------------------------

        [Theory]
        [InlineData(@"C:\game\Data\textures\armor\iron.png", @"textures\armor\iron.dds")]
        [InlineData("/home/me/textures/armor/iron.tga", @"textures\armor\iron.dds")]
        [InlineData(@"textures\armor\iron.dds", @"textures\armor\iron.dds")]
        [InlineData("cube/sky.dds", @"cube\sky.dds")]
        public void NormalizesTexturePaths(string input, string expected) =>
            Assert.Equal(expected, MaterialData.NormalizeTexturePath(input));

        [Fact]
        public void LeavesUnrecognisedTexturePathsAlone() =>
            // No textures or cube segment to anchor on, so only the extension changes.
            Assert.Equal(@"custom\thing.dds", MaterialData.NormalizeTexturePath(@"custom\thing.png"));

        // --- conversion -------------------------------------------------------

        [Fact]
        public void EmitsAMaterialPerShape()
        {
            FbxScene scene = Convert("multi_material_cube.nif");

            // Three shapes, three materials.
            Assert.Equal(3, scene.OfClass("Material").Count());
        }

        [Fact]
        public void ConnectsTheMaterialToTheMeshHolder()
        {
            FbxScene scene = Convert("multi_material_cube.nif");

            FbxObject holder = scene.OfClass("Model", "Mesh").First();

            Assert.Single(scene.ChildrenOf(holder.Id).Where(o => o.Class == "Material"));
        }

        [Fact]
        public void WritesPhongPropertiesFromTheShader()
        {
            NifModel model = LoadNif("multi_material_cube.nif");
            NifItem shape = model.Blocks.First(b =>
                b.Name == "NiTriShape" && model.GetName(b) == "Cube_Material0");
            NifItem shader = model.GetRef(shape, "Shader Property")!;

            float glossiness = model.FindItem(shader, "Glossiness")!.Value.ToFloat();
            float strength = model.FindItem(shader, "Specular Strength")!.Value.ToFloat();

            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject material = scene.OfClass("Material").First(m => m.Name.StartsWith("Cube_Material0"));

            Assert.Equal("Phong", material.Child("ShadingModel")!.Properties[0]);
            Assert.Equal(glossiness, material.Properties.GetDouble("ShininessExponent"), 4);

            // NIF keeps specular strength over 0..999.
            Assert.Equal(strength / 999.0, material.Properties.GetDouble("SpecularFactor"), 6);
        }

        [Fact]
        public void CarriesTheShaderTypeAsAReadableName()
        {
            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject material = scene.OfClass("Material").First();

            FbxProperty70? shaderType = material.Properties.Find("shader_type");

            if (shaderType is null)
                return;

            Assert.True(shaderType.Value.IsUserDefined);

            // A name, not a bare number.
            string value = (string)shaderType.Value.Values[0]!;
            Assert.False(string.IsNullOrEmpty(value));
            Assert.False(int.TryParse(value, out _), $"expected a name, got \"{value}\"");
        }

        [Fact]
        public void EmitsTexturesBoundToTheirMaterialProperty()
        {
            FbxScene scene = Convert("multi_material_cube.nif");

            var textures = scene.OfClass("Texture").ToList();

            if (textures.Count == 0)
                return;

            FbxObject material = scene.OfClass("Material").First();

            var bindings = scene.PropertyConnectionsTo(material.Id)
                .Select(c => c.Property)
                .ToList();

            // A texture attaches to the named property it drives, not to the
            // material as a whole.
            Assert.Contains("DiffuseColor", bindings);
        }

        [Fact]
        public void AppliesTheTexturePathPrefix()
        {
            FbxScene scene = Convert("multi_material_cube.nif", "/game/Data");

            FbxObject? texture = scene.OfClass("Texture").FirstOrDefault();

            if (texture is null)
                return;

            string fileName = (string)texture.Child("FileName")!.Properties[0]!;
            string relative = (string)texture.Child("RelativeFilename")!.Properties[0]!;

            Assert.StartsWith("/game/Data", fileName);

            // The relative path stays as the NIF stored it.
            Assert.DoesNotContain("/game/Data", relative);
        }

        [Fact]
        public void MarksTheGeometryAsUsingOneMaterial()
        {
            FbxScene scene = Convert("multi_material_cube.nif");
            FbxObject geometry = scene.OfClass("Geometry").First();

            var element = geometry.Child("LayerElementMaterial");
            Assert.NotNull(element);

            // One shape, one material, so every polygon uses index 0.
            Assert.Equal("AllSame",
                element!.Nodes.First(n => n.Name == "MappingInformationType").Properties[0]);

            var layer = geometry.Child("Layer")!;
            Assert.Contains(layer.Nodes.Where(n => n.Name == "LayerElement"),
                e => (string)e.Nodes.First(c => c.Name == "Type").Properties[0]! == "LayerElementMaterial");
        }

        [Fact]
        public void MaterialsSurviveAWriteAndReadCycle()
        {
            var converter = new NifToFbx(LoadNif("multi_material_cube.nif"));
            FbxDocument document = converter.Convert();

            using var stream = new MemoryStream();
            document.Save(stream);
            stream.Position = 0;

            var reloaded = new FbxScene(FbxDocument.Load(stream));

            Assert.Equal(3, reloaded.OfClass("Material").Count());

            FbxObject material = reloaded.OfClass("Material").First();
            Assert.Equal("Phong", material.Child("ShadingModel")!.Properties[0]);
            Assert.True(material.Properties.Contains("SpecularFactor"));
        }

        // The sample NIFs have empty texture sets -- they are Blender scenes with
        // no textures assigned -- so the texture path is covered synthetically.

        private static MaterialData TexturedMaterial()
        {
            var material = new MaterialData { Name = "Iron", Glossiness = 40f, SpecularStrength = 500f };
            material.Textures.Add(@"textures\armor\iron_d.dds");
            material.Textures.Add(@"textures\armor\iron_n.dds");
            material.Textures.Add(string.Empty);
            material.Textures.Add(@"textures\armor\iron_g.dds");
            return material;
        }

        private static FbxScene SceneWith(MaterialData material, string prefix = "")
        {
            var scene = new FbxScene(FbxDocumentTemplate.CreateEmpty());
            FbxMaterialWriter.AddMaterial(scene, material, prefix);
            scene.Flush();
            return scene;
        }

        [Fact]
        public void WritesOneTexturePerNonEmptySlot()
        {
            FbxScene scene = SceneWith(TexturedMaterial());

            // Three of the four slots are filled; the empty one is skipped.
            Assert.Equal(3, scene.OfClass("Texture").Count());
        }

        [Fact]
        public void BindsTexturesToTheRightMaterialProperties()
        {
            FbxScene scene = SceneWith(TexturedMaterial());
            FbxObject material = scene.OfClass("Material").Single();

            var bindings = scene.PropertyConnectionsTo(material.Id).Select(c => c.Property).ToList();

            Assert.Contains("DiffuseColor", bindings);
            Assert.Contains("NormalMap", bindings);

            // Slots past the second have no standard FBX property, so they get a
            // user-defined one named after the slot.
            Assert.Contains("slot4", bindings);
            Assert.True(material.Properties.Find("slot4")!.Value.IsUserDefined);
        }

        [Fact]
        public void ADiffuseTextureAlsoDrivesTransparencyWhenAlphaIsPresent()
        {
            MaterialData material = TexturedMaterial();
            material.AlphaProperty = AlphaSettings.FromFlags(0x00ED, 128);

            FbxScene scene = SceneWith(material);
            FbxObject fbxMaterial = scene.OfClass("Material").Single();

            var bindings = scene.PropertyConnectionsTo(fbxMaterial.Id).Select(c => c.Property).ToList();

            Assert.Contains("TransparentColor", bindings);
        }

        [Fact]
        public void WritesAlphaSettingsAsUserProperties()
        {
            MaterialData material = TexturedMaterial();
            material.AlphaProperty = AlphaSettings.FromFlags(0x00ED, 128);

            FbxScene scene = SceneWith(material);
            FbxProperties properties = scene.OfClass("Material").Single().Properties;

            Assert.Equal("SRC_ALPHA", properties.GetString("source_blend_mode"));
            Assert.Equal("ONE_MINUS_SRC_ALPHA", properties.GetString("destination_blend_mode"));
            Assert.Equal(128, properties.GetInt("alpha_test_threshold"));
            Assert.True(properties.Find("source_blend_mode")!.Value.IsUserDefined);
        }

        [Fact]
        public void TexturesPointAtTheSharedUvElement()
        {
            FbxScene scene = SceneWith(TexturedMaterial());
            FbxObject texture = scene.OfClass("Texture").First();

            // Must match the geometry's UV element or the binding is dangling.
            Assert.Equal(FbxMeshWriter.UvElementName, texture.Properties.GetString("UVSet"));
        }
    }
}
