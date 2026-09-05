using LeanMeshIO;
using NIFSharp;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;
using Xunit;

namespace SECmd.Tests
{
    /// <summary>
    /// Havok constraints, written out as tagged attachment points.
    /// </summary>
    /// <remarks>
    /// FBX has constraints, but none of them mean what a Havok constraint means, so
    /// the joint becomes an empty node where the joint is, carrying the descriptor
    /// as properties. What has to be true is that the node is in the right place,
    /// says what it was, and keeps enough of the descriptor to be worth having.
    ///
    /// The corpus has two, and neither is a type FBXWrangler implements: a stiff
    /// spring inside a breakable constraint, and a ball-and-socket chain of
    /// twenty-five bodies.
    /// </remarks>
    public class ConstraintExportTests
    {
        private static readonly NifXmlDatabase Db = NifXmlDatabase.LoadEmbedded();

        private static NifModel Load(string name) =>
            NifModel.Load(Path.Combine(AppContext.BaseDirectory, "Resources", "nifly", name), Db);

        /// <summary>
        /// Converted files, kept because converting them is not cheap.
        /// </summary>
        /// <remarks>
        /// The chain fixture is twenty-five capsules, and tessellating them all for
        /// every assertion costs more than the rest of the suite put together. The
        /// conversion is deterministic, so one per file is enough; each caller still
        /// gets its own scene over the shared document.
        /// </remarks>
        private static readonly Dictionary<string, (FbxDocument Document, List<string> Warnings)> Converted =
            new(StringComparer.Ordinal);

        private static (FbxScene Scene, List<string> Warnings) Export(string name)
        {
            lock (Converted)
            {
                if (!Converted.TryGetValue(name, out var cached))
                {
                    var converter = new NifToFbx(Load(name));
                    Converted[name] = cached = (converter.Convert(), converter.Warnings);
                }

                return (new FbxScene(cached.Document), cached.Warnings);
            }
        }

        private static IEnumerable<FbxObject> AttachPoints(FbxScene scene) =>
            scene.OfClass("Model").Where(
                o => o.Name.EndsWith(FbxConstraintWriter.NameSuffix, StringComparison.Ordinal));

        private static string Property(FbxObject o, string name) =>
            o.Properties.GetString(name);

        [Theory]
        [InlineData("TestNifFile_Furniture_Col_SE.nif", "StiffSpring")]
        [InlineData("TestNifFile_DeepGraph_SE.nif", "BallSocketConstraintChain")]
        public void ConstraintsBecomeTaggedAttachmentPoints(string file, string type)
        {
            (FbxScene scene, List<string> warnings) = Export(file);

            FbxObject point = Assert.Single(AttachPoints(scene));

            // Without the tag the node is an empty at a plausible position and
            // nothing more; the tag is what says it was a joint.
            Assert.Equal(type, Property(point, FbxConstraintWriter.TypeProperty));
            Assert.Empty(warnings);
        }

        [Fact]
        public void AttachmentPointsNameTheBodiesTheyJoin()
        {
            (FbxScene scene, _) = Export("TestNifFile_DeepGraph_SE.nif");

            FbxObject point = Assert.Single(AttachPoints(scene));

            // Far body first -- which is also the parent's own name -- then the
            // body that owned the constraint. Reading it back, the parent gives one
            // entity and the second half the other.
            Assert.Equal(
                $"RopeL01_rb{FbxConstraintWriter.NameSeparator}PegRight01_rb{FbxConstraintWriter.NameSuffix}",
                point.Name);

            Assert.Equal("RopeL01_rb", Assert.Single(scene.ParentsOf(point.Id)).Name);
        }

        [Fact]
        public void AttachmentPointsHangOffABodyNode()
        {
            foreach (string file in new[] { "TestNifFile_Furniture_Col_SE.nif", "TestNifFile_DeepGraph_SE.nif" })
            {
                (FbxScene scene, _) = Export(file);

                foreach (FbxObject point in AttachPoints(scene))
                {
                    FbxObject parent = Assert.Single(scene.ParentsOf(point.Id));

                    // The frame written on the node is in a body's space, so a node
                    // parented anywhere else is at the wrong place in the world.
                    Assert.EndsWith("_rb", parent.Name, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void PivotBecomesThePositionInSkyrimUnits()
        {
            NifModel model = Load("TestNifFile_Furniture_Col_SE.nif");

            NifItem constraint = model.Blocks.First(b => b.Name == "bhkBreakableConstraint");
            NifItem descriptor = model.ConstraintDescriptor(constraint);

            NifVector4 pivot = model.FindItem(descriptor, "Pivot B")!.Value.Get<NifVector4>();

            (FbxScene scene, _) = Export("TestNifFile_Furniture_Col_SE.nif");
            FbxObject point = Assert.Single(AttachPoints(scene));

            (double x, double y, double z) = point.Properties.GetVector3("Lcl Translation");

            // Havok works in metres and the rest of the file in Skyrim units, so an
            // unscaled pivot puts the joint about seventy times too close to origin.
            Assert.Equal(pivot.X * ShapeTessellator.BhkScaleFactor, x, 4);
            Assert.Equal(pivot.Y * ShapeTessellator.BhkScaleFactor, y, 4);
            Assert.Equal(pivot.Z * ShapeTessellator.BhkScaleFactor, z, 4);

            Assert.NotEqual(0d, x);
        }

        [Fact]
        public void WrappedDescriptorIsWrittenOnceWithItsWrappersSettings()
        {
            (FbxScene scene, _) = Export("TestNifFile_Furniture_Col_SE.nif");

            FbxObject point = Assert.Single(AttachPoints(scene));

            // A breakable constraint holds its real descriptor in a union. Walking
            // both would write every field twice, once bare and once under the
            // union's path.
            Assert.Equal("0.062033348", Property(point, "hkc_length"));
            // Four components: a Havok pivot's W is part of it, and dropping it
            // meant it could only ever come back as zero.
            Assert.Equal("0.00463609 -0.00057309354 -0.009331107 0", Property(point, "hkc_pivot_b"));

            Assert.DoesNotContain(point.Properties.All,
                p => p.Name.Contains("constraint_data", StringComparison.Ordinal));

            // ...and the wrapper's own settings are not part of the descriptor.
            Assert.Equal("20", Property(point, "hkc_threshold"));
            Assert.Equal("0", Property(point, "hkc_remove_when_broken"));
        }

        [Fact]
        public void OnlyTheLiveFieldsOfADescriptorAreWritten()
        {
            (FbxScene scene, _) = Export("TestNifFile_Furniture_Col_SE.nif");

            FbxObject point = Assert.Single(AttachPoints(scene));

            var fields = point.Properties.All
                .Select(p => p.Name)
                .Where(n => n.StartsWith(FbxConstraintWriter.FieldPrefix, StringComparison.Ordinal))
                .ToList();

            // nif.xml lists the same field several times over for different Havok
            // versions. Writing them all would put this file's values next to
            // another version's zeroes under names that look equally real.
            Assert.Equal(fields.Count, fields.Distinct().Count());

            Assert.DoesNotContain(fields, n => n.Contains("hinge", StringComparison.Ordinal));
            Assert.DoesNotContain(fields, n => n.Contains("ragdoll", StringComparison.Ordinal));
        }

        [Fact]
        public void ChainPivotsAreKept()
        {
            NifModel model = Load("TestNifFile_DeepGraph_SE.nif");

            NifItem chain = model.Blocks.First(b => b.Name == "bhkBallSocketConstraintChain");
            int pivots = model.FindItem(chain, "Pivots")!.Children.Count;

            (FbxScene scene, _) = Export("TestNifFile_DeepGraph_SE.nif");
            FbxObject point = Assert.Single(AttachPoints(scene));

            // The pivots are the whole of what a chain says. Without them it is a
            // joint with a tau and a damping and no idea where any link is.
            Assert.Equal(24, pivots);

            for (int i = 0; i < pivots; i++)
            {
                Assert.True(point.Properties.Contains($"hkc_pivots_{i}_pivot_a"), $"pivot {i} missing");
                Assert.True(point.Properties.Contains($"hkc_pivots_{i}_pivot_b"), $"pivot {i} missing");
            }
        }

        [Fact]
        public void EntityLinksAreNotWrittenAsFields()
        {
            (FbxScene scene, _) = Export("TestNifFile_DeepGraph_SE.nif");

            FbxObject point = Assert.Single(AttachPoints(scene));

            // Block indices mean nothing once exported; which bodies are joined is
            // said by the node's name and by where it hangs.
            Assert.DoesNotContain(point.Properties.All,
                p => p.Name.Contains("entity_a", StringComparison.Ordinal)
                    || p.Name.Contains("entity_b", StringComparison.Ordinal));
        }

        [Fact]
        public void CollisionlessFilesGetNoAttachmentPoints()
        {
            (FbxScene scene, List<string> warnings) = Export("TestNifFile_Static_SE.nif");

            Assert.Empty(AttachPoints(scene));
            Assert.Empty(warnings);
        }

        [Fact]
        public void ConstraintsFollowTheCollisionSwitch()
        {
            FbxDocument document = new NifToFbx(
                Load("TestNifFile_DeepGraph_SE.nif"),
                new NifToFbxOptions { ExportCollision = false }).Convert();

            // A constraint joins collision bodies, so it cannot outlive them.
            Assert.Empty(AttachPoints(new FbxScene(document)));
        }

        [Fact]
        public void AttachmentPointsSurviveBeingWrittenAndReadBack()
        {
            (FbxScene before, _) = Export("TestNifFile_Furniture_Col_SE.nif");
            before.Flush();

            using var stream = new MemoryStream();
            before.Document.Save(stream);
            stream.Position = 0;

            var after = new FbxScene(FbxDocument.Load(stream));

            FbxObject point = Assert.Single(AttachPoints(after));

            Assert.Equal("StiffSpring", Property(point, FbxConstraintWriter.TypeProperty));
            Assert.Equal("0.062033348", Property(point, "hkc_length"));
        }
    }
}
