using System.Reflection;

namespace SECmd
{
    /// <summary>
    /// Which se-cmd this is.
    /// </summary>
    /// <remarks>
    /// Taken from the assembly rather than written down here, because the release
    /// pipeline sets it from the tag and two places to change is one place to
    /// forget. A build from a working copy has no tag and says so.
    ///
    /// It goes wherever somebody might later ask "which version did this" -- the
    /// console, the conversion log, the NIF header and the FBX's Creator -- since a
    /// converted file outlives the session that made it and the answer is otherwise
    /// unrecoverable.
    /// </remarks>
    internal static class Build
    {
        /// <summary>The version, as a person would quote it in a bug report.</summary>
        public static string Version { get; } = Read();

        /// <summary>The name and version together, for stamping into a file.</summary>
        public static string Signature => $"se-cmd {Version}";

        private static string Read()
        {
            Assembly assembly = typeof(Build).Assembly;

            // The informational version carries the source revision after a '+',
            // which is useful in a log and noise in a header.
            string? informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (informational is { Length: > 0 })
            {
                int plus = informational.IndexOf('+');
                return plus < 0 ? informational : informational[..plus];
            }

            return assembly.GetName().Version?.ToString() ?? "unversioned";
        }
    }
}
