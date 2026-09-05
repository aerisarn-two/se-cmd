using NIFSharp;
using System.Runtime.InteropServices;
using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Binding to <c>NifMopp.dll</c>, the external library that generates Havok MOPP
    /// bounding-volume trees.
    /// </summary>
    /// <remarks>
    /// MOPP code is what lets Havok index a mesh collision shape, and generating it
    /// requires the proprietary Havok SDK. NifSkope solves this by shipping a small
    /// DLL compiled against that SDK and loading it at run time
    /// (<c>src/spells/moppcode.cpp</c>); this follows the same approach and the same
    /// exported ABI, so the very same binary works.
    ///
    /// The library is loaded lazily and its absence is not an error: everything that
    /// does not need MOPP keeps working, and callers check
    /// <see cref="IsAvailable"/> before relying on it. Being a Havok build, it is
    /// Windows-only, and its bitness has to match the host process.
    /// </remarks>
    public static class NifMopp
    {
        private const string LibraryName = "NifMopp.dll";

        private static readonly object Gate = new();
        private static bool _attempted;
        private static nint _handle;

        private static GenerateMoppCodeDelegate? _generateMoppCode;
        private static GenerateMoppCodeWithSubshapesDelegate? _generateMoppCodeWithSubshapes;
        private static RetrieveMoppCodeDelegate? _retrieveMoppCode;
        private static RetrieveMoppScaleDelegate? _retrieveMoppScale;
        private static RetrieveMoppOriginDelegate? _retrieveMoppOrigin;

        // The DLL is __stdcall. That only distinguishes a calling convention on
        // 32-bit Windows, but declaring it keeps an x86 host working.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GenerateMoppCodeDelegate(
            int vertexCount, [In] NifVector3[] vertices, int triangleCount, [In] NifTriangle[] triangles);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GenerateMoppCodeWithSubshapesDelegate(
            int shapeCount,
            [In] int[] shapes,
            int vertexCount,
            [In] NifVector3[] vertices,
            int triangleCount,
            [In] NifTriangle[] triangles);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RetrieveMoppCodeDelegate(int bufferLength, [Out] byte[] buffer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RetrieveMoppScaleDelegate(out float value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int RetrieveMoppOriginDelegate(out NifVector3 value);

        /// <summary>
        /// Where to look for the library, in addition to the default search paths.
        /// Set this before the first call if the DLL lives outside the executable's
        /// directory.
        /// </summary>
        public static string? SearchDirectory { get; set; }

        /// <summary>
        /// True when the library was found and every entry point resolved. Loading is
        /// attempted once and the result cached.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                EnsureLoaded();
                return _generateMoppCode is not null
                    && _retrieveMoppCode is not null
                    && _retrieveMoppScale is not null
                    && _retrieveMoppOrigin is not null;
            }
        }

        /// <summary>True when the library also offers the sub-shape entry point.</summary>
        public static bool SupportsSubshapes
        {
            get
            {
                EnsureLoaded();
                return _generateMoppCodeWithSubshapes is not null;
            }
        }

        /// <summary>Why the library could not be used, for reporting to the user.</summary>
        public static string? UnavailableReason { get; private set; }

        private static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (_attempted)
                    return;

                _attempted = true;

                if (!TryLoadLibrary(out _handle))
                {
                    UnavailableReason =
                        $"{LibraryName} could not be loaded. It is a Havok build and therefore Windows-only, "
                        + "and its bitness must match this process. Place it next to the executable or set "
                        + $"{nameof(NifMopp)}.{nameof(SearchDirectory)}.";
                    return;
                }

                _generateMoppCode = GetExport<GenerateMoppCodeDelegate>("GenerateMoppCode");
                _generateMoppCodeWithSubshapes =
                    GetExport<GenerateMoppCodeWithSubshapesDelegate>("GenerateMoppCodeWithSubshapes");
                _retrieveMoppCode = GetExport<RetrieveMoppCodeDelegate>("RetrieveMoppCode");
                _retrieveMoppScale = GetExport<RetrieveMoppScaleDelegate>("RetrieveMoppScale");
                _retrieveMoppOrigin = GetExport<RetrieveMoppOriginDelegate>("RetrieveMoppOrigin");

                if (_generateMoppCode is null || _retrieveMoppCode is null
                    || _retrieveMoppScale is null || _retrieveMoppOrigin is null)
                {
                    UnavailableReason = $"{LibraryName} was loaded but does not export the expected entry points.";
                }
            }
        }

        /// <summary>
        /// Where the library is looked for, in order: an explicitly configured
        /// directory, the current working directory, and the directory holding the
        /// executable.
        /// </summary>
        /// <remarks>
        /// The working directory comes first so that a copy sitting next to the
        /// files being converted wins over an installed one, which is what someone
        /// dropping the DLL into their working folder expects. The executable's own
        /// directory is where NifSkope keeps it. If none of them has it, the
        /// platform loader gets a bare name and applies its own search path.
        /// </remarks>
        public static IEnumerable<string> SearchPaths()
        {
            if (SearchDirectory is { Length: > 0 } configured)
                yield return Path.Combine(configured, LibraryName);

            yield return Path.Combine(Environment.CurrentDirectory, LibraryName);
            yield return Path.Combine(AppContext.BaseDirectory, LibraryName);
        }

        private static bool TryLoadLibrary(out nint handle)
        {
            foreach (string candidate in SearchPaths())
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                    return true;
            }

            return NativeLibrary.TryLoad(LibraryName, out handle);
        }

        private static T? GetExport<T>(string name) where T : Delegate =>
            NativeLibrary.TryGetExport(_handle, name, out nint address)
                ? Marshal.GetDelegateForFunctionPointer<T>(address)
                : null;

        /// <summary>
        /// Builds MOPP code for a mesh.
        /// </summary>
        /// <param name="subShapeVertexCounts">
        /// Vertex counts per sub-shape, for a packed shape made of several pieces.
        /// Pass null for a single shape.
        /// </param>
        /// <returns>
        /// The MOPP code, along with the origin and scale that go with it, or null
        /// when the library is unavailable or declined to build a tree.
        /// </returns>
        public static MoppCode? Generate(
            IReadOnlyList<NifVector3> vertices,
            IReadOnlyList<NifTriangle> triangles,
            IReadOnlyList<int>? subShapeVertexCounts = null)
        {
            if (!IsAvailable)
                return null;

            if (vertices.Count == 0 || triangles.Count == 0)
                return null;

            NifVector3[] vertexArray = vertices as NifVector3[] ?? [.. vertices];
            NifTriangle[] triangleArray = triangles as NifTriangle[] ?? [.. triangles];

            int length;

            if (subShapeVertexCounts is { Count: > 0 } && _generateMoppCodeWithSubshapes is not null)
            {
                int[] shapes = subShapeVertexCounts as int[] ?? [.. subShapeVertexCounts];

                length = _generateMoppCodeWithSubshapes(
                    shapes.Length, shapes, vertexArray.Length, vertexArray, triangleArray.Length, triangleArray);
            }
            else
            {
                length = _generateMoppCode!(
                    vertexArray.Length, vertexArray, triangleArray.Length, triangleArray);
            }

            if (length <= 0)
                return null;

            byte[] code = new byte[length];

            // A zero return means the library refused to hand the code back.
            if (_retrieveMoppCode!(length, code) == 0)
                return null;

            _retrieveMoppScale!(out float scale);
            _retrieveMoppOrigin!(out NifVector3 origin);

            return new MoppCode(code, origin, scale);
        }
    }

    /// <summary>A generated MOPP tree, with the origin and scale it was built against.</summary>
    public sealed record MoppCode(byte[] Code, NifVector3 Origin, float Scale);
}
