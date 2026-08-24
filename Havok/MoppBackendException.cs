namespace SECmd.Havok
{
    /// <summary>
    /// The MOPP backend died rather than declining.
    /// </summary>
    /// <remarks>
    /// A backend that answers "I cannot index this shape" and one that crashes look
    /// alike from the outside: both leave an output that will not parse. The exit code
    /// is what separates them, and the difference matters — a shape Havok will not
    /// index is a fact about the shape, and a crash is a bug with a model attached to
    /// it that somebody could go and look at.
    /// </remarks>
    public sealed class MoppBackendException(string message) : Exception(message);
}
