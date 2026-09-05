using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The two kinds of particle reference the corpus fixture does not have.
    /// </summary>
    /// <remarks>
    /// A mesh emitter births particles off named scene nodes rather than from a
    /// volume, and a collider manager holds a chain of collider blocks. Both are in
    /// the spec's table of links out of the stack
    /// (`docs/nif-particle-spec.md` §3.1); neither appears in `TestNifFile_Animated_LE`,
    /// which uses a cylinder emitter and no colliders at all.
    /// </remarks>
    public class ParticleLinkTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>A particle system with the modifiers a test asks for.</summary>
        private static NifModel Build(params string[] modifierTypes)
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem system = model.InsertBlock("NiParticleSystem");
            model.SetString(system, "Name", "Cloud");

            NifItem data = model.InsertBlock("NiPSysData");
            model.SetRef(system, "Data", data);

            // Two nodes for the emitter to birth from, and one for a collider.
            var children = new List<NifItem> { system };

            foreach (string name in new[] { "EmitterA", "EmitterB", "Wall" })
            {
                NifItem node = model.InsertBlock("NiNode");
                model.SetString(node, "Name", name);
                children.Add(node);
            }

            if (model.SetArraySize(root, "Num Children", "Children", children.Count) is { } list)
            {
                for (int i = 0; i < children.Count; i++)
                    list.Children[i].Value.SetLink(model.IndexOf(children[i]));
            }

            var modifiers = new List<NifItem>();

            foreach (string type in modifierTypes)
            {
                NifItem modifier = model.InsertBlock(type);
                model.SetString(modifier, "Name", $"{type}:0");
                model.SetRef(modifier, "Target", system);
                modifiers.Add(modifier);
            }

            if (model.SetArraySize(system, "Num Modifiers", "Modifiers", modifiers.Count) is { } array)
            {
                for (int i = 0; i < modifiers.Count; i++)
                    array.Children[i].Value.SetLink(model.IndexOf(modifiers[i]));
            }

            return model;
        }

        private static NifItem NodeNamed(NifModel model, string name) =>
            model.Blocks.First(b => model.GetName(b) == name);

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

        // --- mesh emitters ------------------------------------------------------

        /// <summary>A system whose emitter births particles off two named nodes.</summary>
        private static NifModel BuildMeshEmitter()
        {
            NifModel model = Build("NiPSysMeshEmitter");

            NifItem emitter = model.Blocks.First(b => b.Name == "NiPSysMeshEmitter");

            if (model.SetArraySize(emitter, "Num Emitter Meshes", "Emitter Meshes", 2) is { } meshes)
            {
                meshes.Children[0].Value.SetLink(model.IndexOf(NodeNamed(model, "EmitterA")));
                meshes.Children[1].Value.SetLink(model.IndexOf(NodeNamed(model, "EmitterB")));
            }

            model.FindItem(emitter, "Speed")!.Value.SetFloat(12.5f);
            return model;
        }

        [Fact]
        public void EveryEmitterMeshIsNamed()
        {
            var scene = new FbxScene(new NifToFbx(BuildMeshEmitter()).Convert());

            FbxObject emitter = scene.OfClass("Model").Single(FbxParticleWriter.IsModifierNode);

            // An array of links, so each element is named separately -- the count
            // alone would rebuild an emitter that births from nowhere.
            Assert.Equal("EmitterA", emitter.Properties.GetString($"emitter_meshes_0{FbxParticleWriter.LinkSuffix}"));
            Assert.Equal("EmitterB", emitter.Properties.GetString($"emitter_meshes_1{FbxParticleWriter.LinkSuffix}"));
        }

        [Fact]
        public void EmitterMeshesComeBackInOrder()
        {
            (NifModel model, List<string> warnings) = RoundTrip(BuildMeshEmitter());

            NifItem emitter = model.Blocks.First(b => b.Name == "NiPSysMeshEmitter");

            Assert.Equal(2u, model.GetUInt(emitter, "Num Emitter Meshes"));

            // The order matters as much as the membership: the emission walks them.
            Assert.Equal(
                ["EmitterA", "EmitterB"],
                model.GetRefArray(emitter, "Emitter Meshes").Select(model.GetName));

            Assert.Empty(warnings);
        }

        [Fact]
        public void MeshEmitterSettingsSurviveToo()
        {
            (NifModel model, _) = RoundTrip(BuildMeshEmitter());

            NifItem emitter = model.Blocks.First(b => b.Name == "NiPSysMeshEmitter");

            Assert.Equal(12.5f, model.FindItem(emitter, "Speed")!.Value.ToFloat(), 4);
        }

        // --- collider chains ----------------------------------------------------

        /// <summary>A collider manager holding a two-collider chain.</summary>
        private static NifModel BuildColliders()
        {
            NifModel model = Build("NiPSysColliderManager", "NiPSysSpawnModifier");

            NifItem manager = model.Blocks.First(b => b.Name == "NiPSysColliderManager");
            NifItem spawn = model.Blocks.First(b => b.Name == "NiPSysSpawnModifier");

            NifItem plane = model.InsertBlock("NiPSysPlanarCollider");
            model.FindItem(plane, "Width")!.Value.SetFloat(3f);
            model.FindItem(plane, "Height")!.Value.SetFloat(4f);
            model.FindItem(plane, "X Axis")!.Value.Set(new NifVector3(1f, 0f, 0f));
            model.FindItem(plane, "Bounce")!.Value.SetFloat(0.5f);
            model.FindItem(plane, "Die on Collide")!.Value.SetCount(1);

            // The two links a collider has that the tree cannot say: a node it sits
            // on, and a modifier it spawns through.
            model.SetRef(plane, "Collider Object", NodeNamed(model, "Wall"));
            model.SetRef(plane, "Spawn Modifier", spawn);

            NifItem sphere = model.InsertBlock("NiPSysSphericalCollider");
            model.FindItem(sphere, "Radius")!.Value.SetFloat(2.5f);
            model.FindItem(sphere, "Bounce")!.Value.SetFloat(0.25f);

            model.SetRef(plane, "Next Collider", sphere);
            model.SetRef(plane, "Parent", manager);
            model.SetRef(sphere, "Parent", manager);
            model.SetRef(manager, "Collider", plane);

            return model;
        }

        private static List<FbxObject> ColliderNodes(FbxScene scene)
        {
            FbxObject manager = scene.OfClass("Model").First(
                o => o.Properties.GetString(FbxParticleWriter.ModifierTypeProperty) == "NiPSysColliderManager");

            return scene.ChildrenOf(manager.Id).Where(FbxParticleWriter.IsColliderNode).ToList();
        }

        [Fact]
        public void TheChainBecomesChildrenOfItsManager()
        {
            var nodes = ColliderNodes(new FbxScene(new NifToFbx(BuildColliders()).Convert()));

            // A list is a thing a tree can show, so the chain hangs under the manager
            // in the order it is walked.
            Assert.Equal(
                ["NiPSysPlanarCollider", "NiPSysSphericalCollider"],
                nodes.Select(n => n.Properties.GetString(FbxParticleWriter.ColliderTypeProperty)));
        }

        [Fact]
        public void ChainStructureIsNotAlsoWrittenAsProperties()
        {
            var nodes = ColliderNodes(new FbxScene(new NifToFbx(BuildColliders()).Convert()));

            // Position in the chain and the manager it belongs to are what the tree
            // already says; naming them too would give two sources for one fact.
            Assert.All(nodes, n => Assert.DoesNotContain(
                n.Properties.All,
                p => p.Name.StartsWith("next_collider", StringComparison.Ordinal)
                    || p.Name.StartsWith("parent", StringComparison.Ordinal)));
        }

        [Fact]
        public void TheChainComesBackLinkedUp()
        {
            (NifModel model, List<string> warnings) = RoundTrip(BuildColliders());

            NifItem manager = model.Blocks.First(b => b.Name == "NiPSysColliderManager");
            NifItem first = model.GetRef(manager, "Collider")!;
            NifItem second = model.GetRef(first, "Next Collider")!;

            Assert.Equal("NiPSysPlanarCollider", first.Name);
            Assert.Equal("NiPSysSphericalCollider", second.Name);

            // A chain that lost its links is a set of colliders the manager never
            // reaches.
            Assert.Null(model.GetRef(second, "Next Collider"));
            Assert.Equal(manager, model.GetRef(first, "Parent"));
            Assert.Equal(manager, model.GetRef(second, "Parent"));

            Assert.Empty(warnings);
        }

        [Fact]
        public void ColliderSettingsSurvive()
        {
            (NifModel model, _) = RoundTrip(BuildColliders());

            NifItem plane = model.Blocks.First(b => b.Name == "NiPSysPlanarCollider");
            NifItem sphere = model.Blocks.First(b => b.Name == "NiPSysSphericalCollider");

            Assert.Equal(3f, model.FindItem(plane, "Width")!.Value.ToFloat(), 4);
            Assert.Equal(4f, model.FindItem(plane, "Height")!.Value.ToFloat(), 4);
            Assert.Equal(0.5f, model.FindItem(plane, "Bounce")!.Value.ToFloat(), 4);
            Assert.Equal(1u, model.GetUInt(plane, "Die on Collide"));

            NifVector3 axis = model.FindItem(plane, "X Axis")!.Value.Get<NifVector3>();
            Assert.Equal(1f, axis.X, 4);

            // The subclass fields differ between the two, so this also says each was
            // rebuilt as its own type rather than as the base class.
            Assert.Equal(2.5f, model.FindItem(sphere, "Radius")!.Value.ToFloat(), 4);
        }

        [Fact]
        public void ColliderLinksOutOfTheChainSurvive()
        {
            (NifModel model, _) = RoundTrip(BuildColliders());

            NifItem plane = model.Blocks.First(b => b.Name == "NiPSysPlanarCollider");

            // The node it collides against, resolved once the tree exists, and the
            // modifier it spawns through, resolved within the stack.
            Assert.Equal("Wall", model.GetName(model.GetRef(plane, "Collider Object")!));
            Assert.Equal("NiPSysSpawnModifier", model.GetRef(plane, "Spawn Modifier")!.Name);
        }

        [Fact]
        public void ColliderNodesDoNotBecomeBones()
        {
            (NifModel model, _) = RoundTrip(BuildColliders());

            // They stand for blocks that were rebuilt, so left to the walk they would
            // be two empty NiNodes in the tree as well.
            Assert.DoesNotContain(
                model.Blocks,
                b => b.Name == "NiNode"
                    && model.GetName(b).Contains("Collider", StringComparison.Ordinal));
        }

        [Fact]
        public void RebuiltFilesAreReadable()
        {
            foreach (NifModel source in new[] { BuildMeshEmitter(), BuildColliders() })
            {
                (NifModel model, _) = RoundTrip(source);

                using var stream = new MemoryStream();
                model.Save(stream);
                stream.Position = 0;

                NifModel reloaded = NifModel.Load(stream, Db);

                Assert.Contains(reloaded.Blocks, b => b.Name == "NiParticleSystem");
            }
        }
    }
}
