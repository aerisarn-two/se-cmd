using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// NIF to FBX and back, compared field by field.
    /// </summary>
    /// <remarks>
    /// The comparison walks both graphs from the root and follows references, so block
    /// order does not enter into it: what is being asked is whether the rebuilt file
    /// says the same things, not whether it says them in the same places.
    ///
    /// Byte identity is the goal and is not reached yet. What holds today, and what
    /// this pins, is that the ck-cmd example files come back with the same graph — the
    /// same blocks, of the same kinds, linked the same way — and differ only in the
    /// fields listed in <see cref="KnownGaps"/>. Each of those is either derived on
    /// import by design or a gap with a reason recorded against it.
    /// </remarks>
    public class RoundTripTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>
        /// The conversion alone, for a test that deliberately builds a model the full
        /// comparison would have plenty to say about.
        /// </summary>
        private static NifModel Rebuilt(NifModel source)
        {
            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            return new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);
        }

        private static NifModel RoundTrip(NifModel source)
        {
            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            var converter = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    // The root's name is a property of the file, and the option exists
                    // for FBX that never was a NIF. Carrying it keeps the comparison
                    // about the conversion rather than about the option.
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                });

            NifModel rebuilt = converter.Convert(Db);

            // Every round trip through this helper is compared in full, not only the
            // one field the calling test came to look at. A test that builds a shape to
            // check its radius should not be silent about the placement going missing
            // beside it -- and that is not hypothetical, it is how several of the
            // defects in RoundTripBaseline.Open survived.
            var unexplained = RoundTripBaseline.Unexplained(source, rebuilt);

            Assert.True(
                unexplained.Count == 0,
                $"the round trip differs in {unexplained.Count} field(s) that are neither "
                + $"derived nor a recorded defect:\n  "
                + string.Join("\n  ", unexplained.Take(20)));

            return rebuilt;
        }

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

        /// <summary>
        /// Fields the round trip is not expected to reproduce, and why.
        /// </summary>
        /// <remarks>
        /// A field is listed by the name it ends in, since the same gap shows up under
        /// every shape in a file. Shrinking this list is the work; it exists so that
        /// anything *not* on it fails rather than being absorbed into a count.
        /// </remarks>
        public static readonly Dictionary<string, string> KnownGaps = new(StringComparer.Ordinal)
        {
            // Derived on import by design. The size of a collision shape comes back
            // from its tessellated geometry, which is the half of the shape a DCC tool
            // can edit; carrying the original would ignore whatever was done to it.
            ["Radius"] = "refitted from the tessellated collision geometry",
            ["Dimensions"] = "refitted from the tessellated collision geometry",


            // Deliberately dropped, not lost. These bodies carry a mass on a layer
            // their own filter calls SKYL_STATIC, and a static with a mass is treated
            // as movable -- which is how scenery ends up falling through the world. So
            // the static profile zeroes both, as ck-cmd's does, and the source file
            // disagrees with itself rather than with the importer.
            ["Mass"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m11"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m22"] = "zeroed by the static motion profile, as ck-cmd does",
            ["m33"] = "zeroed by the static motion profile, as ck-cmd does",

            // 0xCD in every byte is the debug heap's fill pattern: these are fields the
            // exporter that wrote the fixture never initialised. There is nothing to
            // reproduce.
            ["Auto Remove Level"] = "uninitialised in the source file (0xCD)",
            ["Response Modifier Flags"] = "uninitialised in the source file (0xCD)",
            ["Num Shape Keys in Contact Point"] = "uninitialised in the source file (0xCD)",
            ["Force Collided Onto PPU"] = "uninitialised in the source file (0xCD)",

            // Real gaps, each its own piece of work.
            ["Consistency Flags"] = "not carried",
            ["Shader Flags 2"] = "one flag differs; the shader flag words are not carried verbatim",
            ["Bounding Sphere"] = "recomputed rather than carried",
            ["Center"] = "recomputed rather than carried",
            // The hull is refitted, so its vertices and planes come back in the order
            // the fit produced rather than the order Havok emitted. Only the *order*
            // is excused: that the corners themselves all come back is asserted by
            // AConvexHullKeepsEveryCorner below, and the plane convention is checked
            // against a shipped hull in ConvexHullPlaneTests.
            ["Vertices"] = "convex hull refitted from the tessellation, so the order differs",
            ["Normals"] = "convex hull refitted from the tessellation, so the order differs",
        };

        public static TheoryData<string> CkCmdExamples() =>
            new("generate_rb.nif", "generate_rb_box.nif", "generate_rb_sphere.nif", "multi_material_cube.nif");

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void TheGraphSurvivesTheRoundTrip(string name)
        {
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            // Same blocks, of the same kinds. The comparison below follows references
            // and would miss a block that nothing points at.
            Assert.Equal(
                source.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal),
                rebuilt.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal));
        }

        /// <summary>Every fixture NIF, whatever it was put there to exercise.</summary>
        /// <remarks>
        /// Found rather than listed, so a fixture added for one reason is compared for
        /// every reason — which is the point: the four this used to check were the four
        /// someone thought to name.
        ///
        /// <see cref="FixtureFiles.IsFixture"/> decides what counts, so the meshes
        /// extracted from the game under `vanilla` stay out (they are Bethesda's, and
        /// they would make the run depend on local state) and so does the corrupted
        /// fixture, which exists to fail loading.
        /// </remarks>
        public static TheoryData<string> EveryFixture()
        {
            var data = new TheoryData<string>();
            string root = Path.Combine(AppContext.BaseDirectory, "Resources");

            foreach (string path in Directory.GetFiles(root, "*.nif", SearchOption.AllDirectories)
                         .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                         .Where(FixtureFiles.IsFixture)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                data.Add(path);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void OnlyTheKnownGapsDiffer(string name)
        {
            // The whole graph, field by field, for every NIF in the fixtures -- not the
            // four this used to check, and not a census of block types.
            //
            // A census is what the corpus sweep does, and it cannot see anything wrong
            // *inside* a block. That is how a shader controller came back driving the
            // wrong variable in 1,648 meshes, how every off-centre collision box
            // collapsed to the body origin, and how a skin partition's triangles were
            // remapped into nonsense -- each of them with a green suite.
            //
            // Differences already known do not fail: see RoundTripBaseline, which keeps
            // the derived ones and the outstanding defects in separate lists on purpose.
            // A field in neither is new, and new is what this is for.
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            var unexplained = RoundTripBaseline.Unexplained(source, rebuilt);

            Assert.True(
                unexplained.Count == 0,
                $"{name} differs in {unexplained.Count} field(s) that are neither derived "
                + $"nor a recorded defect:\n  " + string.Join("\n  ", unexplained.Take(20)));
        }

        [Fact]
        public void TheRootKeepsItsKind()
        {
            // Half of BSXFlags is a question about the root, twice asking whether it is
            // exactly NiNode, so rebuilding one kind as another changes what the file
            // claims about itself.
            NifModel source = Load("nifly/TestNifFile_Static_SE.nif");

            Assert.Equal("NiNode", source.Blocks[0].Name);

            NifModel rebuilt = RoundTrip(source);
            NifItem root = rebuilt.GetBlock(rebuilt.FindItem(rebuilt.Footer, "Roots")!.Children[0])!;

            Assert.Equal("NiNode", root.Name);
        }

        [Fact]
        public void ACarriedClassBringsItsOwnFieldsWithIt()
        {
            // Carrying a class without the thing the class is for is worse than not
            // carrying it: a BSLODTriShape rebuilt without its triangle counts draws
            // nothing at any distance. The same shape of bug waits in every class that
            // adds fields to its base -- a BSOrderedNode's sort bound, a BSValueNode's
            // value.
            //
            // The values here are deliberately not the schema's defaults. The fixture's
            // BSOrderedNode happens to hold exactly the defaults, so it round-trips
            // whether or not anything is carried, and proves nothing either way.
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("BSOrderedNode");
            model.SetString(root, "Name", "sorted");

            model.FindItem(root, "Alpha Sort Bound")!.Value.Set(new NifVector4(1f, 2f, 3f, 4f));
            model.FindItem(root, "Static Bound")!.Value.SetCount(0);

            model.SetRoots([root]);
            model.UpdateHeader();

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSOrderedNode");

            NifVector4 bound = rebuilt.FindItem(after, "Alpha Sort Bound")!.Value.Get<NifVector4>();

            Assert.Equal(1f, bound.X, 3);
            Assert.Equal(4f, bound.W, 3);
            Assert.Equal(0u, rebuilt.FindItem(after, "Static Bound")!.Value.ToUInt());
        }

        [Theory]
        [InlineData("nifly/TestNifFile_OrderedNode_SE.nif", "BSOrderedNode")]
        [InlineData("nifly/TestNifFile_MultiBound_SE.nif", "BSMultiBoundNode")]
        public void ANodeKeepsItsKind(string name, string kind)
        {
            NifModel source = Load(name);

            Assert.Contains(source.Blocks, b => b.Name == kind);

            NifModel rebuilt = RoundTrip(source);

            Assert.Contains(rebuilt.Blocks, b => b.Name == kind);
        }

        [Theory]
        [InlineData("nifly/TestNifFile_Static_SE.nif")]
        [InlineData("multi_material_cube.nif")]
        [InlineData("nifly/TestNifFile_Skinned_Dynamic_SE.nif")]
        public void GeometryKeepsItsClass(string name)
        {
            // The two geometry families differ in where the vertices live, not merely
            // in name: a BSTriShape packs them inline, everything under NiTriBasedGeom
            // keeps them in a data block beside it. Choosing by edition alone converts
            // every shape in a file to whichever class the edition prefers, and an SE
            // file holds NiTriShape as freely as BSTriShape.
            NifModel source = Load(name);

            var expected = source.Blocks
                .Where(b => source.BlockInherits(b, "NiTriBasedGeom") || source.BlockInherits(b, "BSTriShape"))
                .GroupBy(b => b.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.NotEmpty(expected);

            NifModel rebuilt = RoundTrip(source);

            foreach ((string type, int count) in expected)
                Assert.Equal(count, rebuilt.Blocks.Count(b => b.Name == type));
        }

        [Fact]
        public void AnSeShapeKeepsItsVertexLayoutAndGainsTangents()
        {
            // SE packs its vertices inline, and the descriptor says which attributes
            // are in them and how wide one is. Getting it wrong does not fail loudly:
            // the reader walks the buffer at the wrong stride and produces geometry
            // that is merely wrong.
            NifModel source = Load("nifly/TestNifFile_Static_SE.nif");
            NifItem sourceShape = source.Blocks.First(b => source.BlockInherits(b, "BSTriShape"));

            ulong descriptor = source.FindItem(sourceShape, "Vertex Desc")!.Value.ToUInt64();

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltShape = rebuilt.Blocks.First(b => rebuilt.BlockInherits(b, "BSTriShape"));

            Assert.Equal(descriptor, rebuilt.FindItem(rebuiltShape, "Vertex Desc")!.Value.ToUInt64());

            NifItem sourceVertices = source.FindItem(sourceShape, "Vertex Data")!;
            NifItem rebuiltVertices = rebuilt.FindItem(rebuiltShape, "Vertex Data")!;

            Assert.Equal(sourceVertices.Children.Count, rebuiltVertices.Children.Count);

            // Tangents are regenerated rather than carried, so they agree to about the
            // precision the source stores them at rather than exactly.
            NifVector3 expected = source.FindItem(sourceVertices.Children[0], "Tangent")!.Value.Get<NifVector3>();
            NifVector3 actual = rebuilt.FindItem(rebuiltVertices.Children[0], "Tangent")!.Value.Get<NifVector3>();

            Assert.Equal(expected.X, actual.X, 2);
            Assert.Equal(expected.Y, actual.Y, 2);
            Assert.Equal(expected.Z, actual.Z, 2);
        }

        [Fact]
        public void TheCullingVolumeIsVisibleInTheScene()
        {
            // Six numbers on a node is a volume nobody will ever notice is wrong. So
            // it is drawn as a mesh as well, the way collision shapes are, and the
            // exact numbers stay on the properties.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifVector3 size = source.FindItem(
                source.Blocks.First(b => b.Name == "BSMultiBoundOBB"), "Size")!.Value.Get<NifVector3>();

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject volume = Assert.Single(
                scene.Objects, o => o.Class == "Model" && FbxMultiBound.IsVolumeMesh(o.Name));

            FbxObject geometry = Assert.Single(scene.ChildrenOf(volume.Id), o => o.Class == "Geometry");

            MeshGeometry mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options())!;

            // A box of the right size, centred on its node: the extents are half of
            // what the volume calls its size.
            Assert.Equal(size.X / 2f, mesh.Vertices.Max(v => v.X), 2);
            Assert.Equal(size.Y / 2f, mesh.Vertices.Max(v => v.Y), 2);
            Assert.Equal(size.Z / 2f, mesh.Vertices.Max(v => v.Z), 2);
        }

        [Fact]
        public void TheCullingVolumeDoesNotBecomeGeometry()
        {
            // It is a picture of the bound, not part of the model. Left unrecognised
            // it would come back as a box floating inside every multi-bound node.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            int shapes = source.Blocks.Count(b => source.BlockInherits(b, "BSTriShape"));

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(shapes, rebuilt.Blocks.Count(b => rebuilt.BlockInherits(b, "BSTriShape")));
        }

                [Fact]
        public void AMultiBoundNodeKeepsItsVolume()
        {
            // The volume is the whole point of the class: the engine culls against it
            // instead of working one out from the geometry, which is how a room's
            // walls are drawn only when the player can see in. Losing it leaves a
            // multi-bound node bounding nothing, and nothing looks wrong.
            NifModel source = Load("nifly/TestNifFile_MultiBound_SE.nif");

            NifItem before = Assert.Single(source.Blocks, b => b.Name == "BSMultiBoundOBB");

            NifModel rebuilt = RoundTrip(source);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSMultiBoundOBB");

            Assert.Equal(
                source.FindItem(before, "Center")!.Value.Get<NifVector3>(),
                rebuilt.FindItem(after, "Center")!.Value.Get<NifVector3>());

            Assert.Equal(
                source.FindItem(before, "Size")!.Value.Get<NifVector3>(),
                rebuilt.FindItem(after, "Size")!.Value.Get<NifVector3>());

            // And it is reachable from the node, not merely present in the file.
            NifItem node = Assert.Single(rebuilt.Blocks, b => b.Name == "BSMultiBoundNode");

            Assert.Equal(after, rebuilt.GetRef(rebuilt.GetRef(node, "Multi Bound")!, "Data"));
        }

                [Fact]
        public void ExtraDataSurvives()
        {
            // Almost every NIF has some, and none of it has an FBX equivalent: a
            // behaviour graph path, a furniture marker, a string nothing else reads.
            // Dropping it changes what the game does with the file and leaves nothing
            // to see.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            var expected = source.Blocks
                .Where(b => source.BlockInherits(b, "NiExtraData") && b.Name != "BSXFlags")
                .GroupBy(b => b.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            Assert.NotEmpty(expected);

            NifModel rebuilt = RoundTrip(source);

            foreach ((string type, int count) in expected)
            {
                Assert.Equal(count, rebuilt.Blocks.Count(b => b.Name == type));
            }
        }

        [Fact]
        public void TheCalculatedBsxFlagsIsNotCarriedAsWell()
        {
            // BSXFlags is extra data too, and is recalculated rather than carried --
            // so carrying it here as well would leave the file with two, and the
            // engine reads the first it finds.
            NifModel source = Load("generate_rb_box.nif");

            Assert.Single(source.Blocks, b => b.Name == "BSXFlags");

            NifModel rebuilt = RoundTrip(source);

            Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");
        }

                [Fact]
        public void SharedPropertyBlocksAreSharedAgain()
        {
            // Eight shapes pointing at two alpha properties came back with eight, and
            // two texture sets came back as twenty-seven. Sharing is data: it says the
            // shapes are the same material, not merely alike.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            int alphas = source.Blocks.Count(b => b.Name == "NiAlphaProperty");
            int sets = source.Blocks.Count(b => b.Name == "BSShaderTextureSet");

            Assert.NotEqual(0, alphas);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(alphas, rebuilt.Blocks.Count(b => b.Name == "NiAlphaProperty"));
            Assert.Equal(sets, rebuilt.Blocks.Count(b => b.Name == "BSShaderTextureSet"));
        }

        [Fact]
        public void IdenticalBlocksKeptApartStayApart()
        {
            // The other half, and the reason sharing cannot be decided by comparing
            // content: this file carries three texture sets that are identical and
            // separate. Merging equal blocks would be as wrong as never merging.
            NifModel source = Load("multi_material_cube.nif");

            var sets = source.Blocks.Where(b => b.Name == "BSShaderTextureSet").ToList();

            Assert.True(sets.Count > 1, "the fixture is supposed to have several");

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(sets.Count, rebuilt.Blocks.Count(b => b.Name == "BSShaderTextureSet"));
        }

                [Fact]
        public void ASkeletonAuthoredElsewhereIsStillASkeleton()
        {
            // The case that has no provenance to carry: a rig built in a DCC tool and
            // brought in for the first time. Without the classes there is nothing to
            // restore, and a plain bhkCollisionObject means the engine does not treat
            // the file as a ragdoll however many bones and constraints it has.
            //
            // ck-cmd covers this with an export_rig flag the caller has to know to
            // set. The constraints say it instead: a ragdoll constraint is something
            // only a skeleton has.
            NifModel source = Load("xpmsse/skeleton_cow.nif");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            // Strip what a scene from elsewhere would never have had.
            int stripped = 0;

            foreach (FbxObject node in scene.Objects.Where(o => o.Class == "Model"))
            {
                foreach (string property in new[]
                         {
                             FbxCollisionObject.TypeProperty,
                             FbxCollisionObject.BodyTypeProperty,
                             FbxCollisionObject.HeirGainProperty,
                             FbxCollisionObject.VelGainProperty
                         })
                {
                    // Renaming is how a property is disabled here: the reader looks
                    // them up by name, so a renamed one is invisible to it.
                    if (node.Properties.Find(property) is { } found)
                    {
                        found.Node.Name = "P_stripped";
                        stripped++;
                    }
                }
            }

            Assert.NotEqual(0, stripped);

            NifModel rebuilt = new FbxToNif(
                scene,
                new FbxToNifOptions
                {
                    RootName = source.GetName(
                        source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);

            Assert.Equal(
                source.Blocks.Count(b => b.Name == "bhkBlendCollisionObject"),
                rebuilt.Blocks.Count(b => b.Name == "bhkBlendCollisionObject"));

            // The flags are the point: bit 2 says the engine has a ragdoll.
            Assert.Equal(source.Calculate(), rebuilt.Calculate());
        }

                /// <summary>Every collision body's world placement, keyed by the node that owns it.</summary>
        /// <remarks>
        /// Keyed by owner rather than by block order, which differs between the two
        /// files: comparing by index pairs a body with whichever body happens to sit
        /// at that position in the rebuilt list, and reports 4 of 24 for a skeleton
        /// that round-trips perfectly.
        /// </remarks>
        private static Dictionary<string, NifVector4> BodyPlacements(NifModel model)
        {
            var placements = new Dictionary<string, NifVector4>(StringComparer.Ordinal);

            foreach (NifItem node in model.Blocks.Where(b => model.BlockInherits(b, "NiAVObject")))
            {
                if (model.GetRef(node, "Collision Object") is { } collision
                    && model.GetRef(collision, "Body") is { } body
                    && model.FindItem(body, @"Rigid Body Info\Translation") is { } translation)
                {
                    placements[model.GetName(node)] = translation.Value.Get<NifVector4>();
                }
            }

            return placements;
        }

        [Theory]
        [MemberData(nameof(CkCmdExamples))]
        public void AConvexHullKeepsEveryCorner(string name)
        {
            // The corners of a `bhkConvexVerticesShape` are not a point cloud to be
            // reduced: they are already a hull, the one Qhull gave NifSkope when the
            // shape was authored. So taking the hull of them again has to give every
            // one of them back, and a round trip has to return the shape it was handed.
            //
            // Nothing checked this. `Vertices` sits in KnownGaps because the *order*
            // changes, and that excused the values along with it -- a hull that
            // silently dropped corners passed. It was dropping them: `dwecog01` is a
            // disc a fifth of a millimetre thick, which seeded a tetrahedron flatter
            // than the hull's own tolerance and came back as 4 of its 48 corners, and
            // a mill pond's corners each moved three hundredths of a unit because the
            // two Havok scale factors are not reciprocals.
            NifModel source = Load(name);

            var hulls = source.Blocks.Where(b => b.Name == "bhkConvexVerticesShape").ToList();

            if (hulls.Count == 0)
                return;

            NifModel rebuilt = RoundTrip(source);

            var after = rebuilt.Blocks.Where(b => b.Name == "bhkConvexVerticesShape").ToList();

            Assert.Equal(hulls.Count, after.Count);

            for (int i = 0; i < hulls.Count; i++)
            {
                List<NifVector4> before = Corners(source, hulls[i]);
                List<NifVector4> now = Corners(rebuilt, after[i]);

                // Relative to the shape, not absolute. The only fixture with a hull is
                // three hundredths of a unit across, and the scale bug moved its
                // corners by seven millionths -- under any fixed tolerance worth
                // writing, and a fifth of a percent of the shape.
                float extent = MathF.Max(
                    before.Max(v => v.X) - before.Min(v => v.X),
                    MathF.Max(
                        before.Max(v => v.Y) - before.Min(v => v.Y),
                        before.Max(v => v.Z) - before.Min(v => v.Z)));

                float slack = MathF.Max(extent * 1e-5f, 1e-9f);

                var lost = before
                    .Where(v => !now.Any(u =>
                        MathF.Abs(u.X - v.X) < slack
                        && MathF.Abs(u.Y - v.Y) < slack
                        && MathF.Abs(u.Z - v.Z) < slack))
                    .ToList();

                Assert.True(lost.Count == 0,
                    $"{name}: {lost.Count} of {before.Count} corners did not come back, "
                    + $"first ({lost.FirstOrDefault().X}, {lost.FirstOrDefault().Y}, {lost.FirstOrDefault().Z})");

                // And nothing invented: a hull that gained corners is refitting from a
                // tessellation that is not the shape.
                Assert.Equal(before.Count, now.Count);
            }

            static List<NifVector4> Corners(NifModel model, NifItem shape) =>
                model.FindItem(shape, "Vertices") is { } array
                    ? array.Children.Select(v => v.Value.Get<NifVector4>()).ToList()
                    : [];
        }

        /// <summary>
        /// Fixtures whose shader controllers are all reachable from the root.
        /// </summary>
        /// <remarks>
        /// `TestNifFile_LooseBlocks_SE` is deliberately not here. Two of its three
        /// shader properties are owned by nothing at all — that is what the fixture is
        /// for — and a controller on a property no geometry references does not survive
        /// a scene it was never in. That drop predates this and is unchanged by it:
        /// three become one either way. It is a question about loose blocks, not about
        /// what a controller drives.
        /// </remarks>
        public static TheoryData<string> ShaderControllerFiles() => new(
            "nifly/TestNifFile_OrderedNode_SE.nif",
            "nifly/TestNifFile_Animated_LE.nif");

        [Theory]
        [MemberData(nameof(ShaderControllerFiles))]
        public void AShaderControllerKeepsTheVariableItDrives(string name)
        {
            // A `BSEffectShaderPropertyFloatController` says which of its shader
            // property's floats it animates -- emissive multiple, alpha, a UV offset --
            // and that number is the whole of what the controller is for. Nothing
            // carried it, so every one of them came back driving variable 0: a fade
            // curve rebuilt as an emissive curve, on 11,140 of the game's 13,244 shader
            // controllers.
            //
            // No count of blocks can see this. The rebuilt file has exactly the
            // controllers the original had, pointed at exactly the right interpolators,
            // and every one of them aimed at the wrong thing -- which is why it sat
            // undisturbed under a divergence that *was* visible, dlceclipsesky's
            // twenty-five NiFloatData coming back as twenty-seven. Both are the same
            // missing id: with nothing to tell two controllers on a node apart, neither
            // the variable nor the data they share can be keyed to one of them.
            NifModel source = Load(name);

            static List<uint> Variables(NifModel m) => m.Blocks
                .Select(b => NifAnimAccess.ControlledVariable(m, b))
                .Where(f => f is not null)
                .Select(f => f!.Value.ToUInt())
                .OrderBy(v => v)
                .ToList();

            List<uint> before = Variables(source);

            Assert.NotEmpty(before);

            // A fixture where every one is zero would pass whatever the code did.
            Assert.Contains(before, v => v != 0);

            Assert.Equal(before, Variables(RoundTrip(source)));
        }

        [Fact]
        public void AStringFieldNobodySetsNamesNoString()
        {
            // A string field holds an index into the header's string table, and "no
            // string" is -1. A freshly inserted field sat at 0 instead, which names the
            // *first* string in the table -- and the root's name is usually the first
            // thing written, so the field silently took it.
            //
            // `NiTextKeyExtraData` is where it showed. `WriteTextKeys` never sets the
            // block's own `Name`, so every animated file this tool wrote carried an
            // extra-data block claiming to be called after the root. All 22 such blocks
            // in the fixtures carry -1; anything looking extra data up by name would
            // find the wrong block.
            //
            // Neither corpus sweep can see this. They rebuild from a file whose string
            // fields already hold the source's own indices, so the authoring default is
            // never reached.
            NifModel model = NifModel.CreateNew(Db);
            NifItem block = model.InsertBlock("NiTextKeyExtraData");

            NifItem name = model.FindItem(block, "Name")!;

            Assert.Equal(uint.MaxValue, name.Value.ToUInt());

            // And as a StringIndex, which is written out as a plain uint. Left as the
            // String type it would go through the writer's string-table branch, where
            // anything above a plausible index is clamped to 0 -- so the -1 would have
            // become the very 0 this is avoiding.
            Assert.Equal(NifValueType.StringIndex, name.Value.Type);

            // A name that *is* set still resolves, so this has not simply broken them.
            model.SetString(block, "Name", "keys");

            Assert.Equal("keys", model.ResolveString(model.FindItem(block, "Name")!));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AUvSurvivesTheRoundTrip(string name)
        {
            // NIF's V axis points the other way from FBX's, so the export flips it and
            // the reader flips it back -- that is what `FbxMeshReader.Options.InvertV`
            // is for, and it is on by default. Packing an SE vertex then flipped a third
            // time, so every texture coordinate came back as `1 - v` and the mesh was
            // textured upside down. The `NiTriShapeData` path beside it has always
            // written the value straight through; two places, one convention, one of
            // them wrong.
            //
            // Compared as a set of distinct values. Vertex *order* is a separate defect
            // (see RoundTripBaseline.Open) and vertices identical in every attribute are
            // allowed to merge, so neither position nor count is the thing being
            // asserted here -- only that a coordinate comes back as the coordinate it
            // was, rather than as one minus it.
            NifModel source = Load(name);
            NifModel rebuilt = RoundTrip(source);

            static List<string> Uvs(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.FindItem(b, "Vertex Data") is { Children.Count: > 0 })
                    .SelectMany(b => m.FindItem(b, "Vertex Data")!.Children)
                    .Select(v => m.FindItem(v, "UV")?.Value.Get<NifVector2>())
                    .Where(uv => uv is not null)
                    .Select(uv => $"{uv!.Value.X:F4},{uv.Value.Y:F4}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)];

            List<string> before = Uvs(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Uvs(rebuilt));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ABodyKeepsTheLayerItWasOn(string name)
        {
            // A rigid body's collision layer decides what it collides with, and it is
            // also the input to the motion profile -- the layer chooses the motion
            // system, the deactivation and the quality (spec §5.7). So losing it loses
            // those with it.
            //
            // It was being read off the body, written into the scene twice, read back on
            // the way in, and then used only to answer "is this a static". Nothing ever
            // wrote it to the body it came from, so every rebuilt body kept the field's
            // default and anything not already static came back as though it were.
            NifModel source = Load(name);

            static List<string> Layers(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "bhkRigidBody"))
                    .Select(b => FbxCollisionMaterial.LayerOf(m, b))];

            List<string> before = Layers(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Layers(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ASequenceKeepsHowItEndsAndWhatItAccumulatesAgainst(string name)
        {
            // Two fields of a NiControllerSequence that were not modelled, not read and
            // not carried, so both were invented on the way out.
            //
            // `Cycle Type` was written as a constant named CycleClamp holding zero --
            // and nif.xml's zero is CYCLE_LOOP, clamp being 2. So every sequence in
            // every file this tool wrote came back looping, including the ones meant to
            // play once and stop. A door that opens and stays open instead opens for
            // ever.
            //
            // `Accum Root Name` was synthesised from whichever block happened to be
            // first, so a sequence accumulating against `Mesh01` came back naming
            // `Scene Root`.
            NifModel source = Load(name);

            static List<string> Sequences(NifModel m) =>
                [.. m.Blocks
                    .Where(b => b.Name == "NiControllerSequence")
                    .Select(b => $"{m.GetString(b, "Name")}|"
                        + $"{m.FindItem(b, "Cycle Type")?.Value.ToUInt()}|"
                        + $"{m.GetString(b, "Accum Root Name")}")];

            List<string> before = Sequences(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Sequences(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ALightingShaderKeepsWhatItLooksLike(string name)
        {
            // Four values of a BSLightingShaderProperty that were being replaced with
            // defaults. Three -- the two lighting effects and the refraction strength --
            // were not modelled at all, so a rim power of 10 came back as 0.3.
            //
            // The fourth is the shader *type*, which the exporter wrote by name and the
            // importer never read. That is not only a label: nif.xml makes `Environment
            // Map Scale` conditional on the type being 1, so an environment-mapped shader
            // lost its scale as well and the two reported as separate faults.
            NifModel source = Load(name);

            static List<string> Shaders(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "BSLightingShaderProperty"))
                    .Select(b => string.Join("|", new[]
                    {
                        "Shader Type", "Environment Map Scale",
                        "Lighting Effect 1", "Lighting Effect 2", "Refraction Strength"
                    }.Select(f => $"{f}={m.FindItem(b, f)?.Value.ToString() ?? "-"}")))];

            List<string> before = Shaders(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Shaders(RoundTrip(source)));
        }

        /// <summary>A shape's vertex buffer, following the skin when it keeps none.</summary>
        private static NifItem VertexBuffer(NifModel model, NifItem shape)
        {
            if (model.FindItem(shape, "Vertex Data") is { Children.Count: > 0 } own)
                return own;

            NifItem? skin = model.GetRef(shape, "Skin");

            NifItem? partition = skin is null
                ? null
                : model.GetRef(skin, "Skin Partition")
                  ?? (model.GetRef(skin, "Data") is { } data ? model.GetRef(data, "Skin Partition") : null);

            return (partition is null ? null : model.FindItem(partition, "Vertex Data"))
                   ?? model.FindItem(shape, "Vertex Data")!;
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ASkinnedShapeKeepsItsGeometryWhereTheFormatDoes(string name)
        {
            // A skinned Skyrim SE shape keeps nothing in itself: its vertices live in
            // the NiSkinPartition and the shape's own counts are zero. NifToFbx already
            // reads it that way -- it follows the skin when the shape holds nothing --
            // but the import had nowhere to put it back, because the partition writer
            // sizes the per-partition arrays and never the block's own. So the geometry
            // stayed on the shape and the partition came back empty.
            NifModel source = Load(name);

            static List<string> Where(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "BSTriShape") && m.GetRef(b, "Skin") is not null)
                    .Select(b =>
                    {
                        NifItem? skin = m.GetRef(b, "Skin");
                        NifItem? part = skin is null ? null
                            : m.GetRef(skin, "Skin Partition")
                              ?? (m.GetRef(skin, "Data") is { } d ? m.GetRef(d, "Skin Partition") : null);

                        int own = m.FindItem(b, "Vertex Data")?.Children.Count ?? 0;
                        int inPart = part is null ? 0 : m.FindItem(part, "Vertex Data")?.Children.Count ?? 0;

                        return $"shape={own} partition={inPart}";
                    })];

            List<string> before = Where(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Where(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ADynamicShapeHasNoPositionInItsVertex(string name)
        {
            // A BSDynamicTriShape keeps its positions in its own buffer of Vector4s --
            // the static ones are zero in every file seen -- so the format does not
            // store them twice and the descriptor's `Vertex` flag is off. nif.xml
            // follows that through: without the flag a vertex has no position and no
            // bitangent X, and the struct begins at the texture coordinate, 24 bytes
            // rather than 40.
            //
            // The descriptor is calculated from what a vertex holds, not carried, so
            // getting it right means knowing that a dynamic shape's vertex holds no
            // position. Computing it from the mesh alone gave every dynamic shape a
            // position it does not carry.
            //
            // Needs its own test: `Vertex Desc` is on the open list for an unrelated
            // reason, so the general comparison excuses this case as well.
            NifModel source = Load(name);

            static List<string> Dynamic(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "BSDynamicTriShape"))
                    .Select(b => m.FindItem(b, "Vertex Desc")?.Value.ToUInt64().ToString() ?? "-")];

            List<string> before = Dynamic(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Dynamic(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AControllerKeepsTheFlagsItCameWith(string name)
        {
            // A controller's flags say whether it is active and which way it plays, and
            // they were written from a constant -- 12 for a property controller -- over
            // whatever the file held. The game's are as readily 72 or 108, so a shader
            // controller came back active and looping when it was neither.
            //
            // Only controllers that came from a file. The manager, the multi-target
            // transform controller and the blend interpolators are made by this port and
            // have no source to read from; they still take the constant, and `Flags` is
            // on the open list for them.
            //
            // Needs its own test for that reason: the general comparison excuses `Flags`
            // everywhere while those remain.
            //
            // Compared as distinct class-and-value pairs rather than one per block. A
            // controller hanging off a shader property that nothing owns does not survive
            // a scene it was never in, which is a separate matter from what its flags say.
            NifModel source = Load(name);

            static List<string> Flags(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "NiTimeController")
                                && !m.BlockInherits(b, "NiControllerManager")
                                && !m.BlockInherits(b, "NiMultiTargetTransformController"))
                    .Select(b => $"{b.Name}={m.FindItem(b, "Flags")?.Value.ToUInt()}")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal)];

            List<string> before = Flags(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Flags(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AConstraintChainKeepsEveryBodyItPassesThrough(string name)
        {
            // A constraint chain is not two bodies. `Entity A` and `Entity B` name only
            // the first pair; the rest of the chain is in `Chained Entities` and nowhere
            // else -- `TestNifFile_DeepGraph_SE`'s rope is twenty-five bodies, 4 through
            // 52.
            //
            // Both entity fields are deliberately not carried, on the grounds that the
            // scene hierarchy says which bodies are joined. That holds for a pair, where
            // the constraint hangs off one body and names the other, and does not hold
            // for a chain: nothing in the scene records the order. So every rebuilt chain
            // came back joining nothing.
            NifModel source = Load(name);

            static List<string> Chains(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.FindItem(b, @"Constraint Chain Info\Chained Entities") is not null)
                    .Select(b => string.Join(
                        ",",
                        m.GetRefArray(b, @"Constraint Chain Info\Chained Entities").Select(m.GetName)))];

            List<string> before = Chains(source);

            if (before.Count == 0 || before.All(c => c.Length == 0))
                return;

            Assert.Equal(before, Chains(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ATransformEntryNamesTheControllerThatDrivesIt(string name)
        {
            // A sequence entry says which node it drives, which interpolator holds the
            // keys, and which controller plays them. The property entries set all three;
            // the transform entry set the first two and left the reference null, so the
            // engine had the keys and nothing bound to play them.
            //
            // A transform track is driven by the one multi-target controller hanging off
            // the manager -- that is what it is for, and where the file points.
            NifModel source = Load(name);

            static int Bound(NifModel m) =>
                m.Blocks
                    .Where(b => b.Name == "NiControllerSequence")
                    .Select(b => m.FindItem(b, "Controlled Blocks"))
                    .Where(l => l is not null)
                    .SelectMany(l => l!.Children)
                    .Count(e => m.GetString(e, "Controller Type") == "NiTransformController"
                                && m.GetRef(e, "Controller") is not null);

            int before = Bound(source);

            if (before == 0)
                return;

            Assert.Equal(before, Bound(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AGeometryKeepsItsActiveMaterial(string name)
        {
            // `Material Data` sits on NiGeometry, below the base class the field carrier
            // treats as a shape's own, so nothing carried this and every rebuilt shape
            // took nif.xml's default of -1. The fixtures hold 0 ten times and -1 six
            // times, both with no materials at all, so there is nothing to assume.
            NifModel source = Load(name);

            static List<int> Active(NifModel m) =>
                [.. m.Blocks
                    .Select(b => m.FindItem(b, @"Material Data\Active Material"))
                    .Where(i => i is not null)
                    .Select(i => i!.Value.ToInt())];

            List<int> before = Active(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Active(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ACompressedMeshShapePointsBackAtTheRoot(string name)
        {
            // nif.xml hedges -- "Points to root node?" -- and the corpus does not: all
            // 8,188 compressed mesh shapes Skyrim ships point at the root block. Nothing
            // wrote it, so every rebuilt shape came back with a null pointer.
            NifModel source = Load(name);

            if (!source.Blocks.Any(b => b.Name == "bhkCompressedMeshShape"))
                return;

            NifModel rebuilt = RoundTrip(source);
            NifItem root = rebuilt.Blocks[0];

            List<NifItem> shapes = [.. rebuilt.Blocks.Where(b => b.Name == "bhkCompressedMeshShape")];

            Assert.NotEmpty(shapes);
            Assert.All(shapes, shape => Assert.Same(root, rebuilt.GetRef(shape, "Target")));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ABodyKeepsHowItSimulates(string name)
        {
            // Friction, restitution, damping, penetration depth and the velocity
            // ceilings are authored settings, not consequences of the geometry --
            // nothing about a rebuilt hull says how slippery it is. Across the 14,408
            // bodies Skyrim ships, friction takes 16 distinct values and penetration
            // depth 2,185, and ck-cmd reads every one of them back off the body when
            // it builds a ragdoll (Skeleton.cpp:1003-1028).
            NifModel source = Load(name);

            static List<(string, float)> Scalars(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "bhkRigidBody"))
                    .SelectMany(b => Fbx.FbxRigidBodyInfo.Scalars
                        .Select(s => (s.Field, m.FindItem(b, $@"Rigid Body Info\{s.Field}")))
                        .Where(pair => pair.Item2 is not null)
                        .Select(pair => (pair.Field, pair.Item2!.Value.ToFloat())))];

            List<(string, float)> before = Scalars(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Scalars(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ACapsuleKeepsItsAxisAndWhichEndCameFirst(string name)
        {
            // Two defects sat on top of each other here. The fit snapped the axis to
            // the longest side of the bounding box, which is right only for a capsule
            // lying along X, Y or Z -- 268 of Skyrim's 1,966 run diagonally, mostly in
            // the skeletons where a limb's collision follows the bone, and those came
            // back with both endpoints collapsed onto the centre. And the end order was
            // reversed against Bethesda's, so 94.9% of capsules came back with their
            // two points exchanged: the same volume, and not the same file.
            NifModel source = Load(name);

            static List<(NifVector3, NifVector3)> Capsules(NifModel m) =>
                [.. m.Blocks
                    .Where(b => b.Name == "bhkCapsuleShape")
                    .Select(b => (
                        m.FindItem(b, "First Point")!.Value.Get<NifVector3>(),
                        m.FindItem(b, "Second Point")!.Value.Get<NifVector3>()))];

            List<(NifVector3, NifVector3)> before = Capsules(source);

            if (before.Count == 0)
                return;

            List<(NifVector3, NifVector3)> after = Capsules(RoundTrip(source));

            Assert.Equal(before.Count, after.Count);

            for (int i = 0; i < before.Count; i++)
            {
                // A capsule of zero length is the collapse this guards against.
                Assert.True(
                    Distance(after[i].Item1, after[i].Item2) > 0.5f * Distance(before[i].Item1, before[i].Item2),
                    $"capsule {i} of {name} came back {Distance(after[i].Item1, after[i].Item2)} long, "
                    + $"where it was {Distance(before[i].Item1, before[i].Item2)}");

                // And each end has to be the end it was, not the other one.
                Assert.True(
                    Distance(before[i].Item1, after[i].Item1) < 1e-4f
                    && Distance(before[i].Item2, after[i].Item2) < 1e-4f,
                    $"capsule {i} of {name} came back with its ends moved or exchanged");
            }
        }

        private static float Distance(NifVector3 a, NifVector3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;

            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ABodyKeepsHowHavokSimulatesIt(string name)
        {
            // WriteStaticMotion wrote the static profile onto every body it was handed,
            // though the call site's comment claimed it read the layer to decide. So
            // ck-cmd's three-way split collapsed to its third arm and a biped's bodies
            // came back BOX_STABILIZED/INVALID/OFF where the file had
            // BOX_INERTIA/FIXED/LOW -- taking bit 6 of BSXFlags with them, which asks
            // whether any body is dynamic.
            NifModel source = Load(name);

            static List<uint> Profiles(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "bhkRigidBody"))
                    .SelectMany(b => Fbx.FbxRigidBodyInfo.MotionFields
                        .Select(f => m.GetUInt(b, $@"Rigid Body Info\{f.Field}")))];

            List<uint> before = Profiles(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Profiles(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void EveryLayerFieldOnABodyAgrees(string name)
        {
            // A rigid body carries two Havok filters: bhkWorldObject's own, and the copy
            // inside Rigid Body Info. They agree in all 14,408 bodies Skyrim ships. The
            // import wrote whichever it found first, so the other kept the default and a
            // biped's body came back half on layer 8 and half on layer 1.
            NifModel rebuilt = RoundTrip(Load(name));

            foreach (NifItem body in rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "bhkRigidBody")))
            {
                List<uint> layers =
                [
                    .. new[] { @"Havok Filter\Layer", @"Rigid Body Info\Havok Filter\Layer", @"Rigid Body Info\Layer" }
                        .Select(p => rebuilt.FindItem(body, p))
                        .Where(i => i is not null && rebuilt.EvalCondition(i))
                        .Select(i => i!.Value.ToUInt())
                ];

                Assert.True(
                    layers.Distinct().Count() <= 1,
                    $"{name}: a body came back with its layer fields disagreeing: "
                    + string.Join(", ", layers));
            }
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void NiSkinDataKeepsTheWeightsItWasAuthoredWith(string name)
        {
            // Two faults, one on top of the other, and the baseline blamed a third
            // thing that was not happening.
            //
            // LimitInfluences divided every weight by its vertex total whether or not
            // anything had been dropped. Vanilla weights are already normalised -- over
            // 4,201,422 skinned vertices the worst |sum - 1| with four influences or
            // fewer is 1.6e-7 -- so that was arithmetic with nothing to correct, and it
            // moved every weight in the file. It also fed both copies, which do not
            // agree: NiSkinData keeps what was authored and the partition holds it
            // normalised, and TestNifFile_LooseBlocks_SE has vertices summing to
            // 0.999924 in one and to exactly one in the other.
            //
            // And it rebuilt each bone's list by walking a dictionary, so the weights
            // came back on the right vertices in nobody's order.
            NifModel source = Load(name);

            static List<(string Bone, List<(uint, float)> Weights)> Lists(NifModel m) =>
                [.. m.Blocks
                    .Where(b => b.Name == "NiSkinData")
                    .SelectMany(d => m.FindItem(d, "Bone List") is { } l ? l.Children : [])
                    .Select(entry => (
                        string.Empty,
                        (List<(uint, float)>)
                        [
                            .. (m.FindItem(entry, "Vertex Weights")?.Children ?? [])
                                .Select(w => (
                                    m.FindItem(w, "Index")!.Value.ToUInt(),
                                    m.FindItem(w, "Weight")!.Value.ToFloat()))
                        ]))];

            List<(string, List<(uint, float)>)> before = Lists(source);

            if (before.Count == 0 || before.All(b => b.Item2.Count == 0))
                return;

            List<(string, List<(uint, float)>)> after = Lists(RoundTrip(source));

            Assert.Equal(before.Count, after.Count);

            for (int i = 0; i < before.Count; i++)
            {
                // Order included: these are lists in the file, not sets.
                Assert.Equal(before[i].Item2, after[i].Item2);
            }
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AShapeWithEmptySkinDataTakesItsWeightsFromTheBuffer(string name)
        {
            // Some shapes keep their weights only in the vertex buffer and leave
            // NiSkinData's bone list empty -- treepineforestash03.nif is one, with every
            // vertex fully weighted to bone 0 in the buffer and nothing at all beside
            // it. Reading the bone list by preference has to fall back for those, or the
            // rebuilt mesh comes back with no weights and collapses onto its root.
            //
            // Worth pinning because it is what makes reading NiSkinData safe at all:
            // over 1,768,446 vanilla skinned vertices, every case where the buffer holds
            // an influence NiSkinData lacks is a shape whose NiSkinData is empty. With
            // the fallback counted, the number of influences lost is zero.
            NifModel source = Load(name);

            List<NifItem> emptied = [];

            foreach (NifItem data in source.Blocks.Where(b => b.Name == "NiSkinData").ToList())
            {
                if (source.FindItem(data, "Bone List") is not { Children.Count: > 0 } list)
                    continue;

                foreach (NifItem entry in list.Children)
                    source.SetArraySize(entry, "Num Vertices", "Vertex Weights", 0);

                emptied.Add(data);
            }

            if (emptied.Count == 0)
                return;

            NifModel rebuilt = Rebuilt(source);

            static int Weighted(NifModel m) =>
                m.Blocks
                    .Where(b => b.Name == "NiSkinPartition")
                    .Select(p => m.FindItem(p, "Vertex Data"))
                    .Where(v => v is not null)
                    .SelectMany(v => v!.Children)
                    .Count(vertex => m.FindItem(vertex, "Bone Weights") is { } w
                                     && w.Children.Any(c => c.Value.ToFloat() > 0f));

            // The source still has its buffer, so the rebuild must still have one.
            if (Weighted(source) == 0)
                return;

            Assert.True(
                Weighted(rebuilt) > 0,
                $"{name}: emptying NiSkinData left the rebuilt mesh with no vertex weights at all");
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ANodeKeepsTheFlagsItWasAuthoredWith(string name)
        {
            // Flags sits on NiAVObject, below the class each node adds to, so the
            // own-fields carrier never saw it and every rebuilt node took nif.xml's
            // default of 0x8000E.
            //
            // The default covers 44% of them. Across 42,955 AV objects in a fifth of
            // Skyrim's meshes the field takes 23 distinct values, and 0xE -- the same
            // flags without the 0x80000 bit -- accounts for another 44%. Nothing about
            // the graph says which: see the spec for what was measured against it.
            NifModel source = Load(name);

            static List<uint> Flags(NifModel m) =>
                [.. m.Blocks
                    .Where(b => m.BlockInherits(b, "NiAVObject"))
                    .Select(b => m.FindItem(b, "Flags"))
                    .Where(f => f is { Children.Count: 0 })
                    .Select(f => f!.Value.ToUInt())];

            List<uint> before = Flags(source);

            if (before.Count == 0)
                return;

            Assert.Equal(before, Flags(RoundTrip(source)));
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ABlendInterpolatorLooksLikeEveryOtherOne(string name)
        {
            // All 3,593 blend interpolators in a third of Skyrim's meshes are the same
            // block: flags 1, array size 2, zero weight threshold. Flags bit 0 is
            // "Manager Controlled", which one reached through a sequence necessarily is,
            // and nif.xml makes five of the block's fields conditional on that bit being
            // clear -- so leaving it at zero wrote five fields the file does not have.
            NifModel rebuilt = RoundTrip(Load(name));

            List<NifItem> blends =
                [.. rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiBlendInterpolator"))];

            if (blends.Count == 0)
                return;

            Assert.All(blends, b =>
            {
                Assert.Equal(1u, rebuilt.GetUInt(b, "Flags"));
                Assert.Equal(2u, rebuilt.GetUInt(b, "Array Size"));
            });
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AControllerDoesNotClaimAnInfiniteSpan(string name)
        {
            // nif.xml starts Start Time and Stop Time at FLT_MAX and -FLT_MAX, and
            // nothing replaced them, so every controller a sequence drove came back
            // claiming an inverted infinite span. That is the right value for the
            // multi-target controller, which has no timeline of its own -- all 310 in a
            // quarter of Skyrim's meshes hold exactly that -- and the wrong one for a
            // controller that does have keys.
            NifModel rebuilt = RoundTrip(Load(name));

            foreach (NifItem c in rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiTimeController")))
            {
                float start = rebuilt.FindItem(c, "Start Time")?.Value.ToFloat() ?? 0f;
                float stop = rebuilt.FindItem(c, "Stop Time")?.Value.ToFloat() ?? 0f;

                // The two structural controllers have no timeline of their own -- one
                // is a fan-out to the nodes a sequence names, the other the root of the
                // whole arrangement -- and Skyrim says so with an inverted infinite
                // span. All 310 of each in a quarter of the meshes hold exactly this.
                if (c.Name is "NiMultiTargetTransformController" or "NiControllerManager")
                {
                    Assert.Equal(float.MaxValue, start);
                    Assert.Equal(float.MinValue, stop);
                    continue;
                }

                Assert.True(
                    start != float.MaxValue && stop != float.MinValue,
                    $"{name}: {c.Name} came back with the schema's empty span, {start} to {stop}");
            }
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void ANodeAuthoredWithoutFlagsGetsWhatItsClassUsuallyHas(string name)
        {
            // A scene modelled in a DCC tool carries no nif_av_flags, and one value for
            // every node is the wrong answer: the two big classes pull opposite ways.
            // Of 49,469 NiNodes 60.4% are 0xE, and of 30,106 BSTriShapes 68.4% are
            // 0x8000E -- so nif.xml's single 0x8000E is right for barely half a file.
            NifModel source = Load(name);
            var scene = new FbxScene(new NifToFbx(source).Convert());

            foreach (FbxObject node in scene.Objects)
                node.Properties.Remove(FbxNodeType.FlagsProperty);

            NifItem sourceRoot = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifModel rebuilt = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = source.GetName(sourceRoot),
                Version = source.Version,
                UserVersion = source.UserVersion,
                LegendaryEdition = source.BSVersion < 100
            }).Convert(Db);

            List<NifItem> nodes =
                [.. rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiAVObject")
                                              && rebuilt.FindItem(b, "Flags") is { Children.Count: 0 })];

            Assert.NotEmpty(nodes);

            Assert.All(nodes, b => Assert.Equal(
                FbxNodeType.DefaultFlagsFor(b.Name), rebuilt.GetUInt(b, "Flags")));

            // And at least one of them must not be the schema's blanket value, or this
            // is passing on a file that happens to hold nothing but 0x8000E classes.
            if (nodes.Any(b => b.Name is "NiNode" or "BSDynamicTriShape"))
                Assert.Contains(nodes, b => rebuilt.GetUInt(b, "Flags") != FbxNodeType.DefaultFlags);
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AShaderKeepsBothOfItsFlagWords(string name)
        {
            // Nothing wrote either word, so every rebuilt shader took nif.xml's
            // defaults. Those are poor defaults: across 20,576 shader properties in a
            // quarter of Skyrim's meshes, Shader Flags 1 holds 225 distinct values and
            // Shader Flags 2 holds 111, and the commonest covers 33% and 43.7%.
            NifModel source = Load(name);

            // Matched by shape name, not by walking every shader block: a file can hold
            // shader properties this rebuilds nothing for, and those are somebody else's
            // entry on the open list.
            static Dictionary<string, (uint One, uint Two)> Words(NifModel m)
            {
                var words = new Dictionary<string, (uint, uint)>(StringComparer.Ordinal);

                foreach (NifItem shape in m.Blocks.Where(b => m.BlockInherits(b, "NiAVObject")))
                {
                    if (m.GetName(shape) is not { Length: > 0 } name) continue;
                    if (m.GetRef(shape, "Shader Property") is not { } shader) continue;
                    if (m.FindItem(shader, "Shader Flags 1") is not { } one) continue;
                    if (m.FindItem(shader, "Shader Flags 2") is not { } two) continue;

                    words[name] = (one.Value.ToUInt(), two.Value.ToUInt());
                }

                return words;
            }

            Dictionary<string, (uint One, uint Two)> before = Words(source);

            if (before.Count == 0)
                return;

            Dictionary<string, (uint One, uint Two)> after = Words(RoundTrip(source));

            foreach ((string shape, var expected) in before)
            {
                if (after.TryGetValue(shape, out var actual))
                    Assert.Equal(expected, actual);
            }
        }

        [Theory]
        [MemberData(nameof(EveryFixture))]
        public void AMaterialAuthoredWithoutFlagsGetsCkCmdsDerivedBits(string name)
        {
            // A material modelled in a DCC tool carries no flag words, and nif.xml's
            // default claims vertex colours (bit 5 of 0x8021) whether the mesh has any
            // or not. ck-cmd derives four bits for that case -- vertex colours, vertex
            // alpha, skinned and specular (FBXWrangler.cpp:3445-3451 and :3576) -- and
            // se-cmd follows it, since a scene may pass through either tool.
            NifModel source = Load(name);
            var scene = new FbxScene(new NifToFbx(source).Convert());

            foreach (FbxObject material in scene.Objects.Where(o => o.Class == "Material"))
            {
                material.Properties.Remove(FbxMaterialWriter.ShaderFlags1Property);
                material.Properties.Remove(FbxMaterialWriter.ShaderFlags2Property);
            }

            NifItem sourceRoot = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifModel rebuilt = new FbxToNif(scene, new FbxToNifOptions
            {
                RootName = source.GetName(sourceRoot),
                Version = source.Version,
                UserVersion = source.UserVersion,
                LegendaryEdition = source.BSVersion < 100
            }).Convert(Db);

            int checked_ = 0;

            foreach (NifItem shape in rebuilt.Blocks.Where(b => rebuilt.BlockInherits(b, "NiAVObject")))
            {
                if (rebuilt.GetRef(shape, "Shader Property") is not { } shader) continue;
                if (rebuilt.FindItem(shader, "Shader Flags 2") is not { } two) continue;

                // The descriptor's own flag, not the presence of the conditional field:
                // `Vertex Colors` is conditional on this very flag and sits in the tree
                // either way, so asking whether it exists always answers yes.
                bool colours = rebuilt.FindItem(shape, "Vertex Desc") is { } desc
                    ? new BSVertexDesc(desc.Value.ToUInt64()).HasFlag(VertexFlags.Colors)
                    : rebuilt.GetRef(shape, "Data") is { } data
                      && rebuilt.GetUInt(data, "Has Vertex Colors") != 0;

                checked_++;

                Assert.Equal(colours, (two.Value.ToUInt() & (1u << 5)) != 0);
            }

            Assert.True(checked_ > 0 || !source.Blocks.Any(b => b.Name == "BSLightingShaderProperty"));
        }

        [Fact]
        public void EveryCollisionShapeSurvives()
        {
            // A container held a tree and the import returned the first leaf it found,
            // on the grounds that Havok rebuilds the tree. Havok is not here, so a
            // list of six boxes came back as one box: five sixths of the collision
            // gone, and the sixth exactly right.
            //
            // No fixture has a bhkListShape or a transform shape, so the containers
            // themselves are only exercised by the corpus sweep -- which is where the
            // loss showed up, and where fixing it took the divergent count from 121 to
            // 92. This guards the shapes the fixtures do have.
            NifModel source = Load("nifly/TestNifFile_Furniture_Col_SE.nif");

            var expected = source.Blocks
                .Where(b => source.BlockInherits(b, "bhkShape"))
                .GroupBy(b => b.Name)
                .ToDictionary(g => g.Key, g => g.Count());

            NifModel rebuilt = RoundTrip(source);

            foreach ((string shape, int count) in expected)
            {
                // MOPP trees are walked through rather than rebuilt: their code is
                // generated, and an empty wrapper cannot even be written.
                if (shape == "bhkMoppBvTreeShape")
                    continue;

                Assert.Equal(count, rebuilt.Blocks.Count(b => b.Name == shape));
            }
        }

        [Fact]
        public void ACollisionBodyKeepsItsPlacement()
        {
            // Two bugs met here. The export read the body's placement from the wrong
            // path and silently got nothing, so every collision body in every mesh
            // went out at the origin; and a body's transform is a world transform, so
            // writing it as a node's local transform displaces it by every bone above
            // it. A skeleton has both: 24 bodies, all deep in a bone chain.
            NifModel source = Load("xpmsse/skeleton_cow.nif");

            var expected = BodyPlacements(source);

            Assert.Equal(24, expected.Count);
            Assert.Contains(expected, e => Math.Abs(e.Value.Y) > 0.1f);

            var actual = BodyPlacements(RoundTrip(source));

            foreach ((string owner, NifVector4 placement) in expected)
            {
                NifVector4 got = Assert.Contains(owner, actual);

                // A tolerance rather than a rounding: these are metres, and the value
                // has been through a bone chain and back, so the question is how far
                // it moved rather than which decimal it lands on.
                float moved = Math.Abs(placement.X - got.X)
                              + Math.Abs(placement.Y - got.Y)
                              + Math.Abs(placement.Z - got.Z);

                Assert.True(
                    moved < 0.005f,
                    $"{owner} moved {moved:F4}m: "
                    + $"({placement.X:F4}, {placement.Y:F4}, {placement.Z:F4}) "
                    + $"became ({got.X:F4}, {got.Y:F4}, {got.Z:F4})");
            }
        }

        [Fact]
        public void ACollisionBodySitsWhereItBelongsInTheScene()
        {
            // The other half, and the one a NIF round trip cannot see: the body's
            // global placement in the exported scene has to be its NIF placement, or
            // the collision is drawn somewhere else entirely in a DCC tool.
            NifModel source = Load("xpmsse/skeleton_cow.nif");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            NifItem pelvis = source.Blocks.First(b => source.GetName(b) == "Pelvis");
            NifVector4 world = source.FindItem(
                source.GetRef(source.GetRef(pelvis, "Collision Object")!, "Body")!,
                @"Rigid Body Info\Translation")!.Value.Get<NifVector4>();

            FbxObject node = scene.Objects.First(o => o.Class == "Model" && o.Name == "Pelvis_rb");

            NifTransform global = FbxGlobalTransform.Of(scene, node);

            // Havok metres out to Skyrim units.
            Assert.Equal(world.X * ShapeTessellator.BhkScaleFactor, global.Translation.X, 1);
            Assert.Equal(world.Y * ShapeTessellator.BhkScaleFactor, global.Translation.Y, 1);
            Assert.Equal(world.Z * ShapeTessellator.BhkScaleFactor, global.Translation.Z, 1);
        }

                [Fact]
        public void ASkeletonComesBackASkeleton()
        {
            // A bhkBlendCollisionObject is not merely another collision class: the
            // BSXFlags calculation defines isSkeleton as having one, so rebuilding it
            // as a plain bhkCollisionObject changes what the engine thinks the file
            // is. The cow skeleton went from 0xC6 to 0x8A -- no ragdoll, no dynamic
            // bodies -- with its bones, its constraints and its shapes all intact.
            NifModel source = Load("xpmsse/skeleton_cow.nif");

            int blend = source.Blocks.Count(b => b.Name == "bhkBlendCollisionObject");

            Assert.NotEqual(0, blend);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(blend, rebuilt.Blocks.Count(b => b.Name == "bhkBlendCollisionObject"));

            // And the flags say so, which is the thing that actually matters.
            Assert.Equal(source.Calculate(), rebuilt.Calculate());

            // The blend object's gains come with it; a zero gain is a bone that does
            // not follow.
            NifItem after = rebuilt.Blocks.First(b => b.Name == "bhkBlendCollisionObject");

            Assert.Equal(1f, rebuilt.FindItem(after, "Heir Gain")!.Value.ToFloat(), 3);
            Assert.Equal(1f, rebuilt.FindItem(after, "Vel Gain")!.Value.ToFloat(), 3);
        }

                [Fact]
        public void ASkinKeepsItsBodySlots()
        {
            // A slot says which part of a body a partition is, and that is the whole
            // of the difference between the two skin instance classes. The importer
            // used to write every slot as zero -- the torso -- which is also what
            // ck-cmd does, in a branch that cannot run.
            NifModel source = Load("nifly/TestNifFile_Skinned_SE.nif");

            NifItem before = source.Blocks.First(b => b.Name == "BSDismemberSkinInstance");

            var expected = source.FindItem(before, "Partitions")!.Children
                .Select(p => (
                    Part: p.Children.First(c => c.Name == "Body Part").Value.ToUInt(),
                    Flags: p.Children.First(c => c.Name == "Part Flag").Value.ToUInt()))
                .ToList();

            // The fixture's slot is 32, so a rebuild that defaults to zero fails here
            // rather than passing by accident.
            Assert.Contains(expected, e => e.Part != 0);

            NifModel rebuilt = RoundTrip(source);

            NifItem after = rebuilt.Blocks.First(b => b.Name == "BSDismemberSkinInstance");

            Assert.Equal(
                expected,
                rebuilt.FindItem(after, "Partitions")!.Children
                    .Select(p => (
                        Part: p.Children.First(c => c.Name == "Body Part").Value.ToUInt(),
                        Flags: p.Children.First(c => c.Name == "Part Flag").Value.ToUInt())));
        }

        [Fact]
        public void ASkinKeepsTheInstanceClassItHad()
        {
            // Nothing about a mesh decides between these. A dismember instance carries
            // body-part slots -- what lets a cuirass hide the body under it -- and the
            // game ships 15,728 of those against 11,212 plain ones, with no version or
            // folder separating them. So it is carried, not guessed: rebuilding every
            // skin as the dismember form was the single largest difference across the
            // game's meshes.
            NifModel source = Load("nifly/TestNifFile_Skinned_SE.nif");

            NifItem before = source.Blocks.First(b => source.BlockInherits(b, "NiSkinInstance"));

            NifModel rebuilt = RoundTrip(source);

            NifItem after = rebuilt.Blocks.First(b => rebuilt.BlockInherits(b, "NiSkinInstance"));

            Assert.Equal(before.Name, after.Name);
        }

                [Fact]
        public void ADynamicShapeExportsWhereItActuallyIs()
        {
            // The bug this guards is not that the shape came back wrong -- it is that
            // it went out wrong. A dynamic shape keeps its positions in the buffer the
            // engine rewrites each frame, and the static entries beside them are zero,
            // so reading those collapsed the whole mesh onto the origin. Every count
            // was right, which is why nothing noticed.
            NifModel source = Load("nifly/TestNifFile_Skinned_Dynamic_SE.nif");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject geometry = scene.Objects.First(o => o.Class == "Geometry");
            MeshGeometry mesh = FbxMeshReader.Read(geometry, new FbxMeshReader.Options())!;

            Assert.NotEmpty(mesh.Vertices);
            Assert.Contains(mesh.Vertices, v => v.X != 0f || v.Y != 0f || v.Z != 0f);
        }

        [Fact]
        public void ADynamicShapeKeepsItsClassAndItsBuffer()
        {
            NifModel source = Load("nifly/TestNifFile_Skinned_Dynamic_SE.nif");

            NifItem before = source.Blocks.First(b => b.Name == "BSDynamicTriShape");

            var expected = source.FindItem(before, "Vertices")!.Children
                .Select(c => c.Value.Get<NifVector4>()).ToList();

            Assert.NotEmpty(expected);

            NifModel rebuilt = RoundTrip(source);

            // The fixture has two, and they come back as two.
            Assert.Equal(
                source.Blocks.Count(b => b.Name == "BSDynamicTriShape"),
                rebuilt.Blocks.Count(b => b.Name == "BSDynamicTriShape"));

            NifItem after = rebuilt.Blocks.First(b => b.Name == "BSDynamicTriShape");

            var actual = rebuilt.FindItem(after, "Vertices")!.Children
                .Select(c => c.Value.Get<NifVector4>()).ToList();

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].X, actual[i].X, 3);
                Assert.Equal(expected[i].Y, actual[i].Y, 3);
                Assert.Equal(expected[i].Z, actual[i].Z, 3);

                // The fourth component is carried rather than derived, so it is exact.
                Assert.Equal(expected[i].W, actual[i].W, 5);
            }
        }

                [Fact]
        public void AConstantTrackKeepsItsAbsentDataBlock()
        {
            // An interpolator holding a value and no data block is a real animation:
            // "this, for this whole sequence". The absence is the representation, so
            // writing a one-key data block instead would be a different animation that
            // happens to look the same.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            var constants = source.Blocks
                .Where(b => b.Name == "NiBoolInterpolator" && source.GetRef(b, "Data") is null)
                .Select(b => source.FindItem(b, "Value")!.Value.ToUInt())
                .ToList();

            Assert.NotEmpty(constants);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(
                constants,
                rebuilt.Blocks
                    .Where(b => b.Name == "NiBoolInterpolator" && rebuilt.GetRef(b, "Data") is null)
                    .Select(b => rebuilt.FindItem(b, "Value")!.Value.ToUInt()));
        }

        [Fact]
        public void AnAnimatedFileComesBackWithTheSameBlocks()
        {
            // The whole of it: a controller manager, three sequences, attached
            // controllers with blend interpolators, a particle system with its
            // modifiers, its shader and the controller that runs it, and two tracks
            // that hold a constant.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");
            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(
                source.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal),
                rebuilt.Blocks.Select(b => b.Name).OrderBy(x => x, StringComparer.Ordinal));
        }

                [Fact]
        public void AParticleSystemKeepsItsShader()
        {
            // A particle system is a shape: it has a shader and an alpha property like
            // any other, and they are what the effect actually looks like. It has no
            // geometry for them to hang off, which is why the geometry path never saw
            // them and they were dropped.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            NifItem system = Assert.Single(source.Blocks, b => b.Name == "NiParticleSystem");

            string texture = source.GetString(source.GetRef(system, "Shader Property")!, "Source Texture");

            Assert.NotEqual(string.Empty, texture);

            NifModel rebuilt = RoundTrip(source);
            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiParticleSystem");

            Assert.NotNull(rebuilt.GetRef(after, "Alpha Property"));
            Assert.Equal(texture, rebuilt.GetString(rebuilt.GetRef(after, "Shader Property")!, "Source Texture"));
        }

        [Fact]
        public void ASequenceThatOnlyHidesSomethingIsStillASequenceEntry()
        {
            // A track can hold one value for a whole sequence rather than keys. An
            // effect's "loop" sequence hides a mesh outright -- a NiBoolInterpolator
            // with no data and Value 0 -- and its "begin" sequence keys the same
            // property. Both are animation; the second sequence says something the
            // first does not.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "Flames");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "mLoop");
            model.FindItem(sequence, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(sequence, "Stop Time")?.Value.SetFloat(1f);

            NifItem entry = model
                .SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            NifItem interpolator = model.InsertBlock("NiBoolInterpolator");
            model.FindItem(interpolator, "Value")?.Value.SetCount(0);

            model.SetRef(entry, "Interpolator", interpolator);
            model.SetString(entry, "Node Name", "Flames");
            model.SetString(entry, "Controller Type", "NiVisController");

            AnimSequence read = Assert.Single(model.ReadAnimations());
            AnimTrack track = Assert.Single(read.Tracks);

            Assert.Equal(0f, Assert.Single(track.Properties).Constant);
        }

        [Fact]
        public void AnInterpolatorHoldingNeitherKeysNorAPoseValueIsStillABlock()
        {
            // nif.xml gives the pose value a default that means "none": INV_FLT for a
            // float, 2 for a bool, a bool being 0 or 1 and never 2. Reading the
            // sentinel as a constant would set every float it drives to 3.4e38.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "empty");

            NifItem entry = model
                .SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            model.SetRef(entry, "Interpolator", model.InsertBlock("NiFloatInterpolator"));
            model.SetString(entry, "Node Name", "root");
            model.SetString(entry, "Controller Type", "NiVisController");

            AnimTrack track = Assert.Single(Assert.Single(model.ReadAnimations()).Tracks);
            AnimProperty property = Assert.Single(track.Properties);

            // No constant is invented from the sentinel...
            Assert.Null(property.Constant);
            Assert.Empty(property.Curves.SelectMany(c => c.Keys));

            // ...and the block is still there to be rebuilt. The game's lightning
            // effects are full of these: a sequence that drives nothing, spelled out
            // rather than left out.
            Assert.True(property.Empty);
            Assert.Equal("NiFloatInterpolator", property.InterpolatorType);
        }

        [Fact]
        public void ATransformHeldForAWholeSequenceComesBack()
        {
            // A NiTransformInterpolator with no data block is not empty. Its own
            // Transform is the pose the node takes for the whole sequence -- the
            // transform equivalent of a constant scalar -- and reading only the data
            // block lost the interpolator and the controller that held it.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "Gem");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            NifItem controller = model.InsertBlock("NiTransformController");
            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");

            var translation = new NifVector3(1.5f, -2.25f, 3f);
            var rotation = new NifQuat(0.5f, 0.5f, 0.5f, 0.5f);

            model.FindItem(interpolator, @"Transform\Translation")!.Value.Set(translation);
            model.FindItem(interpolator, @"Transform\Rotation")!.Value.Set(rotation);
            model.FindItem(interpolator, @"Transform\Scale")!.Value.SetFloat(2f);

            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", node);
            model.SetRef(node, "Controller", controller);

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiTransformInterpolator");

            // No data block: the absence is the representation, and a one-key block
            // instead would be a different animation that happens to look the same.
            Assert.Null(rebuilt.GetRef(after, "Data"));

            NifVector3 backT = rebuilt.FindItem(after, @"Transform\Translation")!.Value.Get<NifVector3>();
            NifQuat backR = rebuilt.FindItem(after, @"Transform\Rotation")!.Value.Get<NifQuat>();

            Assert.Equal(translation.X, backT.X, 4);
            Assert.Equal(translation.Y, backT.Y, 4);
            Assert.Equal(translation.Z, backT.Z, 4);
            Assert.Equal(rotation.W, backR.W, 4);
            Assert.Equal(rotation.X, backR.X, 4);
            Assert.Equal(2f, rebuilt.FindItem(after, @"Transform\Scale")!.Value.ToFloat(), 4);

            Assert.Single(rebuilt.Blocks, b => b.Name == "NiTransformController");
        }

        [Fact]
        public void ACameraIsStillACamera()
        {
            // A NiCamera is a node in the scene graph and not a NiNode in the schema:
            // it inherits NiAVObject directly and has no Children of its own. Reading
            // the carried class against NiNode rejected it, so every camera came back
            // as a plain node with its frustum, viewport and LOD adjust gone.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem camera = model.InsertBlock("NiCamera");
            model.SetString(camera, "Name", "Camera01");
            model.FindItem(camera, "Frustum Near")?.Value.SetFloat(5f);
            model.FindItem(camera, "Frustum Far")?.Value.SetFloat(2500f);
            model.FindItem(camera, "LOD Adjust")?.Value.SetFloat(1.5f);

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(camera));

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiCamera");

            // The class, and the fields the class is for.
            Assert.Equal(5f, rebuilt.FindItem(after, "Frustum Near")!.Value.ToFloat(), 4);
            Assert.Equal(2500f, rebuilt.FindItem(after, "Frustum Far")!.Value.ToFloat(), 4);
            Assert.Equal(1.5f, rebuilt.FindItem(after, "LOD Adjust")!.Value.ToFloat(), 4);
        }

        [Fact]
        public void AnUnnamedNodeStaysUnnamedAndKeepsItsAnimation()
        {
            // The game's cameras have no name at all. FBX has no anonymous object, so
            // the export falls back to the class name -- and that name has to be what
            // the animation binds by, or the controller has no node to hang on, and
            // it has to be undone on the way back, or the node comes back called
            // "NiCamera".
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem camera = model.InsertBlock("NiCamera");
            model.SetString(camera, "Name", string.Empty);

            NifItem controller = model.InsertBlock("BSFrustumFOVController");
            NifItem interpolator = model.InsertBlock("NiFloatInterpolator");
            NifItem data = model.InsertBlock("NiFloatData");

            NifItem keys = model.SetArraySize(data, @"Data\Num Keys", @"Data\Keys", 2)!;
            keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
            keys.Children[0].Children.First(c => c.Name == "Value").Value.SetFloat(60f);
            keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Value").Value.SetFloat(30f);

            model.SetRef(interpolator, "Data", data);
            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", camera);
            model.SetRef(camera, "Controller", controller);

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(camera));

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiCamera");

            Assert.Equal(string.Empty, rebuilt.GetName(after));
            Assert.Single(rebuilt.Blocks, b => b.Name == "BSFrustumFOVController");

            // On the camera's chain, not merely present.
            Assert.Equal("BSFrustumFOVController", rebuilt.GetRef(after, "Controller")!.Name);
        }

        [Fact]
        public void AShaderThatIsNotALightingShaderRidesUnderItsOwnName()
        {
            // The flat carrier was written for BSEffectShaderProperty, but nothing
            // about it is particular to one class. A BSWaterShaderProperty shares no
            // more with a lighting shader than an effect shader does, and was being
            // dropped outright -- the shape came back with no shader at all.
            // A real mesh, so the shape survives on its own merits and the test is
            // about the shader.
            NifModel model = Load("multi_material_cube.nif");

            NifItem shape = model.Blocks.First(b => b.Name == "NiTriShape");

            NifItem shader = model.InsertBlock("BSWaterShaderProperty");
            model.SetString(shader, "Name", "water");
            model.FindItem(shader, "Shader Flags 1")?.Value.SetCount(0x80000000);

            model.SetRef(shape, "Shader Property", shader);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSWaterShaderProperty");

            Assert.Equal(0x80000000u, rebuilt.GetUInt(after, "Shader Flags 1"));

            // Hung on a shape, not merely present.
            Assert.Contains(
                rebuilt.Blocks.Where(b => b.Name == "NiTriShape"),
                b => rebuilt.GetRef(b, "Shader Property") == after);
        }

        [Fact]
        public void AControllerASequenceNamesIsNotAlsoCarriedStructurally()
        {
            // Holding no field called "Interpolator" is not enough to make a
            // controller structural. A BSProceduralLightningController holds nine
            // interpolators, none of them called that, and every one is driven from a
            // sequence -- so the animation route rebuilds it, and carrying it as
            // structure too gave every lightning node two.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            var claimed = NifAnimAccess.SequencedControllers(source);

            Assert.NotEmpty(claimed);

            var scene = new FbxScene(new NifToFbx(source).Convert());

            // No node carries a structural controller that a sequence already names.
            foreach (FbxObject node in scene.Objects.Where(o => o.Class == "Model"))
            {
                string count = node.Properties.GetString(FbxNodeControllers.CountProperty);

                if (count.Length == 0)
                    continue;

                for (int i = 0; i < int.Parse(count); i++)
                {
                    string type = node.Properties.GetString($"{FbxNodeControllers.Prefix}{i}_type");

                    Assert.DoesNotContain(claimed, c => c.Name == type);
                }
            }
        }

        [Fact]
        public void AShapeWithNoVerticesIsStillAShape()
        {
            // nif.xml says it outright: a BSProceduralLightningController is "paired
            // with dummy TriShapes", empty shapes the engine generates lightning into
            // at runtime. The game's staff bolts and rune projectiles are built from
            // them, and exporting nothing lost the shape, its shader and its alpha
            // property -- half the blocks in those files.
            NifModel source = Load("multi_material_cube.nif");

            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifItem dummy = source.InsertBlock("BSTriShape");
            source.SetString(dummy, "Name", "object0");

            NifItem shader = source.InsertBlock("BSEffectShaderProperty");
            source.SetString(shader, "Source Texture", @"textures\effects\bolt.dds");
            source.SetRef(dummy, "Shader Property", shader);

            var children = source.GetRefArray(root, "Children").ToList();
            children.Add(dummy);

            if (source.SetArraySize(root, "Num Children", "Children", children.Count) is { } array)
            {
                for (int i = 0; i < children.Count; i++)
                    array.Children[i].Value.SetLink(source.IndexOf(children[i]));
            }

            NifModel rebuilt = RoundTrip(source);

            // The fixture is an LE file, so the shape comes back as the class that
            // edition has -- what matters is that it comes back at all.
            NifItem after = Assert.Single(rebuilt.Blocks, b => rebuilt.GetName(b) == "object0");

            Assert.True(rebuilt.Database.Inherits(after.Name, "NiTriBasedGeom"));

            // Still empty: a data block with nothing in it, not a mesh invented for it.
            Assert.Equal(0u, rebuilt.GetUInt(rebuilt.GetRef(after, "Data")!, "Num Vertices"));

            NifItem back = rebuilt.GetRef(after, "Shader Property")!;

            Assert.Equal("BSEffectShaderProperty", back.Name);
            Assert.Equal(@"textures\effects\bolt.dds", rebuilt.GetString(back, "Source Texture"));
        }

        [Fact]
        public void AStructuralControllerBringsItsInterpolatorsWithIt()
        {
            // The flat carrier moves fields, not references, which is right for almost
            // everything -- a link is a block index and means nothing once exported.
            // A BSProceduralLightningController is the exception: it holds nine
            // interpolators under names of its own, and when no sequence drives them
            // they hang off the controller and nothing else would bring them back.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "ProceduralGeometry");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            NifItem controller = model.InsertBlock("BSProceduralLightningController");
            model.FindItem(controller, "Length")?.Value.SetFloat(512f);
            model.SetRef(controller, "Target", node);
            model.SetRef(node, "Controller", controller);

            NifItem interpolator = model.InsertBlock("NiFloatInterpolator");
            NifItem data = model.InsertBlock("NiFloatData");

            NifItem keys = model.SetArraySize(data, @"Data\Num Keys", @"Data\Keys", 2)!;
            keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
            keys.Children[0].Children.First(c => c.Name == "Value").Value.SetFloat(3f);
            keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Value").Value.SetFloat(9f);

            model.SetRef(interpolator, "Data", data);
            model.SetRef(controller, "Interpolator 9: Arc Offset", interpolator);

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSProceduralLightningController");

            Assert.Equal(512f, rebuilt.FindItem(after, "Length")!.Value.ToFloat(), 3);

            // In the slot it came from, not merely present somewhere.
            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiFloatInterpolator");

            Assert.Equal(back, rebuilt.GetRef(after, "Interpolator 9: Arc Offset"));

            // And its keys travelled with it -- the codec sizes the array from the
            // count field it read a moment before.
            NifItem backData = Assert.Single(rebuilt.Blocks, b => b.Name == "NiFloatData");

            Assert.Equal(2u, rebuilt.GetUInt(backData, @"Data\Num Keys"));
            Assert.Equal(
                9f,
                rebuilt.FindItem(backData, @"Data\Keys")!.Children[1]
                    .Children.First(c => c.Name == "Value").Value.ToFloat(),
                3);
        }

        [Fact]
        public void AnInterpolatorThisLayerCannotReadIsCarriedWhole()
        {
            // A NiPathInterpolator walks a node along a curve. Nothing about that is a
            // curve on an FBX property, so the animation layer declines it -- and it
            // used to fall between the two routes: not animation, because the
            // interpolator could not be read, and not structural either, because the
            // controller *had* one. Both routes passed and the controller vanished.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "Fish");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            NifItem controller = model.InsertBlock("NiTransformController");
            NifItem interpolator = model.InsertBlock("NiPathInterpolator");

            model.FindItem(interpolator, "Max Bank Angle")!.Value.SetFloat(0.75f);
            model.FindItem(interpolator, "Follow Axis")!.Value.SetCount(2);
            model.SetRef(interpolator, "Path Data", model.InsertBlock("NiPosData"));

            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", node);
            model.SetRef(node, "Controller", controller);

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPathInterpolator");

            Assert.Equal(0.75f, rebuilt.FindItem(after, "Max Bank Angle")!.Value.ToFloat(), 4);
            Assert.Equal(2u, rebuilt.GetUInt(after, "Follow Axis"));
            Assert.NotNull(rebuilt.GetRef(after, "Path Data"));

            // On the node's chain, through the controller that held it.
            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiTransformController");

            Assert.Equal(after, rebuilt.GetRef(back, "Interpolator"));
        }

        [Fact]
        public void ACylinderComesBackACylinder()
        {
            // ck-cmd's recursive_convert has no bhkCylinderShape case at all, so a
            // body whose shape is one leaves with no geometry and the collision object
            // above it is lost with it. The game ships them.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem body = model.InsertBlock("bhkRigidBody");
            NifItem collision = model.InsertBlock("bhkCollisionObject");

            model.SetRef(collision, "Body", body);
            model.SetRef(collision, "Target", root);
            model.SetRef(root, "Collision Object", collision);

            NifItem cylinder = model.InsertBlock("bhkCylinderShape");
            model.FindItem(cylinder, "Vertex A")!.Value.Set(new NifVector4(0f, 0f, -1f, 0.25f));
            model.FindItem(cylinder, "Vertex B")!.Value.Set(new NifVector4(0f, 0f, 1f, 0.25f));
            model.FindItem(cylinder, "Cylinder Radius")!.Value.SetFloat(0.25f);

            model.SetRef(body, "Shape", cylinder);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "bhkCylinderShape");

            NifVector4 a = rebuilt.FindItem(after, "Vertex A")!.Value.Get<NifVector4>();
            NifVector4 b = rebuilt.FindItem(after, "Vertex B")!.Value.Get<NifVector4>();

            // The ends are the discs themselves, not a radius short of them: reading a
            // cylinder as a capsule would bring these back at -0.75 and 0.75.
            Assert.Equal(-1f, a.Z, 2);
            Assert.Equal(1f, b.Z, 2);
            Assert.Equal(0.25f, rebuilt.FindItem(after, "Cylinder Radius")!.Value.ToFloat(), 2);

            // And the body above it survived, which is the whole point.
            Assert.Single(rebuilt.Blocks, x => x.Name == "bhkCollisionObject");
        }

        [Theory]
        [InlineData("bhkTransformShape")]
        [InlineData("bhkConvexTransformShape")]
        public void ATransformShapeKeepsThePlacementItIsFor(string type)
        {
            // A transform shape is the transform. `bhkBoxShape` and `bhkSphereShape`
            // have no centre of their own — the import fits one and throws it away,
            // because the block cannot hold it — so wrapping a box in a transform shape
            // is the only way the game puts collision anywhere but the body's origin.
            //
            // Nothing read or wrote that matrix in either direction: the export emitted
            // an identity node and the import never set the field. Every off-centre box
            // in the game came back at the origin, so its collision stood where the
            // object was not, and no count of blocks could see it — the shape is there,
            // the class is right, only the placement is gone.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem body = model.InsertBlock("bhkRigidBody");
            NifItem collision = model.InsertBlock("bhkCollisionObject");

            model.SetRef(collision, "Body", body);
            model.SetRef(collision, "Target", root);
            model.SetRef(root, "Collision Object", collision);

            NifItem box = model.InsertBlock("bhkBoxShape");
            model.FindItem(box, "Dimensions")!.Value.Set(new NifVector3(0.5f, 0.25f, 0.125f));

            NifItem transform = model.InsertBlock(type);
            model.SetRef(transform, "Shape", box);
            model.SetRef(body, "Shape", transform);

            // A metre and a half along x, which is about 105 Skyrim units — far enough
            // that a shape which lost it is unmistakably in the wrong place.
            model.FindItem(transform, "Transform")!.Value.Set(new NifMatrix44
            {
                M11 = 1f, M22 = 1f, M33 = 1f,
                M41 = 1.5f, M42 = 0f, M43 = 0f
            });

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == type);
            NifMatrix44 m = rebuilt.FindItem(after, "Transform")!.Value.Get<NifMatrix44>();

            Assert.Equal(1.5f, m.M41, 3);
            Assert.Equal(0f, m.M42, 3);
            Assert.Equal(0f, m.M43, 3);

            // And the rotation is still the identity it was, rather than whatever a
            // dropped matrix decays to.
            Assert.Equal(1f, m.M11, 3);
            Assert.Equal(1f, m.M22, 3);
            Assert.Equal(1f, m.M33, 3);
        }

        [Fact]
        public void TwoNodesWithOneNameAreStillTwoNodes()
        {
            // A NIF is free to give two nodes the same name, and impactfrosticestorm
            // does: five called AddOnNode66, each with a transform controller of its
            // own. A track binds by name, so keying on the shared one kept the first
            // controller and dropped four.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            var nodes = new List<NifItem>();

            for (int i = 0; i < 3; i++)
            {
                NifItem node = model.InsertBlock("NiNode");
                model.SetString(node, "Name", "AddOnNode66");
                nodes.Add(node);

                NifItem controller = model.InsertBlock("NiTransformController");
                NifItem interpolator = model.InsertBlock("NiTransformInterpolator");
                NifItem data = model.InsertBlock("NiTransformData");

                NifItem keys = model.SetArraySize(
                    data, @"Translations\Num Keys", @"Translations\Keys", 2)!;

                for (int k = 0; k < 2; k++)
                {
                    keys.Children[k].Children.First(c => c.Name == "Time").Value.SetFloat(k);
                    keys.Children[k].Children.First(c => c.Name == "Value").Value
                        .Set(new NifVector3(i * 10 + k, 0f, 0f));
                }

                model.SetRef(interpolator, "Data", data);
                model.SetRef(controller, "Interpolator", interpolator);
                model.SetRef(controller, "Target", node);
                model.SetRef(node, "Controller", controller);
            }

            if (model.SetArraySize(root, "Num Children", "Children", nodes.Count) is { } children)
            {
                for (int i = 0; i < nodes.Count; i++)
                    children.Children[i].Value.SetLink(model.IndexOf(nodes[i]));
            }

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            // Three controllers, not one.
            Assert.Equal(3, rebuilt.Blocks.Count(b => b.Name == "NiTransformController"));

            // And all three nodes still carry the name they had, not the numbered one
            // the FBX object needed.
            Assert.Equal(
                3,
                rebuilt.Blocks.Count(b => b.Name == "NiNode" && rebuilt.GetName(b) == "AddOnNode66"));
        }

        [Fact]
        public void AnInterpolatorsPointerIsNotFollowed()
        {
            // The carrier that brings a structural controller's interpolators across
            // follows references two levels down. A pointer is not a reference: a
            // NiLookAtInterpolator's Look At names the node it aims at, and following
            // it carried a copy of that node, which came back as a second node
            // attached to nothing.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem aimed = model.InsertBlock("NiNode");
            model.SetString(aimed, "Name", "Target");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "Watcher");

            if (model.SetArraySize(root, "Num Children", "Children", 2) is { } children)
            {
                children.Children[0].Value.SetLink(model.IndexOf(aimed));
                children.Children[1].Value.SetLink(model.IndexOf(node));
            }

            NifItem controller = model.InsertBlock("NiTransformController");
            NifItem interpolator = model.InsertBlock("NiLookAtInterpolator");

            model.FindItem(interpolator, "Look At")?.Value.SetLink(model.IndexOf(aimed));
            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", node);
            model.SetRef(node, "Controller", controller);

            model.SetRoots([root]);

            int before = model.Blocks.Count(b => b.Name == "NiNode");

            NifModel rebuilt = RoundTrip(model);

            // The interpolator came across; the node it aims at did not come twice.
            Assert.Single(rebuilt.Blocks, b => b.Name == "NiLookAtInterpolator");
            Assert.Equal(before, rebuilt.Blocks.Count(b => b.Name == "NiNode"));
        }

        [Fact]
        public void AStripsShapeKeepsItsSeams()
        {
            // One bhkNiTriStripsShape can reference several NiTriStripsData blocks --
            // whprison02 has two shapes with two each -- and FBX has one mesh per
            // node, so merging them lost where the seams were and rebuilt one block
            // where there were two.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem body = model.InsertBlock("bhkRigidBody");
            NifItem collision = model.InsertBlock("bhkCollisionObject");

            model.SetRef(collision, "Body", body);
            model.SetRef(collision, "Target", root);
            model.SetRef(root, "Collision Object", collision);

            NifItem shape = model.InsertBlock("bhkNiTriStripsShape");
            var blocks = new List<NifItem>();

            // Two data blocks, two triangles each, at different heights so the merge
            // and the split can be told apart.
            for (int part = 0; part < 2; part++)
            {
                NifItem data = model.InsertBlock("NiTriStripsData");

                model.FindItem(data, "Num Vertices")!.Value.SetCount(4);
                model.FindItem(data, "Has Vertices")!.Value.SetCount(1);

                NifItem vertices = model.FindItem(data, "Vertices")!;
                vertices.InvalidateConditionsRecursive();
                model.UpdateArraySize(vertices);

                vertices.Children[0].Value.Set(new NifVector3(0f, 0f, part));
                vertices.Children[1].Value.Set(new NifVector3(1f, 0f, part));
                vertices.Children[2].Value.Set(new NifVector3(1f, 1f, part));
                vertices.Children[3].Value.Set(new NifVector3(0f, 1f, part));

                model.FindItem(data, "Num Triangles")!.Value.SetCount(2);
                model.FindItem(data, "Num Strips")!.Value.SetCount(1);
                model.FindItem(data, "Has Points")!.Value.SetCount(1);

                NifItem lengths = model.SetArraySize(data, "Num Strips", "Strip Lengths", 1)!;
                lengths.Children[0].Value.SetCount(4);

                NifItem points = model.FindItem(data, "Points")!;
                points.InvalidateConditionsRecursive();
                model.UpdateArraySize(points);
                model.UpdateArraySize(points.Children[0]);

                for (int i = 0; i < 4; i++)
                    points.Children[0].Children[i].Value.SetCount((uint)i);

                blocks.Add(data);
            }

            if (model.SetArraySize(shape, "Num Strips Data", "Strips Data", 2) is { } refs)
            {
                for (int i = 0; i < 2; i++)
                    refs.Children[i].Value.SetLink(model.IndexOf(blocks[i]));
            }

            model.SetArraySize(shape, "Num Filters", "Filters", 2);
            model.SetRef(body, "Shape", shape);

            NifModel rebuilt = RoundTrip(model);

            Assert.Single(rebuilt.Blocks, b => b.Name == "bhkNiTriStripsShape");

            // Two blocks back, not one merged one.
            Assert.Equal(2, rebuilt.Blocks.Count(b => b.Name == "NiTriStripsData"));

            NifItem after = rebuilt.Blocks.First(b => b.Name == "bhkNiTriStripsShape");

            Assert.Equal(2, rebuilt.GetRefArray(after, "Strips Data").Count());

            // And each holds its own four corners, with indices local to it.
            foreach (NifItem data in rebuilt.GetRefArray(after, "Strips Data"))
                Assert.Equal(4u, rebuilt.GetUInt(data, "Num Vertices"));
        }

        [Fact]
        public void APosedTransformSurvivesAnimatedPropertiesOnTheSameNode()
        {
            // A track carries the node's transform and its properties together. Asking
            // whether *any* of its curves had keys said "this transform is animated"
            // about a node whose transform is a pose and whose visibility is what
            // moves -- and wrote an empty NiTransformData for a transform that has
            // none. The blacksmith's forge marker has exactly one such node.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "MagicNode");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            // A posed transform: no data block, a real transform in the interpolator.
            NifItem transform = model.InsertBlock("NiTransformController");
            NifItem posed = model.InsertBlock("NiTransformInterpolator");

            model.FindItem(posed, @"Transform\Translation")!.Value.Set(new NifVector3(0.5f, -4.25f, 8.75f));
            model.FindItem(posed, @"Transform\Scale")!.Value.SetFloat(1f);
            model.SetRef(transform, "Interpolator", posed);
            model.SetRef(transform, "Target", node);
            model.SetRef(node, "Controller", transform);

            // And a visibility controller on the same node that *is* keyed.
            NifItem visibility = model.InsertBlock("NiVisController");
            NifItem boolInterpolator = model.InsertBlock("NiBoolInterpolator");
            NifItem boolData = model.InsertBlock("NiBoolData");

            NifItem keys = model.SetArraySize(boolData, @"Data\Num Keys", @"Data\Keys", 2)!;
            keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
            keys.Children[0].Children.First(c => c.Name == "Value").Value.SetCount(1);
            keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Value").Value.SetCount(0);

            model.SetRef(boolInterpolator, "Data", boolData);
            model.SetRef(visibility, "Interpolator", boolInterpolator);
            model.SetRef(visibility, "Target", node);
            model.SetRef(transform, "Next Controller", visibility);

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "NiTransformInterpolator");

            // Still a pose: no data block invented for it.
            Assert.Null(rebuilt.GetRef(after, "Data"));
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiTransformData");

            Assert.Equal(
                0.5f, rebuilt.FindItem(after, @"Transform\Translation")!.Value.Get<NifVector3>().X, 3);

            // And the visibility keys still came across.
            Assert.Single(rebuilt.Blocks, b => b.Name == "NiBoolData");
        }

        [Fact]
        public void APlaneShapeComesBackAPlane()
        {
            // A bhkPlaneShape is an infinite plane with a box saying which part of it
            // is real -- what the game puts under water and under a fish egg cluster.
            // ck-cmd converts none, so it tessellated to nothing and the body above it
            // was lost.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("BSFadeNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem body = model.InsertBlock("bhkRigidBody");
            NifItem collision = model.InsertBlock("bhkCollisionObject");

            model.SetRef(collision, "Body", body);
            model.SetRef(collision, "Target", root);
            model.SetRef(root, "Collision Object", collision);

            NifItem plane = model.InsertBlock("bhkPlaneShape");

            model.FindItem(plane, "Plane Normal")!.Value.Set(new NifVector3(0f, 0f, 1f));
            model.FindItem(plane, "Plane Constant")!.Value.SetFloat(2f);
            model.FindItem(plane, "AABB Center")!.Value.Set(new NifVector4(0f, 0f, 2f, 0f));
            model.FindItem(plane, "AABB Half Extents")!.Value.Set(new NifVector4(3f, 4f, 0f, 0f));

            model.SetRef(body, "Shape", plane);

            NifModel rebuilt = RoundTrip(model);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "bhkPlaneShape");

            NifVector3 normal = rebuilt.FindItem(after, "Plane Normal")!.Value.Get<NifVector3>();

            // The plane it was: facing the same way, the same distance out.
            Assert.Equal(1f, MathF.Abs(normal.Z), 2);
            Assert.Equal(2f, MathF.Abs(rebuilt.FindItem(after, "Plane Constant")!.Value.ToFloat()), 2);

            NifVector4 half = rebuilt.FindItem(after, "AABB Half Extents")!.Value.Get<NifVector4>();

            Assert.Equal(3f, half.X, 1);
            Assert.Equal(4f, half.Y, 1);

            // And the body above it survived, which is the whole point.
            Assert.Single(rebuilt.Blocks, x => x.Name == "bhkCollisionObject");
        }

        /// <summary>
        /// A sequence entry whose interpolator this layer cannot model.
        /// </summary>
        /// <remarks>
        /// The counterpart of the attached case. `fxambwatersalmon01b` hangs a
        /// NiPathInterpolator on a controller, and `fxambwaterfallsalmon02` names six
        /// of them from three sequences with no attached controller at all -- so both
        /// routes have to be able to carry one, and neither could.
        /// </remarks>
        [Fact]
        public void ASequenceKeepsAnInterpolatorThisLayerCannotModel()
        {
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem node = model.InsertBlock("NiNode");
            model.SetString(node, "Name", "FishController01");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(node));

            NifItem data = model.InsertBlock("NiPosData");
            NifItem keys = model.SetArraySize(data, @"Data\Num Keys", @"Data\Keys", 2)!;

            keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
            keys.Children[0].Children.First(c => c.Name == "Value").Value.Set(new NifVector3(1f, 2f, 3f));
            keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Value").Value.Set(new NifVector3(4f, 5f, 6f));

            // Two sequences naming one path, which is what the waterfall does: the
            // same fish path played three times over.
            var interpolators = new List<NifItem>();

            for (int i = 0; i < 2; i++)
            {
                NifItem interpolator = model.InsertBlock("NiPathInterpolator");

                model.FindItem(interpolator, "Max Bank Angle")!.Value.SetFloat(0.5f);
                model.SetRef(interpolator, "Path Data", data);
                interpolators.Add(interpolator);

                NifItem sequence = model.InsertBlock("NiControllerSequence");
                model.SetString(sequence, "Name", $"swim{i}");
                model.FindItem(sequence, "Start Time")?.Value.SetFloat(0f);
                model.FindItem(sequence, "Stop Time")?.Value.SetFloat(1f);

                NifItem entry = model
                    .SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                    .Children[0];

                model.SetRef(entry, "Interpolator", interpolator);
                model.SetString(entry, "Node Name", "FishController01");
                model.SetString(entry, "Controller Type", "NiTransformController");
            }

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            // Both interpolators back, with their own fields.
            Assert.Equal(2, rebuilt.Blocks.Count(b => b.Name == "NiPathInterpolator"));

            foreach (NifItem back in rebuilt.Blocks.Where(b => b.Name == "NiPathInterpolator"))
                Assert.Equal(0.5f, rebuilt.FindItem(back, "Max Bank Angle")!.Value.ToFloat(), 3);

            // And the path they shared is still one block, not two.
            NifItem backData = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPosData");

            Assert.Equal(2u, rebuilt.GetUInt(backData, @"Data\Num Keys"));
            Assert.Equal(
                4f,
                rebuilt.FindItem(backData, @"Data\Keys")!.Children[1]
                    .Children.First(c => c.Name == "Value").Value.Get<NifVector3>().X,
                3);

            // Nothing was invented to own it: the sequence machinery drives these, not
            // a controller attached to the node.
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiBlendFloatInterpolator");
        }

        [Fact]
        public void TwoTracksThatSharedTheirKeysStillShareThem()
        {
            // Two interpolators can point at one NiFloatData, and the game's files do
            // it: dlc2scatteredembers has three data blocks for four interpolators.
            // Rebuilding each one's keys separately turns one block into two.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem data = model.InsertBlock("NiFloatData");
            NifItem keys = model.SetArraySize(data, @"Data\Num Keys", @"Data\Keys", 2)!;

            keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
            keys.Children[0].Children.First(c => c.Name == "Value").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
            keys.Children[1].Children.First(c => c.Name == "Value").Value.SetFloat(7f);

            // Two nodes, one controller each, both reading the same keys.
            var children = new List<NifItem>();

            for (int i = 0; i < 2; i++)
            {
                NifItem node = model.InsertBlock("NiNode");
                model.SetString(node, "Name", $"Ember{i}");
                children.Add(node);

                NifItem interpolator = model.InsertBlock("NiFloatInterpolator");
                model.SetRef(interpolator, "Data", data);

                NifItem controller = model.InsertBlock("NiFloatExtraDataController");
                model.SetString(controller, "Extra Data Name", "Glow");
                model.SetRef(controller, "Interpolator", interpolator);
                model.SetRef(controller, "Target", node);
                model.SetRef(node, "Controller", controller);
            }

            if (model.SetArraySize(root, "Num Children", "Children", children.Count) is { } array)
            {
                for (int i = 0; i < children.Count; i++)
                    array.Children[i].Value.SetLink(model.IndexOf(children[i]));
            }

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            Assert.Equal(2, rebuilt.Blocks.Count(b => b.Name == "NiFloatInterpolator"));

            // One data block, not two, and both interpolators reading it.
            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiFloatData");

            Assert.All(
                rebuilt.Blocks.Where(b => b.Name == "NiFloatInterpolator"),
                b => Assert.Equal(back, rebuilt.GetRef(b, "Data")));
        }

        [Fact]
        public void AnEntryForAMissingNodeKeepsItsControllerToo()
        {
            // A controller hangs on the thing it drives, and when that node is not in
            // the file there is nothing to hang it on -- so the game hangs it on
            // nothing. sprigganmatron holds two BSNiAlphaPropertyTestRefController
            // with no target, on no chain, reachable only because eleven sequence
            // entries each name them. Two, not one: they are the same class with no
            // ids, told apart solely by the node their entries name.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "LeavesScared");
            model.FindItem(sequence, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(sequence, "Stop Time")?.Value.SetFloat(1f);

            NifItem entries = model
                .SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 2)!;

            for (int i = 0; i < 2; i++)
            {
                NifItem controller = model.InsertBlock("BSNiAlphaPropertyTestRefController");
                model.SetRef(controller, "Interpolator", model.InsertBlock("NiBlendFloatInterpolator"));

                NifItem interpolator = model.InsertBlock("NiFloatInterpolator");
                NifItem data = model.InsertBlock("NiFloatData");

                NifItem keys = model.SetArraySize(data, @"Data\Num Keys", @"Data\Keys", 2)!;
                keys.Children[0].Children.First(c => c.Name == "Time").Value.SetFloat(0f);
                keys.Children[0].Children.First(c => c.Name == "Value").Value.SetFloat(0f);
                keys.Children[1].Children.First(c => c.Name == "Time").Value.SetFloat(1f);
                keys.Children[1].Children.First(c => c.Name == "Value").Value.SetFloat(1f);

                model.SetRef(interpolator, "Data", data);

                NifItem entry = entries.Children[i];
                model.SetRef(entry, "Interpolator", interpolator);
                model.SetRef(entry, "Controller", controller);

                // The node is not in this file, which is the whole point.
                model.SetString(entry, "Node Name", $"SprigganBodyLeaves01:{i}");
                model.SetString(entry, "Controller Type", "BSNiAlphaPropertyTestRefController");
                model.SetString(entry, "Property Type", "NiAlphaProperty");
            }

            NifModel rebuilt = RoundTrip(model);

            // Both entries back, in one sequence.
            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiControllerSequence");

            Assert.Equal(2, rebuilt.FindItem(back, "Controlled Blocks")!.Children.Count);

            // And two controllers, not one: the node name is what separates them.
            Assert.Equal(
                2,
                rebuilt.Blocks.Count(b => b.Name == "BSNiAlphaPropertyTestRefController"));

            // Hanging on nothing, as they were.
            Assert.All(
                rebuilt.Blocks.Where(b => b.Name == "BSNiAlphaPropertyTestRefController"),
                b => Assert.Null(rebuilt.GetRef(b, "Target")));
        }

        [Fact]
        public void ASequenceDoesNotStealANodesName()
        {
            // Every block has a name, and plenty share one with a node: a falmer
            // scorpion has a sequence called "back" and a bone called "back". The name
            // a track binds by is made unique, and numbering across every block let
            // the sequence take the plain name and pushed the bone to "back#1" -- so
            // each sequence's entry for it bound to a model that was not there, and
            // seven animations were dropped.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem bone = model.InsertBlock("NiNode");
            model.SetString(bone, "Name", "back");

            if (model.SetArraySize(root, "Num Children", "Children", 1) is { } children)
                children.Children[0].Value.SetLink(model.IndexOf(bone));

            // A sequence of the same name, written first so it would win the race.
            NifItem sequence = model.InsertBlock("NiControllerSequence");
            model.SetString(sequence, "Name", "back");
            model.FindItem(sequence, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(sequence, "Stop Time")?.Value.SetFloat(1f);

            NifItem entry = model
                .SetArraySize(sequence, "Num Controlled Blocks", "Controlled Blocks", 1)!
                .Children[0];

            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");
            NifItem data = model.InsertBlock("NiTransformData");

            NifItem keys = model.SetArraySize(data, @"Translations\Num Keys", @"Translations\Keys", 2)!;

            for (int k = 0; k < 2; k++)
            {
                keys.Children[k].Children.First(c => c.Name == "Time").Value.SetFloat(k);
                keys.Children[k].Children.First(c => c.Name == "Value").Value
                    .Set(new NifVector3(k * 5f, 0f, 0f));
            }

            model.SetRef(interpolator, "Data", data);
            model.SetRef(entry, "Interpolator", interpolator);
            model.SetString(entry, "Node Name", "back");
            model.SetString(entry, "Controller Type", "NiTransformController");

            model.SetRoots([root]);

            NifModel rebuilt = RoundTrip(model);

            // The bone keeps its own name, and the entry still finds it.
            Assert.Contains(
                rebuilt.Blocks,
                b => b.Name == "NiNode" && rebuilt.GetName(b) == "back");

            NifItem back = Assert.Single(rebuilt.Blocks, b => b.Name == "NiControllerSequence");
            NifItem rebuiltEntry = Assert.Single(rebuilt.FindItem(back, "Controlled Blocks")!.Children);

            Assert.Equal("back", rebuilt.GetString(rebuiltEntry, "Node Name"));
            Assert.Single(rebuilt.Blocks, b => b.Name == "NiTransformData");
        }

        [Fact]
        public void ANodeThatClaimsToBeGeometryIsStillANode()
        {
            // Geometry is built on the mesh path, from a mesh. A node that names a
            // shape class would arrive with no vertices to be one from, so the class
            // is refused rather than followed.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            var scene = new FbxScene(new NifToFbx(model).Convert());

            foreach (FbxObject node in scene.Objects.Where(o => o.Class == "Model"))
                node.Properties.SetUserString(FbxNodeType.Property, "BSTriShape");

            NifModel rebuilt = new FbxToNif(
                scene,
                new FbxToNifOptions { RootName = "root", Version = model.Version, UserVersion = model.UserVersion })
                .Convert(Db);

            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "BSTriShape");
        }

        [Fact]
        public void ANodeKeepsAControllerThatAnimatesNothing()
        {
            // The same case as a particle system's update switch, on an ordinary node.
            // A BSLagBoneController makes a bone trail behind the one above it by a
            // fixed amount -- a property of the skeleton, not of a timeline -- and it
            // holds no interpolator, so the animation layer cannot see it. Seven of
            // the sampled meshes lost one, every skeleton that had any.
            NifModel source = Load("xpmsse/skeleton_cow.nif");

            NifItem node = source.Blocks.First(
                b => b.Name == "NiNode" && source.GetName(b).Length > 0);

            NifItem lag = source.InsertBlock("BSLagBoneController");
            source.FindItem(lag, "Linear Velocity")?.Value.SetFloat(0.25f);
            source.FindItem(lag, "Maximum Distance")?.Value.SetFloat(4f);
            source.SetRef(lag, "Target", node);
            source.SetRef(node, "Controller", lag);

            NifModel rebuilt = RoundTrip(source);

            NifItem after = Assert.Single(rebuilt.Blocks, b => b.Name == "BSLagBoneController");

            // Its fields came with it, not merely its class.
            Assert.Equal(0.25f, rebuilt.FindItem(after, "Linear Velocity")!.Value.ToFloat(), 4);
            Assert.Equal(4f, rebuilt.FindItem(after, "Maximum Distance")!.Value.ToFloat(), 4);

            // And it is on the same node's chain, pointing back at it.
            NifItem host = rebuilt.Blocks.First(
                b => b.Name == "NiNode" && rebuilt.GetName(b) == source.GetName(node));

            Assert.Equal(host, rebuilt.GetRef(after, "Target"));
            Assert.Equal(after, rebuilt.GetRef(host, "Controller"));
        }

        [Fact]
        public void TheSequenceMachineryIsNotCarriedAsAStructuralController()
        {
            // A NiControllerManager holds no interpolator either, and it is *not* one
            // of these: it is the animation layer, rebuilt from the sequences. Carried
            // here as well it came back into files whose animation had been turned off.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            Assert.Contains(source.Blocks, b => b.Name == "NiControllerManager");

            var scene = new FbxScene(new NifToFbx(source, new NifToFbxOptions { ExportAnimation = false })
                .Convert());

            NifModel rebuilt = new FbxToNif(
                scene,
                new FbxToNifOptions
                {
                    RootName = source.GetName(
                        source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);

            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiControllerManager");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiMultiTargetTransformController");
        }

                [Fact]
        public void AParticleSystemKeepsTheControllerThatRunsIt()
        {
            // NiPSysUpdateCtlr holds no interpolator and no keys. It is not animation
            // -- it is the switch that makes the system run at all -- so the animation
            // layer cannot see it: that layer recognises a controller by what its
            // interpolator drives, and this one has none.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            Assert.Contains(source.Blocks, b => b.Name == "NiPSysUpdateCtlr");

            NifModel rebuilt = RoundTrip(source);

            NifItem update = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPSysUpdateCtlr");
            NifItem system = Assert.Single(rebuilt.Blocks, b => b.Name == "NiParticleSystem");

            // On the system's chain and pointing back at it, not merely present.
            Assert.Equal(system, rebuilt.GetRef(update, "Target"));

            var chain = new List<NifItem>();

            for (NifItem? c = rebuilt.GetRef(system, "Controller");
                 c is not null;
                 c = rebuilt.GetRef(c, "Next Controller"))
            {
                chain.Add(c);
            }

            Assert.Contains(update, chain);
        }

                [Fact]
        public void ASequencedControllerIsAttachedAndBlended()
        {
            // Attached controllers and sequences are two halves of one arrangement.
            // The controller hangs on what it drives and holds a blend interpolator --
            // the slot the manager mixes every playing sequence into -- while each
            // sequence holds its own interpolator with the keys and names that
            // controller. Rebuilding only the sequences leaves an animation with
            // nothing to apply it to.
            NifModel source = Load("nifly/TestNifFile_Animated_LE.nif");

            Assert.Contains(source.Blocks, b => b.Name == "NiControllerManager");

            NifModel rebuilt = RoundTrip(source);

            foreach (string type in new[]
                     {
                         "BSEffectShaderPropertyFloatController",
                         "NiPSysEmitterCtlr",
                         "NiBlendFloatInterpolator",
                         "NiBlendBoolInterpolator"
                     })
            {
                Assert.Equal(
                    source.Blocks.Count(b => b.Name == type),
                    rebuilt.Blocks.Count(b => b.Name == type));
            }

            // One controller serves all three sequences rather than one each.
            NifItem emitter = Assert.Single(rebuilt.Blocks, b => b.Name == "NiPSysEmitterCtlr");

            // Its two tracks go in different slots: nif.xml names them BirthRate and
            // EmitterActive, the second on Visibility Interpolator.
            Assert.Equal("NiBlendFloatInterpolator", rebuilt.GetRef(emitter, "Interpolator")!.Name);
            Assert.Equal("NiBlendBoolInterpolator", rebuilt.GetRef(emitter, "Visibility Interpolator")!.Name);
        }

                /// <summary>
        /// A node with a transform controller of its own, named by no sequence.
        /// </summary>
        /// <remarks>
        /// Built rather than loaded: no fixture has one. Every transform controller
        /// the fixtures carry belongs to a sequence, which is a different path — and
        /// which is why the standalone one could be dropped for the whole corpus
        /// without a single test noticing.
        /// </remarks>
        private static NifModel BuildMovingNode()
        {
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem data = model.InsertBlock("NiTransformData");

            // Translations is a KeyGroup, so its count and interpolation live inside
            // it rather than beside it.
            model.FindItem(data, @"Translations\Num Keys")!.Value.SetCount(2);
            data.InvalidateConditionsRecursive();
            model.FindItem(data, @"Translations\Interpolation")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Translations\Keys")!;
            model.UpdateArraySize(keys);

            for (int i = 0; i < 2; i++)
            {
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat(i);
                model.FindItem(keys.Children[i], "Value")!.Value.Set(new NifVector3(i * 10f, 0f, 0f));
            }

            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");
            model.SetRef(interpolator, "Data", data);

            NifItem controller = model.InsertBlock("NiTransformController");
            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", root);

            model.SetRef(root, "Controller", controller);
            model.SetRoots([root]);
            model.UpdateHeader();

            return model;
        }

        [Fact]
        public void AnInterpolatorKeepsItsExactClass()
        {
            // NiBoolTimelineInterpolator is a NiBoolInterpolator that, in nif.xml's
            // words, "ensures that keys have not been missed between two updates".
            // Rebuilding it as its base turns a track that cannot skip an event into
            // one that can, which shows up as an animation occasionally not firing.
            //
            // Built rather than loaded: no fixture has one, which is why 19 meshes in
            // an 800-mesh sample lost it without a test noticing.
            NifModel model = NifModel.CreateNew(Db);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");

            NifItem data = model.InsertBlock("NiBoolData");

            model.FindItem(data, @"Data\Num Keys")!.Value.SetCount(2);
            data.InvalidateConditionsRecursive();
            model.FindItem(data, @"Data\Interpolation")!.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(data, @"Data\Keys")!;
            model.UpdateArraySize(keys);

            for (int i = 0; i < 2; i++)
            {
                model.FindItem(keys.Children[i], "Time")!.Value.SetFloat(i);
                model.FindItem(keys.Children[i], "Value")!.Value.SetCount((uint)i);
            }

            NifItem interpolator = model.InsertBlock("NiBoolTimelineInterpolator");
            model.SetRef(interpolator, "Data", data);

            NifItem controller = model.InsertBlock("NiVisController");
            model.SetRef(controller, "Interpolator", interpolator);
            model.SetRef(controller, "Target", root);
            model.SetRef(root, "Controller", controller);

            model.SetRoots([root]);
            model.UpdateHeader();

            NifModel rebuilt = RoundTrip(model);

            Assert.Single(rebuilt.Blocks, b => b.Name == "NiBoolTimelineInterpolator");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiBoolInterpolator");
        }

        [Fact]
        public void ANodeThatMovesOnItsOwnKeepsMoving()
        {
            // A NiTransformController attached to a node and named by no sequence moves
            // the node itself. The export gathers controllers by what their
            // interpolator drives, and this one drives the node rather than a property
            // of it, so it fell through both paths and was dropped -- the largest
            // single cause of divergence across the game's meshes.
            NifModel source = BuildMovingNode();

            NifModel rebuilt = RoundTrip(source);

            foreach (string type in new[]
                     { "NiTransformController", "NiTransformInterpolator", "NiTransformData" })
            {
                Assert.Equal(
                    source.Blocks.Count(b => b.Name == type),
                    rebuilt.Blocks.Count(b => b.Name == type));
            }

            // And the keys came with it, rather than an empty controller.
            NifItem after = rebuilt.Blocks.First(b => b.Name == "NiTransformData");

            Assert.Equal(2u, rebuilt.GetUInt(after, @"Translations\Num Keys"));

            NifItem last = rebuilt.FindItem(after, @"Translations\Keys")!.Children[1];

            Assert.Equal(10f, last.Children.First(c => c.Name == "Value").Value.Get<NifVector3>().X, 2);
        }

                [Fact]
        public void StandaloneControllersComeBackStandalone()
        {
            // A controller no sequence names is attached to what it controls and runs
            // on its own. FBX has no way to say that -- every animation there belongs
            // to a stack -- so the export invents a sequence and the import has to
            // undo the invention. Writing it back as a real sequence puts a controller
            // manager, an object palette and a text key block into a file that had
            // none, and leaves the controllers pointing at nothing.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            Assert.DoesNotContain(source.Blocks, b => b.Name == "NiControllerManager");

            int controllers = source.Blocks.Count(b => b.Name == "BSEffectShaderPropertyFloatController");

            Assert.NotEqual(0, controllers);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(
                controllers,
                rebuilt.Blocks.Count(b => b.Name == "BSEffectShaderPropertyFloatController"));

            // And nothing was invented to hold them.
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiControllerManager");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiControllerSequence");
            Assert.DoesNotContain(rebuilt.Blocks, b => b.Name == "NiDefaultAVObjectPalette");

            // Each is on a shader property, with keys, rather than loose in the file.
            foreach (NifItem controller in rebuilt.Blocks.Where(
                         b => b.Name == "BSEffectShaderPropertyFloatController"))
            {
                Assert.NotNull(rebuilt.GetRef(controller, "Interpolator"));
            }
        }

                [Fact]
        public void AnEffectShaderLooksLikeItselfInTheScene()
        {
            // The es_ properties reimport perfectly on their own, which is what makes
            // a blank material easy to ship: nothing fails, and an artist opening the
            // file sees an untextured surface beside correctly textured ones.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            var scene = new FbxScene(new NifToFbx(source).Convert());

            FbxObject material = scene.Objects.First(
                o => o.Class == "Material" && FbxEffectShader.WasWritten(o));

            var connected = scene.PropertyConnectionsTo(material.Id).ToList();

            // Its own texture, on the channel a DCC tool renders from.
            (FbxObject texture, _) = Assert.Single(connected, c => c.Property == "DiffuseColor");

            Assert.Equal(
                source.GetString(
                    source.Blocks.First(b => b.Name == "BSEffectShaderProperty"), "Source Texture"),
                texture.Child("RelativeFilename")?.Properties.FirstOrDefault());
        }

                [Fact]
        public void EffectShadersSurviveWithTheirOwnFields()
        {
            // ck-cmd's FBX path drops these: its export casts the shader to
            // BSLightingShaderProperty and takes the null when that fails, and its
            // import only ever builds a lighting shader. Following it would lose every
            // glow, decal and magic effect in a file.
            NifModel source = Load("nifly/TestNifFile_OrderedNode_SE.nif");

            int shaders = source.Blocks.Count(b => b.Name == "BSEffectShaderProperty");

            Assert.NotEqual(0, shaders);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(shaders, rebuilt.Blocks.Count(b => b.Name == "BSEffectShaderProperty"));

            // An effect shader shares almost no fields with a lighting one, so the
            // check is on its own: its texture, its flags and its colour.
            NifItem before = source.Blocks.First(b => b.Name == "BSEffectShaderProperty");
            NifItem after = rebuilt.Blocks.First(b => b.Name == "BSEffectShaderProperty");

            foreach (string field in new[] { "Shader Flags 1", "Shader Flags 2", "Falloff Start Angle" })
                Assert.Equal(source.FindItem(before, field)!.Value.ToString(), rebuilt.FindItem(after, field)!.Value.ToString());

            Assert.Equal(source.GetString(before, "Source Texture"), rebuilt.GetString(after, "Source Texture"));
        }

                [Fact]
        public void BonesNamedLikeSkyrimsResolve()
        {
            // FBX names cannot hold a space or a bracket, so "NPC R Thigh [RThg]" goes
            // out as NPC_s_R_s_Thigh_s__ob_RThg_cb_ and has to be decoded on the way
            // back. Left encoded it matches no node, and because a skin whose bones all
            // fail to resolve is dropped whole, every Skyrim body part loses its
            // skinning -- with the mesh, the shader and the bones themselves all intact.
            NifModel source = Load("nifly/TestNifFile_LooseBlocks_SE.nif");

            Assert.Contains(source.Blocks, b => source.GetName(b).Contains('['));

            int partitions = source.Blocks.Count(b => b.Name == "NiSkinPartition");

            Assert.NotEqual(0, partitions);

            NifModel rebuilt = RoundTrip(source);

            Assert.Equal(partitions, rebuilt.Blocks.Count(b => b.Name == "NiSkinPartition"));
        }

                [Fact]
        public void ASkinnedSeShapeCarriesItsWeightsInTheVertex()
        {
            // SE reads a skinned mesh's weights from the vertex buffer, not from
            // NiSkinData. A shape with the skinning blocks but not these is fully
            // rigged in a NIF editor and rigid in game, which is as quiet as this
            // gets.
            NifModel source = Load("nifly/TestNifFile_Skinned_SE.nif");
            NifItem sourceShape = source.Blocks.First(b => source.BlockInherits(b, "BSTriShape"));

            ulong descriptor = source.FindItem(sourceShape, "Vertex Desc")!.Value.ToUInt64();

            NifModel rebuilt = RoundTrip(source);
            NifItem shape = rebuilt.Blocks.First(b => rebuilt.BlockInherits(b, "BSTriShape"));

            // Same layout, which for a skinned shape means the wider vertex: the
            // twelve bytes of weights and indices, and the bit announcing them.
            Assert.Equal(descriptor, rebuilt.FindItem(shape, "Vertex Desc")!.Value.ToUInt64());

            // Wherever the vertex buffer actually is. A skinned SE shape keeps nothing in
            // itself -- the vertices live in the skin partition -- which is how this
            // fixture is written and now how it comes back. This read the shape's own
            // array, which was populated only because the import put it in the wrong
            // place.
            NifItem vertices = VertexBuffer(rebuilt, shape);

            Assert.NotEmpty(vertices.Children);

            foreach (NifItem vertex in vertices.Children)
            {
                float total = rebuilt.FindItem(vertex, "Bone Weights")!
                    .Children.Sum(c => c.Value.ToFloat());

                // Every vertex is fully weighted. A vertex summing to less is one the
                // engine drags towards the origin.
                Assert.Equal(1f, total, 3);
            }
        }

                [Fact]
        public void TheCollisionObjectKeepsItsFlags()
        {
            // bhkCOFlags says how the body and its node keep in step: SET_LOCAL reads
            // the body transform as local, SYNC_ON_UPDATE follows the node when it is
            // animated. Rebuilding it as a bare ACTIVE leaves the collision the right
            // size and in roughly the right place, no longer tracking what it belongs
            // to.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceCollision = source.Blocks.First(b => b.Name == "bhkCollisionObject");

            uint expected = source.GetUInt(sourceCollision, "Flags");

            Assert.Equal(9u, expected);

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltCollision = rebuilt.Blocks.First(b => b.Name == "bhkCollisionObject");

            Assert.Equal(expected, rebuilt.GetUInt(rebuiltCollision, "Flags"));
        }

        [Fact]
        public void TheCollisionMaterialSurvives()
        {
            // Nothing in the tessellated triangles says wood rather than stone, and the
            // engine reads it for footstep sound and impact response.
            NifModel source = Load("generate_rb_box.nif");
            NifItem sourceShape = source.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(source, sourceShape));

            NifModel rebuilt = RoundTrip(source);
            NifItem rebuiltShape = rebuilt.Blocks.First(b => b.Name == "bhkBoxShape");

            Assert.Equal("SKY_HAV_MAT_WOOD", FbxCollisionMaterial.NameOf(rebuilt, rebuiltShape));
        }

        [Fact]
        public void TheShaderKeepsAnIdentityUvTransform()
        {
            // A zero UV scale does not fail loudly: it multiplies every texture
            // coordinate in the mesh to nothing.
            NifModel rebuilt = RoundTrip(Load("multi_material_cube.nif"));

            foreach (NifItem shader in rebuilt.Blocks.Where(b => b.Name == "BSLightingShaderProperty"))
                Assert.Equal(new NifVector2(1f, 1f), rebuilt.FindItem(shader, "UV Scale")!.Value.Get<NifVector2>());
        }

        [Fact]
        public void TheImporterCalculatesBsxFlags()
        {
            NifModel rebuilt = RoundTrip(Load("generate_rb_box.nif"));

            NifItem bsx = Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");

            Assert.Equal(rebuilt.Calculate(), rebuilt.GetUInt(bsx, "Integer Data"));
        }
    }
}
