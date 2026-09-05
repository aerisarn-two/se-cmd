using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// The order a NIF stores its blocks in.
    /// </summary>
    /// <remarks>
    /// Block order is not free. A Havok block has to come *before* whatever references
    /// it — the reverse of every other block — and a constraint after the bodies it
    /// joins. Every mesh the game ships obeys this; a file built by walking a scene and
    /// appending as it goes does not, and nothing about the result looks wrong until
    /// something tries to read it.
    ///
    /// The rule is NifSkope's <c>spSanitizeBlockOrder</c>, which is the only
    /// written-down statement of it there is.
    /// </remarks>
    public class BlockOrderTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        /// <summary>Every place a block sits on the wrong side of one that references it.</summary>
        public static List<string> Violations(NifModel model)
        {
            var found = new List<string>();

            foreach (NifItem block in model.Blocks)
            {
                int self = model.IndexOf(block);

                foreach (NifItem child in Referenced(model, block))
                {
                    if (NifBlockOrder.BeforeItsParent(model, child) && model.IndexOf(child) > self)
                        found.Add($"{child.Name}[{model.IndexOf(child)}] is after {block.Name}[{self}]");
                }

                if (!model.BlockInherits(block, "bhkConstraint"))
                    continue;

                foreach (string field in new[] { "Entity A", "Entity B" })
                {
                    if (model.FindItem(block, field) is { } link
                        && model.GetBlock(link) is { } entity
                        && model.IndexOf(entity) > self)
                    {
                        found.Add($"entity {entity.Name}[{model.IndexOf(entity)}] is after {block.Name}[{self}]");
                    }
                }
            }

            return found;
        }

        private static IEnumerable<NifItem> Referenced(NifModel model, NifItem block)
        {
            foreach (NifItem link in Links(block))
            {
                if (model.GetBlock(link) is { } target)
                    yield return target;
            }
        }

        private static IEnumerable<NifItem> Links(NifItem item)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Value.Type == NifValueType.Link)
                {
                    yield return child;
                }
                else if (child.Value.Type != NifValueType.UpLink)
                {
                    foreach (NifItem nested in Links(child))
                        yield return nested;
                }
            }
        }

        public static TheoryData<string> Fixtures()
        {
            var data = new TheoryData<string>();
            string root = Path.Combine(AppContext.BaseDirectory, "Resources");

            foreach (string path in Directory.GetFiles(root, "*.nif", SearchOption.AllDirectories))
            {
                if (FixtureFiles.IsFixture(path))
                    data.Add(Path.GetRelativePath(root, path));
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void TheFilesThemselvesAreInOrder(string name)
        {
            // The check has to agree with the files it is checking, or it is only
            // testing itself.
            NifModel model = NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

            Assert.Empty(Violations(model));
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void ARebuiltFileIsInOrderToo(string name)
        {
            NifModel source = NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", name), Db);

            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifModel rebuilt = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);

            Assert.Empty(Violations(rebuilt));
        }

        [Fact]
        public void ChildrenAreLeftInTheOrderTheyCameIn()
        {
            // NifSkope offers a second reordering -- spReorderLinks, which sorts a
            // node's children so shapes come last -- and it must not be applied.
            //
            // It is not an invariant: two of the shipped fixtures violate it, so it is
            // cleanup NifSkope offers rather than a rule the format has. And for a
            // BSOrderedNode it is actively wrong, since that class exists to draw its
            // children in a fixed order and sorting them changes what it draws.
            NifModel source = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "nifly/TestNifFile_OrderedNode_SE.nif"), Db);

            NifItem ordered = source.Blocks.First(b => b.Name == "BSOrderedNode");

            var before = source.FindItem(ordered, "Children")!.Children
                .Select(c => source.GetBlock(c) is { } t ? source.GetName(t) : "-")
                .ToList();

            Assert.True(before.Count > 2, "the fixture is supposed to have several children");

            NifItem root = source.GetBlock(source.FindItem(source.Footer, "Roots")!.Children[0])!;

            NifModel rebuilt = new FbxToNif(
                new FbxScene(new NifToFbx(source).Convert()),
                new FbxToNifOptions
                {
                    RootName = source.GetName(root),
                    Version = source.Version,
                    UserVersion = source.UserVersion,
                    LegendaryEdition = source.BSVersion < 100
                }).Convert(Db);

            NifItem after = rebuilt.Blocks.First(b => b.Name == "BSOrderedNode");

            Assert.Equal(
                before,
                rebuilt.FindItem(after, "Children")!.Children
                    .Select(c => rebuilt.GetBlock(c) is { } t ? rebuilt.GetName(t) : "-"));
        }

        [Fact]
        public void ReorderingKeepsEveryLinkPointingWhereItDid()
        {
            // Renumbering is the whole risk here: a link is a block number, so a move
            // that misses one leaves it pointing at the wrong block rather than at
            // nothing, which is far harder to notice.
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "xpmsse/skeleton_cow.nif"), Db);

            var before = model.Blocks
                .Where(b => model.BlockInherits(b, "bhkCollisionObject"))
                .ToDictionary(b => b, b => model.GetRef(b, "Body")!.Name);

            Assert.NotEmpty(before);

            model.ReorderBlocks(NifBlockOrder.Sorted(model));

            foreach ((NifItem collision, string body) in before)
                Assert.Equal(body, model.GetRef(collision, "Body")!.Name);

            Assert.Empty(Violations(model));
        }

        [Fact]
        public void AnOrderThatIsNotAPermutationIsRefused()
        {
            // Dropping a block would leave links pointing at whatever moved into its
            // place, so the reorder refuses anything but a rearrangement.
            NifModel model = NifModel.Load(
                Path.Combine(AppContext.BaseDirectory, "Resources", "generate_rb_box.nif"), Db);

            Assert.Throws<ArgumentException>(() => model.ReorderBlocks(model.Blocks.Skip(1).ToList()));
            Assert.Throws<ArgumentException>(() => model.ReorderBlocks([model.Blocks[0], .. model.Blocks]));
        }
    }
}
