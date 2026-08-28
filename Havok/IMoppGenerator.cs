using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Builds Havok MOPP bounding-volume trees for mesh collision shapes.
    /// </summary>
    /// <remarks>
    /// MOPP generation is the one part of the conversion that genuinely needs Havok,
    /// and Havok's licence forbids redistributing it as part of a tool. So se-cmd
    /// contains no Havok code and instead calls out to a binary the user supplies —
    /// the same posture NifSkope takes.
    ///
    /// Two backends implement this: <see cref="NifMoppGenerator"/> (in-process
    /// P/Invoke into NifMopp.dll, Windows only) and
    /// <see cref="MopperProcessGenerator"/> (out-of-process mopper.exe, which runs
    /// under Wine and is therefore the portable option).
    /// </remarks>
    public interface IMoppGenerator
    {
        /// <summary>True when this backend can actually run.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// What the backend said for itself on the last call from this thread.
        /// </summary>
        /// <remarks>
        /// A generation that returns null says only that it failed. The backend
        /// usually knows more and says so on its error stream, and throwing that away
        /// cost hours once: a dock building's collision was reported as "MOPP
        /// generation failed" when Havok had in fact built it and merely complained
        /// about the mesh's winding — on standard output, where the complaint landed
        /// among the numbers and broke the parse.
        ///
        /// Per thread, because a sweep converts in parallel and this belongs to the
        /// call that just returned rather than to the backend.
        /// </remarks>
        string? LastDiagnostics => null;

        /// <summary>
        /// The attempts that did not work on the last call from this thread.
        /// </summary>
        /// <remarks>
        /// Non-empty even when the call *succeeded*, because a generation is retried
        /// and a retry that works hides the attempt that did not. A backend that
        /// crashed on the first try and answered on the second still means some model
        /// kills it, and the only way to find which is to be told while that model is
        /// in hand.
        /// </remarks>
        IReadOnlyList<string> LastFailures => [];

        /// <summary>Why the backend is unusable, for reporting.</summary>
        string? UnavailableReason { get; }

        /// <summary>
        /// Builds MOPP code for a triangle mesh, as needed by a
        /// <c>bhkMoppBvTreeShape</c> wrapping a simple mesh shape.
        /// </summary>
        /// <returns>The code, or null when generation was not possible.</returns>
        MoppResult? GenerateSimpleMesh(IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles);

        /// <summary>
        /// Builds a whole <c>bhkCompressedMeshShape</c> from one or more geometries,
        /// which is what a mesh collision shape needs.
        /// </summary>
        /// <remarks>
        /// This is a different job from <see cref="GenerateSimpleMesh"/>: Havok
        /// chunks and quantises the mesh, so the chunk layout, the transforms and
        /// the MOPP tree all have to come from the same pass. Only backends that can
        /// do it return anything; the rest return null.
        /// </remarks>
        CompressedMeshResult? GenerateCompressedMesh(
            IReadOnlyList<MoppGeometry> geometries, int materials = 1) => null;

        /// <summary>
        /// Builds MOPP code for a shape collection that is not a mesh.
        /// </summary>
        /// <remarks>
        /// A MOPP tree indexes a shape *collection*, and Havok's own
        /// <c>hkpMoppUtility::buildCode</c> takes a shape container rather than a mesh
        /// — so a `bhkListShape` of primitives is as valid an input as a mesh, which is
        /// how ck-cmd's `HKXWrangler` builds one. What that needs is the primitives as
        /// real Havok shapes, so they travel as a description the backend builds from.
        ///
        /// A tree over a list has leaves that are child *indices*, not triangle
        /// indices, which is why a tessellation cannot stand in for one.
        /// </remarks>
        /// <param name="description">
        /// The shape tree in mopper's own grammar. See <c>MoppShapeWriter</c>.
        /// </param>
        MoppResult? GenerateCollection(string description) => null;
    }

    /// <summary>One geometry going into a compressed mesh shape.</summary>
    /// <summary>
    /// One piece of geometry for a compressed mesh, and which material it is made of.
    /// </summary>
    /// <remarks>
    /// The material is an index into the table the caller sends alongside. Havok gives
    /// a chunk the material of the triangles that went into it, so a mesh with two
    /// materials has to arrive as two geometries -- one per material -- or every chunk
    /// comes back claiming the same one.
    /// </remarks>
    public sealed record MoppGeometry(
        IReadOnlyList<NifVector3> Vertices,
        IReadOnlyList<NifTriangle> Triangles,
        int Material = 0);

    /// <summary>A chunk of a compressed mesh shape, as Havok packed it.</summary>
    public sealed record CompressedMeshChunk(
        NifVector4 Offset,
        uint MaterialInfo,
        ushort TransformIndex,
        IReadOnlyList<ushort> Vertices,
        IReadOnlyList<ushort> Indices,
        IReadOnlyList<ushort> StripLengths,
        IReadOnlyList<ushort> WeldingInfo);

    /// <summary>A transform referenced by a compressed mesh chunk.</summary>
    public sealed record CompressedMeshTransform(NifVector4 Translation, NifQuat Rotation);

    /// <summary>
    /// Everything a <c>bhkCompressedMeshShape</c> and its data block need.
    /// </summary>
    public sealed record CompressedMeshResult(
        MoppResult Mopp,
        NifVector4 BoundsMin,
        NifVector4 BoundsMax,
        IReadOnlyList<NifVector4> BigVertices,
        IReadOnlyList<(uint A, uint B, uint C, uint Material, uint WeldingInfo)> BigTriangles,
        IReadOnlyList<CompressedMeshTransform> Transforms,
        IReadOnlyList<CompressedMeshChunk> Chunks);

    /// <summary>
    /// A generated MOPP tree, with the quantisation it was built against and the
    /// per-triangle welding info Havok computed alongside it.
    /// </summary>
    /// <param name="Code">The MOPP bytecode, stored verbatim in the NIF.</param>
    /// <param name="Origin">Offset mapping world space into the tree's integer space.</param>
    /// <param name="Scale">Scale mapping world space into the tree's integer space.</param>
    /// <param name="WeldingInfo">
    /// Per-triangle welding info, empty when the backend does not report it.
    /// </param>
    public sealed record MoppResult(
        byte[] Code,
        NifVector3 Origin,
        float Scale,
        IReadOnlyList<ushort> WeldingInfo);
}
