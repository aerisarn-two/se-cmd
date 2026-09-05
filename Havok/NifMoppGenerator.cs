using NIFSharp;
using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Generates MOPP code in-process through <c>NifMopp.dll</c>.
    /// </summary>
    /// <remarks>
    /// Faster than spawning a process, but Windows-only and requires the DLL's
    /// bitness to match the host. <see cref="MopperProcessGenerator"/> is the
    /// portable alternative.
    ///
    /// The DLL does not report per-triangle welding info, so
    /// <see cref="MoppResult.WeldingInfo"/> comes back empty here.
    /// </remarks>
    public sealed class NifMoppGenerator : IMoppGenerator
    {
        public bool IsAvailable => NifMopp.IsAvailable;

        public string? UnavailableReason => NifMopp.UnavailableReason;

        /// <inheritdoc/>
        public MoppResult? GenerateSimpleMesh(IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles)
        {
            MoppCode? code = NifMopp.Generate(vertices, triangles);

            return code is null
                ? null
                : new MoppResult(code.Code, code.Origin, code.Scale, []);
        }
    }

    /// <summary>
    /// Picks a working MOPP backend, preferring the in-process DLL and falling back
    /// to mopper.exe.
    /// </summary>
    public static class MoppGenerator
    {
        private static IMoppGenerator? _resolved;

        /// <summary>
        /// Overrides backend selection. Set to null to go back to automatic choice.
        /// </summary>
        public static IMoppGenerator? Override { get; set; }

        /// <summary>
        /// The backend to use, or null when neither is available.
        /// </summary>
        /// <remarks>
        /// The DLL is tried first because it avoids a process launch, but on anything
        /// other than 64-bit Windows it will normally be mopper.exe under Wine.
        /// </remarks>
        public static IMoppGenerator? Resolve()
        {
            if (Override is not null)
                return Override.IsAvailable ? Override : null;

            if (_resolved is { IsAvailable: true })
                return _resolved;

            var dll = new NifMoppGenerator();

            if (dll.IsAvailable)
                return _resolved = dll;

            var mopper = new MopperProcessGenerator();

            if (mopper.IsAvailable)
                return _resolved = mopper;

            return null;
        }

        /// <summary>
        /// Explains why MOPP generation is unavailable, listing what each backend
        /// reported, so the user knows which one to install.
        /// </summary>
        public static string DescribeUnavailability()
        {
            var dll = new NifMoppGenerator();
            var mopper = new MopperProcessGenerator();

            return "MOPP generation is unavailable. Havok's licence forbids shipping it inside a tool, "
                + "so one of these has to be supplied separately:"
                + $"{Environment.NewLine}  NifMopp.dll: {dll.UnavailableReason ?? "available"}"
                + $"{Environment.NewLine}  mopper.exe:  {mopper.UnavailableReason ?? "available"}";
        }
    }
}
