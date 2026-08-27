using SECmd.Nif;

namespace SECmd.Tests
{
    /// <summary>
    /// Which fields a round trip is currently allowed to change, and why.
    /// </summary>
    /// <remarks>
    /// Every test that round-trips a NIF compares the whole graph with
    /// <see cref="NifComparer"/> and fails on any field not listed here. The point is
    /// the ratchet: a difference that is already known does not fail, and a *new* one
    /// does, so the surface can only shrink.
    ///
    /// The two lists are not the same thing and must not be merged.
    ///
    /// <see cref="ByDesign"/> is derived rather than carried, on purpose, and will
    /// never be empty. <see cref="Open"/> is a list of defects — every entry is
    /// something this port gets wrong, recorded so that it fails visibly as a *count*
    /// rather than invisibly as nothing at all. Entries leave it by being fixed.
    ///
    /// It exists because the corpus sweep compares a census of block *types*, so
    /// anything wrong inside a block was invisible to it. That is how a shader
    /// controller came back driving the wrong variable in 1,648 meshes, how every
    /// off-centre collision box collapsed to the origin, and how a skin partition's
    /// triangles were being remapped into nonsense — all with a green suite.
    /// </remarks>
    public static class RoundTripBaseline
    {
        /// <summary>
        /// Fields computed from the rebuilt graph rather than carried across.
        /// </summary>
        /// <remarks>
        /// Carrying these would describe the file the FBX came from rather than the one
        /// just built, and for several the source value is the most likely to be stale.
        /// </remarks>
        public static readonly Dictionary<string, string> ByDesign = new(StringComparer.Ordinal)
        {
            // Refitted from the tessellated collision geometry, which is the half of a
            // shape a DCC tool can edit; carrying the original would ignore the edit.
            ["Radius"] = "refitted from the tessellated collision geometry",
            ["Dimensions"] = "refitted from the tessellated collision geometry",

            // Zeroed by the static motion profile, as ck-cmd's is. A static carrying a
            // mass is treated as movable, which is how scenery falls through the world.
            ["Mass"] = "zeroed by the static motion profile, as ck-cmd does",

            // The inertia tensor is computed from the shape and the mass (spec §5.7.2).
            ["m11"] = "inertia computed from the shape and mass",
            ["m12"] = "inertia computed from the shape and mass",
            ["m13"] = "inertia computed from the shape and mass",
            ["m21"] = "inertia computed from the shape and mass",
            ["m22"] = "inertia computed from the shape and mass",
            ["m23"] = "inertia computed from the shape and mass",
            ["m31"] = "inertia computed from the shape and mass",
            ["m32"] = "inertia computed from the shape and mass",
            ["m33"] = "inertia computed from the shape and mass",

            // 0xCD in every byte is the debug heap's fill pattern: fields the exporter
            // that wrote the fixture never initialised. There is nothing to reproduce.
            ["Auto Remove Level"] = "uninitialised in the source file (0xCD)",
            ["Response Modifier Flags"] = "uninitialised in the source file (0xCD)",
            ["Num Shape Keys in Contact Point"] = "uninitialised in the source file (0xCD)",
            ["Force Collided Onto PPU"] = "uninitialised in the source file (0xCD)",

            ["Consistency Flags"] = "not carried",
            ["Shader Flags 2"] = "one flag differs; the shader flag words are not carried verbatim",
            ["Bounding Sphere"] = "recomputed from the vertices",
            ["Center"] = "recomputed from the vertices",

            // The hull is refitted, so its corners and planes arrive in the fit's order
            // rather than Havok's. That the corners themselves all come back is asserted
            // by AConvexHullKeepsEveryCorner, and the plane convention by
            // ConvexHullPlaneTests.
            ["Vertices"] = "convex hull refitted from the tessellation, so the order differs",
            ["Normals"] = "convex hull refitted from the tessellation, so the order differs",

            // Regenerated from the geometry rather than carried (spec §5.3.1). A
            // deliberate departure from ck-cmd, which keeps an FBX's own tangents; §7.3
            // records why that is the wrong half of the trade for an authored mesh.
            ["Tangent"] = "tangent space regenerated (§5.3.1)",
            ["Tangents"] = "tangent space regenerated (§5.3.1)",
            ["Bitangents"] = "tangent space regenerated (§5.3.1)",
            ["Bitangent X"] = "tangent space regenerated (§5.3.1)",
            ["Bitangent Y"] = "tangent space regenerated (§5.3.1)",
            ["Bitangent Z"] = "tangent space regenerated (§5.3.1)",

            // Calculated from the block graph rather than carried (bsxflags-spec.md).
            ["BSXFlags"] = "calculated from the block graph",

            // nif.xml's KeyType runs 1..5 and has no zero, so a key group carrying one
            // is a field nothing ever set — which is what a model built in a test looks
            // like. Writing LINEAR_KEY for it is normalisation, not loss.
            ["Interpolation"] = "0 is not a KeyType; an unset one becomes LINEAR_KEY",
        };

        /// <summary>
        /// Fields a round trip currently gets wrong. Every entry is a defect.
        /// </summary>
        /// <remarks>
        /// Recorded, not excused. The counts are what the fixtures reported when the
        /// list was taken, so a change that makes one worse is still invisible here —
        /// what this catches is a *new* field going wrong, which is the failure mode
        /// that has cost the most.
        ///
        /// Some of these are one defect wearing several names: a mesh whose vertices
        /// arrive in the wrong space reports `Vertex`, `Num Vertices`, `Data Size` and
        /// `Triangles` at once. Fixing the cause will empty several rows together.
        ///
        /// Known causes at the time of writing, from the review backlog:
        ///
        /// <list type="bullet">
        /// <item>`TestNifFile_DeepGraph_SE` returns its vertices in world space —
        /// (-2.1, 2.5, 0.15) comes back as (-197, 222, 334) — so a transform is applied
        /// twice somewhere down a 25-deep chain. That accounts for `Vertex`, `Normal`
        /// and a good deal of `UV`.</item>
        /// <item>The `UV` difference is exactly `1 - v`: one V flip too many or too few
        /// on some path, since the writer flips and the reader flips back.</item>
        /// <item>A skinned SE shape keeps its geometry in the skin partition and the
        /// rebuilt file puts it on the shape, which is what `Vertex Desc`, `Vertex Size`,
        /// `Vertex Data`, `Num Vertices` and `Num Triangles` are saying.</item>
        /// <item>The motion profile is applied rather than carried, so `Layer`,
        /// `Motion System`, `Quality Type`, `Solver Deactivation`, `Friction`,
        /// `Restitution` and the damping pair take the profile's values. This one may
        /// well be by design — the spec documents the profile — but it has not been
        /// checked, and guessing would put a defect in the wrong list.</item>
        /// </list>
        /// </remarks>
        public static readonly Dictionary<string, string> Open = new(StringComparer.Ordinal)
        {
            // A shader property's own name is not carried. Found the moment the
            // synthetic models were compared in full: the test that put a
            // BSWaterShaderProperty on a shape checked its flags and what it hung from,
            // and never that it was still called "water". Keyed by path, because
            // excusing `Name` everywhere would hide the next one of these.
            ["BSWaterShaderProperty/Name"] = "a shader property's name is not carried",

            // Also found the moment synthetic models were compared in full, each by a
            // test that was looking at something else at the time.
            ["Look At"] = "a NiLookAtInterpolator's target reference is not carried",
            ["BSTriShape"] = "a shape with no vertices comes back as NiTriShape",
            ["Strip Lengths"] = "a strips shape's strips are restructured, 1 group becoming 2",
            ["Points"] = "a strips shape's strips are restructured, 1 group becoming 2",

            ["UV"] = "10 fixtures, 7610 fields",
            ["Data Size"] = "9 fixtures, 27 fields",
            ["Num Extra Data List"] = "9 fixtures, 9 fields",
            ["Vertex Desc"] = "6 fixtures, 30 fields",
            ["Vertex Data"] = "6 fixtures, 12 fields",
            ["Vertex Size"] = "6 fixtures, 12 fields",
            ["Angular Damping"] = "5 fixtures, 53 fields",
            ["Linear Damping"] = "5 fixtures, 53 fields",
            ["Unused 01"] = "5 fixtures, 48 fields",
            ["Penetration Depth"] = "5 fixtures, 44 fields",
            ["Num Triangles"] = "5 fixtures, 10 fields",
            ["Flags"] = "4 fixtures, 31 fields",
            ["Translation"] = "4 fixtures, 14 fields",
            ["Num Vertices"] = "4 fixtures, 9 fields",
            ["Vertex"] = "3 fixtures, 2392 fields",
            ["Normal"] = "3 fixtures, 1617 fields",
            ["Layer"] = "3 fixtures, 100 fields",
            ["Motion System"] = "3 fixtures, 48 fields",
            ["Quality Type"] = "3 fixtures, 48 fields",
            ["Solver Deactivation"] = "3 fixtures, 48 fields",
            ["Unused 03"] = "3 fixtures, 45 fields",
            ["Active Material"] = "3 fixtures, 9 fields",
            ["Rotation"] = "3 fixtures, 5 fields",
            ["Build Type"] = "3 fixtures, 3 fields",
            ["Chunk Materials"] = "3 fixtures, 3 fields",
            ["Chunk Transforms"] = "3 fixtures, 3 fields",
            ["Integer Data"] = "3 fixtures, 3 fields",
            ["Min"] = "3 fixtures, 3 fields",
            ["Num Materials"] = "3 fixtures, 3 fields",
            ["Num Transforms"] = "3 fixtures, 3 fields",
            ["Offset"] = "3 fixtures, 3 fields",
            ["Shader Type"] = "3 fixtures, 3 fields",
            ["Target"] = "3 fixtures, 3 fields",
            ["User Data"] = "3 fixtures, 3 fields",
            ["Triangles"] = "2 fixtures, 3430 fields",
            ["First Point"] = "2 fixtures, 48 fields",
            ["Second Point"] = "2 fixtures, 48 fields",
            ["Friction"] = "2 fixtures, 25 fields",
            ["Restitution"] = "2 fixtures, 25 fields",
            ["Backward"] = "2 fixtures, 22 fields",
            ["Forward"] = "2 fixtures, 22 fields",
            ["Chunks"] = "2 fixtures, 2 fields",
            ["Environment Map Scale"] = "2 fixtures, 2 fields",
            ["Extra Data List"] = "2 fixtures, 2 fields",
            ["Lighting Effect 2"] = "2 fixtures, 2 fields",
            ["Num Chunks"] = "2 fixtures, 2 fields",
            ["Num Normals"] = "2 fixtures, 2 fields",
            ["Shader Flags 1"] = "2 fixtures, 2 fields",
            ["Unknown Float 1"] = "2 fixtures, 2 fields",
            ["Index"] = "1 fixture, 8170 fields",
            ["Weight"] = "1 fixture, 7563 fields",
            ["Vertex Weights"] = "1 fixture, 2513 fields",
            ["Triangles Copy"] = "1 fixture, 1888 fields",
            ["Bone Indices"] = "1 fixture, 930 fields",
            ["Vertex Colors"] = "1 fixture, 39 fields",
            ["Chained Entities"] = "1 fixture, 25 fields",
            ["Scale"] = "1 fixture, 5 fields",
            ["Array Size"] = "1 fixture, 4 fields",
            ["Radius 1"] = "1 fixture, 4 fields",
            ["Radius 2"] = "1 fixture, 4 fields",
            ["Start Time"] = "1 fixture, 4 fields",
            ["Stop Time"] = "1 fixture, 4 fields",
            ["Accum Root Name"] = "1 fixture, 3 fields",
            ["Controller"] = "1 fixture, 3 fields",
            ["Cycle Type"] = "1 fixture, 3 fields",
            ["Indices"] = "1 fixture, 3 fields",
            ["Material Index"] = "1 fixture, 3 fields",
            ["Num Indices"] = "1 fixture, 3 fields",
            ["Num Strips"] = "1 fixture, 3 fields",
            ["Num Welding Info"] = "1 fixture, 3 fields",
            ["Strips"] = "1 fixture, 3 fields",
            ["Welding Info"] = "1 fixture, 3 fields",
            ["Has Vertex Weights"] = "1 fixture, 2 fields",
            ["Refraction Strength"] = "1 fixture, 2 fields",
            ["Children"] = "1 fixture, 1 field",
            ["Entity B"] = "1 fixture, 1 field",
            ["Extra Targets"] = "1 fixture, 1 field",
            ["Lighting Effect 1"] = "1 fixture, 1 field",
            ["Next Controller"] = "1 fixture, 1 field",
            ["NiIntegerExtraData"] = "1 fixture, 1 field",
            ["NiPSysEmitterCtlr"] = "1 fixture, 1 field",
            ["Num Children"] = "1 fixture, 1 field",
            ["Num Extra Targets"] = "1 fixture, 1 field",
            ["Num Objs"] = "1 fixture, 1 field",
            ["Objs"] = "1 fixture, 1 field",
            ["Transform Index"] = "1 fixture, 1 field",
        };

        /// <summary>Every difference that is neither derived nor already recorded.</summary>
        /// <remarks>
        /// An entry keyed by a bare field name excuses that field everywhere, which is
        /// right for something derived — a bounding sphere is recomputed wherever it
        /// appears. It is far too broad for a field as common as `Name`, so an entry
        /// holding a slash is matched against the *path* instead, and excuses only the
        /// place it names.
        /// </remarks>
        public static List<NifDifference> Unexplained(NifModel source, NifModel rebuilt) =>
            [.. NifComparer.Compare(source, rebuilt).Where(d => !Excused(d))];

        private static bool Excused(NifDifference difference) =>
            Matches(ByDesign, difference) || Matches(Open, difference);

        private static bool Matches(Dictionary<string, string> entries, NifDifference difference)
        {
            foreach (string key in entries.Keys)
            {
                bool matched = key.Contains('/', StringComparison.Ordinal)
                    ? difference.Path.Contains(key, StringComparison.Ordinal)
                    : key == difference.Field;

                if (matched)
                    return true;
            }

            return false;
        }
    }
}
