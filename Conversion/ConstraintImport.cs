using SECmd.Nif;

namespace SECmd.Conversion
{
    /// <summary>
    /// One constraint, as read back off an FBX attachment point.
    /// </summary>
    /// <remarks>
    /// Deliberately close to the file rather than to either format: the fields are
    /// strings because that is how both ck-cmd and se-cmd store them, and turning
    /// them into numbers here would mean knowing which numbers a constraint type
    /// has, which is exactly what the writer avoids needing to know.
    /// </remarks>
    public sealed class ConstraintImport
    {
        /// <summary>The descriptor kind, e.g. <c>Ragdoll</c> or <c>StiffSpring</c>.</summary>
        public required string Type { get; init; }

        /// <summary>The block wrapping the descriptor, or empty when there is none.</summary>
        public string Wrapper { get; init; } = string.Empty;

        /// <summary>
        /// The bodies a constraint *chain* passes through, in order, by node name.
        /// </summary>
        /// <remarks>
        /// Empty for an ordinary constraint, which joins two bodies and says so in the
        /// attachment node's name.
        ///
        /// A chain is not two bodies. `TestNifFile_DeepGraph_SE`'s rope is twenty-five,
        /// and its `Entity A` and `Entity B` name only the first pair — the rest of the
        /// rope is in `Chained Entities` and nowhere else. The entity fields are skipped
        /// on the grounds that the scene hierarchy says which bodies are joined, which is
        /// true of a pair and not of a chain, so this list travels by name.
        /// </remarks>
        public List<string> ChainedNames { get; init; } = [];

        /// <summary>The body that owned the constraint: entity A.</summary>
        /// <remarks>
        /// The second half of the node's name. The first half repeats the parent's
        /// own name, so this is the only part of the name carrying anything new
        /// (constraint spec §3.1).
        /// </remarks>
        public required string OwnerName { get; init; }

        /// <summary>The body the attachment point hangs off: entity B.</summary>
        public required string OtherName { get; init; }

        /// <summary>Where the joint sits, in entity B's space, in Skyrim units.</summary>
        public NifTransform FrameB { get; init; } = NifTransform.Identity;

        /// <summary>
        /// The descriptor, field by field, keyed as the writer named it.
        /// </summary>
        /// <remarks>
        /// Present on scenes se-cmd exported and absent on scenes ck-cmd did, which
        /// is what <see cref="Legacy"/> is for.
        /// </remarks>
        public Dictionary<string, string> Fields { get; } = new(StringComparer.Ordinal);

        /// <summary>The six limit properties ck-cmd writes, by their own names.</summary>
        public Dictionary<string, string> Legacy { get; } = new(StringComparer.Ordinal);

        /// <summary>Whether the full descriptor is available.</summary>
        public bool HasFields => Fields.Count > 0;
    }
}
