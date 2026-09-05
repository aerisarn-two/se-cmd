using LeanMeshIO;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Carrying a particle system through FBX.
    /// </summary>
    /// <remarks>
    /// There is no conversion to make here. FBX has no emitter and nothing that
    /// means what <c>NiPSysCylinderEmitter</c> means, so the choice is between
    /// losing the system and carrying it across intact. ck-cmd loses it: neither
    /// FBXWrangler nor HKXWrangler mentions particles at all.
    ///
    /// There is also no geometry: the fixture's <c>NiPSysData</c> holds
    /// <c>Vertices = 0</c> and <c>BS Max Vertices = 18</c>, a capacity for a buffer
    /// the engine fills at runtime.
    /// </remarks>
    public class ParticleTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private const string Fixture = "TestNifFile_Animated_LE.nif";

        private static NifModel Load() =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", Fixture), Db);

        private static FbxDocument? _exported;

        private static FbxDocument Export() => _exported ??= new NifToFbx(Load()).Convert();

        private static (NifModel Model, List<string> Warnings) RoundTrip()
        {
            var converter = new FbxToNif(
                new FbxScene(Export()),
                new FbxToNifOptions { RootName = "test", LegendaryEdition = true });

            return (converter.Convert(Db), converter.Warnings);
        }

        private static FbxScene Scene() => new(Export());

        private static FbxObject Node() =>
            Scene().OfClass("Model").First(o => o.Name == "PCloud06");

        /// <summary>The modifier nodes under the system, in stack order.</summary>
        private static List<FbxObject> ModifierNodes()
        {
            FbxScene scene = Scene();

            return scene
                .ChildrenOf(scene.OfClass("Model").First(o => o.Name == "PCloud06").Id)
                .Where(FbxParticleWriter.IsModifierNode)
                .ToList();
        }

        // --- exporting ---------------------------------------------------------

        [Fact]
        public void ParticleSystemsStayEmptyNodes()
        {
            var scene = new FbxScene(Export());
            FbxObject node = scene.OfClass("Model").First(o => o.Name == "PCloud06");

            // Nothing to make a mesh out of, so making one would mean inventing
            // eighteen vertices the file never had.
            Assert.DoesNotContain(scene.ChildrenOf(node.Id), o => o.Class == "Geometry");
            Assert.DoesNotContain(scene.OfClass("Geometry"), o => o.Name.Contains("PCloud", StringComparison.Ordinal));
        }

        [Fact]
        public void TheSystemIsTaggedAndCarried()
        {
            FbxObject node = Node();

            Assert.Equal("NiParticleSystem", node.Properties.GetString(FbxParticleWriter.TypeProperty));
            Assert.Equal("NiPSysData", node.Properties.GetString(FbxParticleWriter.DataTypeProperty));

            // The stack is a subtree, not a property list: eleven empties under the
            // system, in the order they run in.
            Assert.Equal(11, ModifierNodes().Count);
        }

        [Fact]
        public void NodeFieldsAreNotDuplicatedOntoTheProperties()
        {
            FbxObject node = Node();

            // The name and transform are the node's own. Carrying them twice would
            // let the two disagree after an edit, with nothing to say which won.
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}name"));
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}translation"));
            Assert.False(node.Properties.Contains($"{FbxParticleWriter.SystemPrefix}rotation"));
        }

        [Fact]
        public void LinksAreNotCarriedAsValues()
        {
            FbxObject node = Node();

            string[] prefixes = [FbxParticleWriter.SystemPrefix, FbxParticleWriter.DataPrefix];

            var fields = node.Properties.All
                .Select(p => p.Name)
                .Where(n => prefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
                .Concat(ModifierNodes().SelectMany(m => m.Properties.All.Select(p => p.Name)))
                .ToList();

            Assert.NotEmpty(fields);

            // A block index means nothing once exported, so no link is ever carried
            // as a value. What it pointed at is carried by name instead, under a
            // separate property.
            Assert.DoesNotContain(
                fields.Where(n => !n.EndsWith(FbxParticleWriter.LinkSuffix, StringComparison.Ordinal)),
                n => n.EndsWith("_data", StringComparison.Ordinal)
                    || n.EndsWith("_target", StringComparison.Ordinal)
                    || n.EndsWith("_shader_property", StringComparison.Ordinal)
                    || n.EndsWith("_gravity_object", StringComparison.Ordinal));
        }

        [Fact]
        public void LinksAreCarriedByTheNameOfWhatTheyPointedAt()
        {
            var nodes = ModifierNodes();

            var refs = nodes
                .SelectMany(m => m.Properties.All.Select(p => p.Name))
                .Where(n => n.EndsWith(FbxParticleWriter.LinkSuffix, StringComparison.Ordinal))
                .ToList();

            // Exactly the three the fixture has: an emitter's node, a gravity
            // modifier's node, and one modifier naming another. Each sits on the
            // modifier it belongs to rather than on the system.
            Assert.Equal(3, refs.Count);

            Assert.Equal(
                "PCloud06-Emitter",
                nodes[2].Properties.GetString($"emitter_object{FbxParticleWriter.LinkSuffix}"));

            Assert.Equal(
                "Gravity01",
                nodes[8].Properties.GetString($"gravity_object{FbxParticleWriter.LinkSuffix}"));

            Assert.Equal(
                "NiPSysSpawnModifier:1",
                nodes[0].Properties.GetString($"spawn_modifier{FbxParticleWriter.LinkSuffix}"));
        }

        [Fact]
        public void ModifierNodesAreNamedAndTypedIndividually()
        {
            NifModel source = Load();

            var expected = source
                .GetRefArray(source.Blocks.First(b => b.Name == "NiParticleSystem"), "Modifiers")
                .Select(m => (m.Name, source.GetString(m, "Name")))
                .ToList();

            var actual = ModifierNodes()
                .Select(n => (
                    n.Properties.GetString(FbxParticleWriter.ModifierTypeProperty),
                    n.Properties.GetString(FbxParticleWriter.ModifierNameProperty)))
                .ToList();

            // A rigger opening the outliner sees the stack by name, and each node
                // says what it is.
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ModifierFieldsAreNamedAsTheFileNamesThem()
        {
            FbxObject subTex = ModifierNodes().First(
                n => n.Properties.GetString(FbxParticleWriter.ModifierTypeProperty) == "BSPSysSubTexModifier");

            // The node is the modifier, so there is nothing to disambiguate it from:
            // frame_count rather than npsm_7_frame_count.
            Assert.True(subTex.Properties.Contains("frame_count"));
            Assert.True(subTex.Properties.Contains("start_frame"));
            Assert.DoesNotContain(subTex.Properties.All, p => p.Name.StartsWith("npsm_", StringComparison.Ordinal));
        }

        [Fact]
        public void StructuralLinksAreNotNamed()
        {
            FbxObject node = Node();

            // The system's data, its modifier list and each modifier's pointer back
            // to it all follow from the structure being rebuilt. Naming them as well
            // would give two sources for one fact.
            Assert.DoesNotContain(node.Properties.All, p =>
                p.Name.EndsWith($"_target{FbxParticleWriter.LinkSuffix}", StringComparison.Ordinal)
                || p.Name.EndsWith($"_data{FbxParticleWriter.LinkSuffix}", StringComparison.Ordinal)
                || p.Name.EndsWith($"_modifiers{FbxParticleWriter.LinkSuffix}", StringComparison.Ordinal));
        }

        // --- rebuilding --------------------------------------------------------

        [Fact]
        public void TheSystemComesBack()
        {
            (NifModel model, List<string> warnings) = RoundTrip();

            NifItem system = Assert.Single(model.Blocks, b => b.Name == "NiParticleSystem");

            Assert.Equal("PCloud06", model.GetName(system));
            Assert.Equal("NiPSysData", model.GetRef(system, "Data")?.Name);
            Assert.Empty(warnings);
        }

        [Fact]
        public void ModifiersComeBackInOrder()
        {
            NifModel before = Load();

            var expected = before.GetRefArray(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Modifiers")
                .Select(m => (m.Name, before.GetName(m)))
                .ToList();

            (NifModel after, _) = RoundTrip();

            var actual = after.GetRefArray(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Modifiers")
                .Select(m => (m.Name, after.GetName(m)))
                .ToList();

            // The order is the order they run in, so it is data rather than a way of
            // telling them apart: gravity before position before bound update.
            Assert.Equal(11, expected.Count);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void ModifiersPointBackAtTheirSystem()
        {
            (NifModel model, _) = RoundTrip();

            NifItem system = model.Blocks.First(b => b.Name == "NiParticleSystem");

            // A modifier in the array but attached to nothing is one the engine
            // holds and never runs.
            Assert.All(
                model.GetRefArray(system, "Modifiers"),
                m => Assert.Equal(system, model.GetRef(m, "Target")));
        }

        [Fact]
        public void EmitterAndGravityObjectsAreWiredBackUp()
        {
            (NifModel model, List<string> warnings) = RoundTrip();

            NifItem system = model.Blocks.First(b => b.Name == "NiParticleSystem");
            var modifiers = model.GetRefArray(system, "Modifiers").ToList();

            NifItem emitter = modifiers.First(m => m.Name == "NiPSysCylinderEmitter");
            NifItem gravity = modifiers.First(m => m.Name == "NiPSysGravityModifier");

            // An emitter that lost its emitter object emits from the origin, and a
            // gravity modifier that lost its gravity object pulls towards it. Neither
            // shows up as anything but the effect being wrong.
            Assert.Equal(
                "PCloud06-Emitter",
                model.GetName(model.GetBlock(model.FindItem(emitter, "Emitter Object")!)!));

            Assert.Equal(
                "Gravity01",
                model.GetName(model.GetBlock(model.FindItem(gravity, "Gravity Object")!)!));

            Assert.Empty(warnings);
        }

        [Fact]
        public void OneModifierCanNameAnother()
        {
            (NifModel model, _) = RoundTrip();

            NifItem system = model.Blocks.First(b => b.Name == "NiParticleSystem");
            var modifiers = model.GetRefArray(system, "Modifiers").ToList();

            NifItem ageDeath = modifiers.First(m => m.Name == "NiPSysAgeDeathModifier");
            NifItem spawn = model.GetBlock(model.FindItem(ageDeath, "Spawn Modifier")!)!;

            // Resolvable the moment the stack exists, unlike a link to a node, which
            // has to wait for the rest of the tree.
            Assert.Equal("NiPSysSpawnModifier", spawn.Name);
            Assert.Contains(spawn, modifiers);
        }

        [Fact]
        public void ALinkNamingAMissingNodeIsReported()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "root", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject node = FbxMeshWriter.AddModel(scene, "Cloud", "Null", NifTransform.Identity);
            scene.Connect(node, root);

            node.Properties.SetUserString(FbxParticleWriter.TypeProperty, "NiParticleSystem");

            FbxObject modifier = FbxMeshWriter.AddModel(scene, "grav", "Null", NifTransform.Identity);
            scene.Connect(modifier, node);

            modifier.Properties.SetUserString(FbxParticleWriter.ModifierTypeProperty, "NiPSysGravityModifier");
            modifier.Properties.SetUserString(FbxParticleWriter.ModifierNameProperty, "grav");
            modifier.Properties.SetUserString($"gravity_object{FbxParticleWriter.LinkSuffix}", "Nowhere");
            scene.Flush();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions { RootName = "test" });
            NifModel model = converter.Convert(Db);

            // Silence here would mean an effect that pulls towards the origin and no
            // way to find out why.
            Assert.Contains(converter.Warnings, w => w.Contains("Nowhere", StringComparison.Ordinal));

            NifItem gravity = model.Blocks.First(b => b.Name == "NiPSysGravityModifier");
            Assert.Null(model.GetBlock(model.FindItem(gravity, "Gravity Object")!));
        }

        [Fact]
        public void TheDataBlockKeepsItsValues()
        {
            NifModel before = Load();
            NifItem source = before.GetRef(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;

            (NifModel after, _) = RoundTrip();
            NifItem rebuilt = after.GetRef(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;

            // The vertex buffer's capacity is the whole of what the file says about
            // the particles themselves.
            Assert.Equal(before.GetUInt(source, "BS Max Vertices"), after.GetUInt(rebuilt, "BS Max Vertices"));
            Assert.Equal(18u, after.GetUInt(rebuilt, "BS Max Vertices"));

            Assert.Equal(
                before.FindItem(source, "Aspect Ratio")!.Value.ToFloat(),
                after.FindItem(rebuilt, "Aspect Ratio")!.Value.ToFloat(), 5);

            Assert.Equal(
                before.FindItem(source, "Speed to Aspect Speed 2")!.Value.ToFloat(),
                after.FindItem(rebuilt, "Speed to Aspect Speed 2")!.Value.ToFloat(), 3);
        }

        [Fact]
        public void ArraysInsideTheDataBlockComeBack()
        {
            NifModel before = Load();
            NifItem source = before.GetRef(before.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;
            var expected = before.FindItem(source, "Subtexture Offsets")!.Children;

            (NifModel after, _) = RoundTrip();
            NifItem rebuilt = after.GetRef(after.Blocks.First(b => b.Name == "NiParticleSystem"), "Data")!;
            var actual = after.FindItem(rebuilt, "Subtexture Offsets")!.Children;

            // Sized by a count that has to be written first, so this says the two
            // happened in the right order as well as that the values survived.
            Assert.Equal(16, expected.Count);
            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                NifVector4 a = expected[i].Value.Get<NifVector4>();
                NifVector4 b = actual[i].Value.Get<NifVector4>();

                Assert.Equal(a.X, b.X, 5);
                Assert.Equal(a.Y, b.Y, 5);
                Assert.Equal(a.Z, b.Z, 5);
                Assert.Equal(a.W, b.W, 5);
            }
        }

        [Fact]
        public void ModifierSettingsComeBack()
        {
            NifModel before = Load();

            NifItem emitter = before.Blocks.First(b => b.Name == "NiPSysCylinderEmitter");

            (NifModel after, _) = RoundTrip();

            NifItem rebuilt = after.Blocks.First(b => b.Name == "NiPSysCylinderEmitter");

            foreach (string field in new[] { "Radius", "Height", "Speed", "Declination", "Life Span" })
            {
                Assert.Equal(
                    before.FindItem(emitter, field)!.Value.ToFloat(),
                    after.FindItem(rebuilt, field)!.Value.ToFloat(), 4);
            }
        }

        [Fact]
        public void TheEmittersAnimationStillBinds()
        {
            (NifModel model, _) = RoundTrip();

            AnimTrack track = model.ReadAnimations()
                .First(s => s.Name == "mBegin")
                .Tracks.First(t => t.NodeName == "PCloud06");

            // The system is a node like any other as far as animation goes, and
            // rebuilding it as one had better not have broken that.
            Assert.Equal(
                ["BirthRate", "EmitterActive"],
                track.Properties.Select(p => p.InterpolatorId));
        }

        [Fact]
        public void ParticleSystemsAreNotAlsoNodes()
        {
            (NifModel model, _) = RoundTrip();

            // Emitting both would leave the system parented under a copy of itself,
            // and the name would resolve to whichever came first.
            Assert.DoesNotContain(
                model.Blocks,
                b => b.Name == "NiNode" && model.GetName(b) == "PCloud06");
        }

        [Fact]
        public void RebuiltFileIsReadable()
        {
            (NifModel model, _) = RoundTrip();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            NifItem system = Assert.Single(reloaded.Blocks, b => b.Name == "NiParticleSystem");
            Assert.Equal(11, reloaded.GetRefArray(system, "Modifiers").Count());
        }

        [Fact]
        public void UnknownBlockTypesAreReportedRatherThanTrusted()
        {
            FbxDocument document = FbxDocumentTemplate.CreateEmpty();
            var scene = new FbxScene(document);

            FbxObject root = FbxMeshWriter.AddModel(scene, "root", "Null", NifTransform.Identity);
            scene.ConnectToRoot(root);

            FbxObject node = FbxMeshWriter.AddModel(scene, "Cloud", "Null", NifTransform.Identity);
            scene.Connect(node, root);

            // The type arrives as text from outside the file, so it is not something
            // to take on trust: inserting an unknown block throws.
            node.Properties.SetUserString(FbxParticleWriter.TypeProperty, "NiNotAThing");
            scene.Flush();

            var converter = new FbxToNif(new FbxScene(document), new FbxToNifOptions { RootName = "test" });
            NifModel model = converter.Convert(Db);

            Assert.Contains(converter.Warnings, w => w.Contains("NiNotAThing", StringComparison.Ordinal));
            Assert.Contains(model.Blocks, b => b.Name == "NiNode" && model.GetName(b) == "Cloud");
        }

        [Fact]
        public void AModifierNamedByAnotherComesBackWithoutJoiningTheStack()
        {
            // A modifier can point at one that is not in the stack. A dragon crash's
            // BSPSysHavokUpdateModifier names the rotation modifier it applies to the
            // debris it throws, and that one is in nobody's Modifiers array -- so
            // walking the stack never reached it and three were lost.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem system = model.InsertBlock("NiParticleSystem");
            model.SetString(system, "Name", "PArray02");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(system));

            NifItem havok = model.InsertBlock("BSPSysHavokUpdateModifier");
            model.SetString(havok, "Name", "BSPSysHavokUpdateModifier:0");
            model.SetRef(havok, "Target", system);

            // Referenced by the one above, and in no stack.
            NifItem rotation = model.InsertBlock("NiPSysRotationModifier");
            model.SetString(rotation, "Name", "NiPSysRotationModifier:5");
            model.FindItem(rotation, "Rotation Speed")?.Value.SetFloat(2.5f);
            model.SetRef(havok, "Modifier", rotation);

            if (model.SetArraySize(system, "Num Modifiers", "Modifiers", 1) is { } mods)
                mods.Children[0].Value.SetLink(model.IndexOf(havok));

            model.SetRoots([root]);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            NifModel rebuilt = new FbxToNif(
                scene,
                new FbxToNifOptions
                {
                    RootName = "root", Version = model.Version, UserVersion = model.UserVersion
                }).Convert(Db);

            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPSysRotationModifier");

            Assert.Equal(2.5f, rebuilt.FindItem(back, "Rotation Speed")!.Value.ToFloat(), 3);

            // Named by the modifier that wanted it...
            NifItem rebuiltHavok = Assert.Single(
                rebuilt.Blocks, b => b.Name == "BSPSysHavokUpdateModifier");

            Assert.Equal(back, rebuilt.GetRef(rebuiltHavok, "Modifier"));

            // ...and not in the stack, which would change what the system does.
            NifItem rebuiltSystem = Assert.Single(rebuilt.Blocks, b => b.Name == "NiParticleSystem");

            Assert.DoesNotContain(back, rebuilt.GetRefArray(rebuiltSystem, "Modifiers"));
        }
    }
}
