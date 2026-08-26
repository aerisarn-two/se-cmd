using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    public class NifModelTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>Every NIF fixture, as a theory data source.</summary>
        public static TheoryData<string> NifFiles() =>
        [
            "generate_rb.nif",
            "generate_rb_box.nif",
            "generate_rb_sphere.nif",
            "multi_material_cube.nif"
        ];

        private static string PathTo(string name) => Path.Combine(AppContext.BaseDirectory, "Resources", name);

        private static NifModel LoadFixture(string name) => NifModel.Load(PathTo(name), Db);

        [Theory]
        [MemberData(nameof(NifFiles))]
        public void LoadsTheFixture(string name)
        {
            NifModel model = LoadFixture(name);

            Assert.Equal(0x14020007u, model.Version);
            Assert.NotEmpty(model.Blocks);
        }

        /// <summary>
        /// The load/save round trip is the real test of the whole stack: the XML
        /// descriptors, every condition, every array length and both stream
        /// directions have to agree for the bytes to come back identical.
        /// </summary>
        [Theory]
        [MemberData(nameof(NifFiles))]
        public void SavingReproducesTheFileByteForByte(string name)
        {
            byte[] original = File.ReadAllBytes(PathTo(name));

            NifModel model = LoadFixture(name);

            using var saved = new MemoryStream();
            model.Save(saved);

            byte[] actual = saved.ToArray();

            Assert.Equal(original.Length, actual.Length);

            // Report the first divergence rather than dumping two blobs.
            for (int i = 0; i < original.Length; i++)
            {
                if (original[i] != actual[i])
                {
                    Assert.Fail(
                        $"{name} differs at offset 0x{i:X} " +
                        $"(expected 0x{original[i]:X2}, got 0x{actual[i]:X2})");
                }
            }
        }

        [Theory]
        [MemberData(nameof(NifFiles))]
        public void LoadsWithoutWarnings(string name)
        {
            NifModel model = LoadFixture(name);

            Assert.Empty(model.Warnings);
        }

        [Fact]
        public void AnEmptyBinaryArrayIsEmptyRatherThanAFailure()
        {
            // A binary array is read as one opaque blob, and its size comes from a
            // count field. Sizing one rejected a size of zero as though it were a size
            // that could not be worked out — and every caller reads that as a hard
            // error, so writing threw before a byte was emitted and reading turned into
            // a NifFormatException that rejected the entire file.
            //
            // A `bhkMoppBvTreeShape` fresh from the authoring API is exactly this case:
            // its `MOPP Code\Data Size` sits at zero until a backend fills it in. So a
            // whole file could not be written over a field with nothing in it, and a
            // file carrying a legitimately empty blob could not be read back.
            //
            // The ordinary array path has always treated zero as zero. This is only
            // that rule reaching the binary case.
            NifModel model = NifModel.CreateNew(Db, bsVersion: 100);

            NifItem root = model.InsertBlock("NiNode");
            model.SetString(root, "Name", "root");
            model.SetRoots([root]);

            NifItem mopp = model.InsertBlock("bhkMoppBvTreeShape");

            // Left exactly as inserted: the size is zero and the blob is empty.
            Assert.Equal(0u, model.FindItem(mopp, "MOPP Code\\Data Size")!.Value.ToUInt());

            model.UpdateHeader();

            using var stream = new MemoryStream();
            model.Save(stream);
            stream.Position = 0;

            NifModel reloaded = NifModel.Load(stream, Db);

            Assert.Contains(reloaded.Blocks, b => b.Name == "bhkMoppBvTreeShape");
        }

        [Fact]
        public void BlockCountMatchesTheHeader()
        {
            NifModel model = LoadFixture("multi_material_cube.nif");

            Assert.Equal(model.GetUInt(model.Header, "Num Blocks"), (uint)model.Blocks.Count);
        }

        [Fact]
        public void ReadsTheBlockHierarchy()
        {
            NifModel model = LoadFixture("multi_material_cube.nif");

            // A Skyrim mesh is rooted at some kind of NiNode.
            NifItem root = model.Blocks[0];
            Assert.True(model.BlockInherits(root, "NiNode"), $"expected a NiNode at the root, got {root.Name}");
        }

        [Fact]
        public void ResolvesStringsThroughTheHeaderTable()
        {
            NifModel model = LoadFixture("multi_material_cube.nif");

            // From 20.1.0.3 a block's Name is an index into the header's string
            // table rather than inline text.
            NifItem root = model.Blocks[0];
            NifItem? name = model.FindItem(root, "Name");

            Assert.NotNull(name);
            Assert.Equal(NifValueType.StringIndex, name!.Value.Type);
            Assert.NotEmpty(model.ResolveString(name));
        }

        [Fact]
        public void FollowsLinksBetweenBlocks()
        {
            NifModel model = LoadFixture("multi_material_cube.nif");

            NifItem root = model.Blocks[0];
            NifItem? children = model.FindItem(root, "Children");

            Assert.NotNull(children);
            Assert.NotEmpty(children!.Children);

            // Every non-null child link must name a real block.
            foreach (NifItem link in children.Children)
            {
                int index = link.Value.ToLink();

                if (index >= 0)
                    Assert.NotNull(model.GetBlock(link));
            }
        }

        [Fact]
        public void ArrayLengthsMatchTheirCountFields()
        {
            NifModel model = LoadFixture("multi_material_cube.nif");

            NifItem root = model.Blocks[0];
            uint declared = model.GetUInt(root, "Num Children");
            NifItem? children = model.FindItem(root, "Children");

            Assert.NotNull(children);
            Assert.Equal((int)declared, children!.Children.Count);
        }

        [Fact]
        public void ReadsHavokCollisionBlocks()
        {
            // These fixtures exist to exercise the bhk* side of the format, which
            // is where the mixin flattening and the fixed compounds show up.
            NifModel model = LoadFixture("generate_rb_box.nif");

            NifItem? rigidBody = model.Blocks.FirstOrDefault(b => model.BlockInherits(b, "bhkRigidBody"));

            Assert.NotNull(rigidBody);

            // HavokFilter is spliced in, so its fields sit directly on the block.
            Assert.NotNull(model.FindItem(rigidBody!, "Layer"));
        }
    }
}
