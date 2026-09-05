using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Calculating <c>BSXFlags</c> from the block graph.
    /// </summary>
    /// <remarks>
    /// Every bit is a fact about the file — whether it animates, collides, is a
    /// skeleton, has one collision or many — so the value is derived rather than
    /// authored, and ck-cmd recalculates it on export. See `docs/bsxflags-spec.md`.
    ///
    /// The real check is against the game's own files, in
    /// <see cref="BsaCorpusTests"/>: of 1,780 vanilla meshes carrying a
    /// <c>BSXFlags</c>, 1,778 agree with this calculation. What is here is the
    /// individual rules, so a broken one says which.
    /// </remarks>
    public class BsxFlagsTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string folder, string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", folder, name), Db);

        private static uint Stored(NifModel model) =>
            model.GetUInt(model.Blocks.First(b => b.Name == "BSXFlags"), "Integer Data");

        /// <summary>
        /// Fixtures whose stored value the calculation is expected to reproduce.
        /// </summary>
        /// <remarks>
        /// Every fixture that carries a <c>BSXFlags</c> except the three in
        /// <see cref="TheDisagreementsAreTheOnesExpected"/>.
        /// </remarks>
        public static TheoryData<string, string> Agreeing()
        {
            var data = new TheoryData<string, string>();

            data.Add("", "generate_rb.nif");
            data.Add("", "generate_rb_box.nif");
            data.Add("", "generate_rb_sphere.nif");
            data.Add("", "multi_material_cube.nif");
            data.Add("nifly", "TestNifFile_Animated_LE.nif");
            data.Add("nifly", "TestNifFile_DeepGraph_SE.nif");
            data.Add("nifly", "TestNifFile_MultiBound_SE.nif");
            data.Add("nifly", "TestNifFile_OrderedNode_SE.nif");
            data.Add("nifly", "TestNifFile_RootNonZero.nif");
            data.Add("xpmsse", "skeleton_cow.nif");

            return data;
        }

        [Theory]
        [MemberData(nameof(Agreeing))]
        public void ReproducesWhatTheFileStores(string folder, string name)
        {
            NifModel model = Load(folder, name);

            NifItem bsx = Assert.Single(model.Blocks, b => b.Name == "BSXFlags");

            Assert.Equal(model.GetUInt(bsx, "Integer Data"), model.Calculate());
        }

        [Fact]
        public void TheDisagreementsAreTheOnesExpected()
        {
            // Two of nifly's fixtures are deliberately wrong -- they exist to be
            // fixed, which is what nifly uses them for -- so reproducing their stored
            // value would mean reproducing the fault.
            foreach (string name in new[]
                     {
                         "TestNifFile_FixBSXFlags_AddExtEmit.nif",
                         "TestNifFile_FixBSXFlags_RemoveExtEmit.nif"
                     })
            {
                NifModel broken = Load("nifly", name);
                NifItem stored = broken.Blocks.First(b => b.Name == "BSXFlags");

                Assert.NotEqual(broken.GetUInt(stored, "Integer Data"), broken.Calculate());
            }

            // The third is a real disagreement with the rule rather than with the
            // file: the furniture has a rigid body of quality MO_QUAL_MOVING, which
            // ck-cmd counts as dynamic, and the file does not set bit 6. The rule is
            // reproduced as ck-cmd wrote it rather than bent to fit one file; 1,778 of
            // 1,780 vanilla meshes agree with it.
            NifModel furniture = Load("nifly", "TestNifFile_Furniture_Col_SE.nif");

            Assert.Equal(0x8Au, furniture.GetUInt(
                furniture.Blocks.First(b => b.Name == "BSXFlags"), "Integer Data"));

            Assert.Equal(0xCAu, furniture.Calculate());
        }

        [Fact]
        public void ASkeletonIsARagdollWithDynamicBodies()
        {
            uint flags = Load("xpmsse", "skeleton_cow.nif").Calculate();

            // A blend collision object is what makes a file a skeleton, and a skeleton
            // is a ragdoll whether or not it has a ragdoll constraint.
            Assert.NotEqual(0u, flags & (1u << NifBsxFlags.Bit.Ragdoll));
            Assert.NotEqual(0u, flags & (1u << NifBsxFlags.Bit.Havok));

            // Every rigid body in a skeleton counts as dynamic, regardless of its
            // quality type.
            Assert.NotEqual(0u, flags & (1u << NifBsxFlags.Bit.DynamicBodies));
        }

        [Fact]
        public void ASkeletonIsNotAnimatedEvenWithControllers()
        {
            NifModel model = Load("xpmsse", "skeleton_cow.nif");

            // Bit 0 is about Gamebryo animation, and a skeleton's motion comes from
            // Havok instead. The rule suppresses it for skeletons and for meshes
            // skinned entirely to bones they do not contain.
            Assert.Equal(0u, model.Calculate() & (1u << NifBsxFlags.Bit.Animated));
        }

        [Fact]
        public void ControllersMakeAFileAnimated()
        {
            NifModel model = Load("nifly", "TestNifFile_Animated_LE.nif");

            Assert.Contains(model.Blocks, b => model.BlockInherits(b, "NiTimeController"));
            Assert.NotEqual(0u, model.Calculate() & (1u << NifBsxFlags.Bit.Animated));
        }

        [Fact]
        public void TheRootComesFromTheFooterNotFromBlockZero()
        {
            NifModel model = Load("nifly", "TestNifFile_RootNonZero.nif");

            // This fixture exists to say the root is not always block 0 — here it is a
            // BSXFlags sitting there. Half the bits are answers about the graph below
            // the root, so starting from the wrong block answers a different question.
            Assert.Equal("BSXFlags", model.Blocks[0].Name);

            uint flags = model.Calculate();

            Assert.NotEqual(0u, flags & (1u << NifBsxFlags.Bit.Havok));
            Assert.NotEqual(0u, flags & (1u << NifBsxFlags.Bit.SingleChain));
        }

        [Fact]
        public void ExternalEmittanceIsReadFromTheShader()
        {
            // nifly ships this pair to test adding and removing the shader flag, so
            // between them they cover both answers.
            // The flag read is shader flags 1 bit 29, not the environment-map bit
            // these two differ in, so both come out the same.
            uint with = Load("nifly", "TestNifFile_FixShaderFlags_AddEnvMap.nif").Calculate();
            uint without = Load("nifly", "TestNifFile_FixShaderFlags_RemoveEnvMap.nif").Calculate();

            Assert.Equal(
                with & (1u << NifBsxFlags.Bit.ExternalEmit),
                without & (1u << NifBsxFlags.Bit.ExternalEmit));
        }

        [Fact]
        public void NoBitsAboveNineAreEverSet()
        {
            // ck-cmd's own type is a bitset of twelve, and bits 8, 10 and 11 are
            // documented as never set in vanilla Skyrim or its DLCs. Producing one
            // would be inventing a claim about the file.
            foreach (string name in new[] { "TestNifFile_Animated_LE.nif", "TestNifFile_DeepGraph_SE.nif" })
                Assert.Equal(0u, Load("nifly", name).Calculate() & 0xFFFFFC00u);

            Assert.Equal(0u, Load("xpmsse", "skeleton_cow.nif").Calculate() & 0xFFFFFC00u);
        }

        // --- the importer ------------------------------------------------------

        /// <summary>NIF to FBX and back, which is where the calculation is used.</summary>
        private static NifModel RoundTrip(string nif)
        {
            NifModel source = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", nif), Db);

            var converter = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = Path.GetFileNameWithoutExtension(nif),
                    LegendaryEdition = true
                });

            return converter.Convert(Db);
        }

        [Fact]
        public void TheImporterHangsOneOffTheRoot()
        {
            NifModel rebuilt = RoundTrip("generate_rb_box.nif");

            NifItem root = rebuilt.GetBlock(rebuilt.FindItem(rebuilt.Footer, "Roots")!.Children[0])!;

            NifItem bsx = Assert.Single(
                rebuilt.GetRefArray(root, "Extra Data List"), b => b.Name == "BSXFlags");

            // The name is what the engine looks the block up by, so it is fixed.
            Assert.Equal("BSX", rebuilt.GetString(bsx, "Name"));
        }

        [Fact]
        public void TheImportersValueDescribesWhatItBuilt()
        {
            NifModel rebuilt = RoundTrip("generate_rb_box.nif");

            // Recalculated from the rebuilt graph rather than copied from the source,
            // so it has to agree with that graph -- and the block describing the file
            // must not itself change the answer.
            Assert.Equal(rebuilt.Calculate(), Stored(rebuilt));

            // A box with one rigid body: Havok, and one collision, so a single chain.
            Assert.NotEqual(0u, Stored(rebuilt) & (1u << NifBsxFlags.Bit.Havok));
            Assert.NotEqual(0u, Stored(rebuilt) & (1u << NifBsxFlags.Bit.SingleChain));
            Assert.Equal(0u, Stored(rebuilt) & (1u << NifBsxFlags.Bit.MultipleCollisions));
        }

        [Fact]
        public void TheImporterKeepsItToOne()
        {
            // The source already has a BSXFlags. Carrying that one across as well
            // would leave the file with two, and the engine reads the first it finds.
            NifModel rebuilt = RoundTrip("generate_rb_box.nif");

            Assert.Single(rebuilt.Blocks, b => b.Name == "BSXFlags");
        }
    }
}
