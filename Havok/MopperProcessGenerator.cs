using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SECmd.Nif;

namespace SECmd.Havok
{
    /// <summary>
    /// Generates MOPP code by running niftools' <c>mopper.exe</c> as a child process.
    /// </summary>
    /// <remarks>
    /// This is the portable backend. mopper is a Win32 executable, but it talks pure
    /// stdin/stdout with no GUI and no COM, so it runs unmodified under Wine — which
    /// is what makes MOPP generation possible on Linux at all. Running it
    /// out-of-process also sidesteps the bitness matching that in-process P/Invoke
    /// into NifMopp.dll requires.
    ///
    /// Contract (from mopper's own <c>--help</c>):
    /// <code>
    /// mopper.exe -msm --      read a simple mesh from stdin
    /// mopper.exe -ccm --      read geometries from stdin, build a compressed mesh
    /// </code>
    /// Input is whitespace-separated ASCII; output is one number per line.
    /// </remarks>
    public sealed class MopperProcessGenerator : IMoppGenerator
    {
        /// <summary>How long to let mopper run before giving up.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Path to <c>mopper.exe</c>. When null, it is looked for beside this
        /// executable and then on PATH.
        /// </summary>
        public string? MopperPath { get; set; }

        /// <summary>
        /// The launcher used on non-Windows hosts. Defaults to <c>wine</c>; set to an
        /// empty string to run the binary directly.
        /// </summary>
        public string WineCommand { get; set; } = "wine";

        private string? _resolvedPath;
        private bool _probed;
        private string? _reason;

        public string? UnavailableReason
        {
            get
            {
                Probe();
                return _reason;
            }
        }

        public bool IsAvailable
        {
            get
            {
                Probe();
                return _resolvedPath is not null;
            }
        }

        private static bool NeedsWine => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private void Probe()
        {
            if (_probed)
                return;

            _probed = true;
            _resolvedPath = ResolveMopper();

            if (_resolvedPath is null)
            {
                _reason = "mopper.exe was not found. Place it beside the executable, put it on PATH, "
                    + $"or set {nameof(MopperPath)}.";
                return;
            }

            if (NeedsWine && WineCommand.Length > 0 && ResolveOnPath(WineCommand) is null)
            {
                _resolvedPath = null;
                _reason = $"mopper.exe was found at \"{ResolveMopper()}\" but \"{WineCommand}\" is not "
                    + "installed, and it is a Windows binary. Install Wine to use it on this platform.";
            }
        }

        /// <summary>
        /// Where mopper.exe is looked for, in order: an explicitly configured path,
        /// the current working directory, then the directory holding the executable.
        /// </summary>
        /// <remarks>
        /// The working directory comes first so a copy sitting next to the files
        /// being converted wins over an installed one. If none of them has it, PATH
        /// is searched.
        /// </remarks>
        public IEnumerable<string> SearchPaths()
        {
            if (MopperPath is { Length: > 0 } configured)
            {
                yield return configured;
                yield break;
            }

            yield return Path.Combine(Environment.CurrentDirectory, "mopper.exe");
            yield return Path.Combine(AppContext.BaseDirectory, "mopper.exe");
        }

        private string? ResolveMopper()
        {
            foreach (string candidate in SearchPaths())
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            // An explicit path that does not exist is an error, not a reason to go
            // hunting on PATH for something the caller did not ask for.
            return MopperPath is { Length: > 0 } ? null : ResolveOnPath("mopper.exe");
        }

        private static string? ResolveOnPath(string fileName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");

            if (path is null)
                return null;

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (directory.Length == 0)
                    continue;

                string candidate = Path.Combine(directory, fileName);

                if (File.Exists(candidate))
                    return candidate;

                // On Unix a launcher such as wine has no extension.
                if (!fileName.Contains('.') && File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <inheritdoc/>
        public MoppResult? GenerateSimpleMesh(IReadOnlyList<NifVector3> vertices, IReadOnlyList<NifTriangle> triangles)
        {
            if (!IsAvailable || vertices.Count == 0 || triangles.Count == 0)
                return null;

            return Attempt(() => ParseSimpleMeshOutput(Run("-msm", BuildSimpleMeshInput(vertices, triangles))));
        }

        /// <inheritdoc/>
        public MoppResult? GenerateCollection(string description)
        {
            if (!IsAvailable || description.Length == 0)
                return null;

            // The same output as -msm, with the welding count zero: a collection of
            // primitives has no triangles to weld.
            return Attempt(() => ParseSimpleMeshOutput(Run("-clm", description)));
        }

        /// <inheritdoc/>
        public CompressedMeshResult? GenerateCompressedMesh(IReadOnlyList<MoppGeometry> geometries)
        {
            if (!IsAvailable || geometries.Count == 0)
                return null;

            return Attempt(() => ParseCompressedMeshOutput(Run("-ccm", BuildCompressedMeshInput(geometries))));
        }

        /// <summary>
        /// Serialises geometries into mopper's <c>-ccm</c> input: a geometry count,
        /// then per geometry a vertex list and a triangle list.
        /// </summary>
        internal static string BuildCompressedMeshInput(IReadOnlyList<MoppGeometry> geometries)
        {
            var text = new StringBuilder();

            text.Append(geometries.Count).Append('\n');

            foreach (MoppGeometry geometry in geometries)
            {
                text.Append(geometry.Vertices.Count).Append('\n');

                foreach (NifVector3 v in geometry.Vertices)
                {
                    text.Append(Format(v.X)).Append(' ')
                        .Append(Format(v.Y)).Append(' ')
                        .Append(Format(v.Z)).Append('\n');
                }

                text.Append(geometry.Triangles.Count).Append('\n');

                foreach (NifTriangle t in geometry.Triangles)
                    text.Append(t.V1).Append(' ').Append(t.V2).Append(' ').Append(t.V3).Append('\n');
            }

            return text.ToString();
        }

        /// <summary>
        /// Parses mopper's <c>-ccm</c> output, which is the whole compressed mesh
        /// shape rather than just a MOPP tree.
        /// </summary>
        /// <remarks>
        /// One quirk to undo: mopper prints the <em>last</em> MOPP byte first, then
        /// bytes 0..n-2. Reading it in order gives a rotated tree that Havok will
        /// not accept.
        /// </remarks>
        internal static CompressedMeshResult? ParseCompressedMeshOutput(string output)
        {
            var reader = new NumberReader(output);

            if (!reader.TryFloat(out float ox) || !reader.TryFloat(out float oy) || !reader.TryFloat(out float oz)
                || !reader.TryFloat(out float scale) || !reader.TryInt(out int codeLength))
            {
                return null;
            }

            if (codeLength <= 0)
                return null;

            byte[] code = new byte[codeLength];

            // The last byte comes first.
            if (!reader.TryInt(out int lastByte))
                return null;

            code[codeLength - 1] = (byte)lastByte;

            for (int i = 0; i < codeLength - 1; i++)
            {
                if (!reader.TryInt(out int value))
                    return null;

                code[i] = (byte)value;
            }

            if (!reader.TryVector4(out NifVector4 boundsMin) || !reader.TryVector4(out NifVector4 boundsMax))
                return null;

            if (!reader.TryInt(out int bigVertexCount))
                return null;

            var bigVertices = new List<NifVector4>(Math.Max(0, bigVertexCount));

            for (int i = 0; i < bigVertexCount; i++)
            {
                if (!reader.TryVector4(out NifVector4 v))
                    return null;

                bigVertices.Add(v);
            }

            if (!reader.TryInt(out int bigTriangleCount))
                return null;

            var bigTriangles = new List<(uint, uint, uint, uint, uint)>(Math.Max(0, bigTriangleCount));

            for (int i = 0; i < bigTriangleCount; i++)
            {
                if (!reader.TryUInt(out uint a) || !reader.TryUInt(out uint b) || !reader.TryUInt(out uint c)
                    || !reader.TryUInt(out uint material) || !reader.TryUInt(out uint welding))
                {
                    return null;
                }

                bigTriangles.Add((a, b, c, material, welding));
            }

            if (!reader.TryInt(out int transformCount))
                return null;

            var transforms = new List<CompressedMeshTransform>(Math.Max(0, transformCount));

            for (int i = 0; i < transformCount; i++)
            {
                if (!reader.TryVector4(out NifVector4 translation)
                    || !reader.TryFloat(out float rx) || !reader.TryFloat(out float ry)
                    || !reader.TryFloat(out float rz) || !reader.TryFloat(out float rw))
                {
                    return null;
                }

                transforms.Add(new CompressedMeshTransform(translation, new NifQuat(rw, rx, ry, rz)));
            }

            if (!reader.TryInt(out int chunkCount))
                return null;

            var chunks = new List<CompressedMeshChunk>(Math.Max(0, chunkCount));

            for (int i = 0; i < chunkCount; i++)
            {
                if (!reader.TryVector4(out NifVector4 offset)
                    || !reader.TryUInt(out uint materialInfo)
                    || !reader.TryUInt(out _)                      // a hard-coded 65535
                    || !reader.TryUInt(out uint transformIndex))
                {
                    return null;
                }

                if (!TryReadUShortList(reader, out var vertices)
                    || !TryReadUShortList(reader, out var indices)
                    || !TryReadUShortList(reader, out var stripLengths)
                    || !TryReadUShortList(reader, out var welding))
                {
                    return null;
                }

                chunks.Add(new CompressedMeshChunk(
                    offset, materialInfo, (ushort)transformIndex, vertices, indices, stripLengths, welding));
            }

            var mopp = new MoppResult(code, new NifVector3(ox, oy, oz), scale, []);

            return new CompressedMeshResult(
                mopp, boundsMin, boundsMax, bigVertices, bigTriangles, transforms, chunks);
        }

        private static bool TryReadUShortList(NumberReader reader, out List<ushort> values)
        {
            values = [];

            if (!reader.TryInt(out int count) || count < 0)
                return false;

            for (int i = 0; i < count; i++)
            {
                if (!reader.TryUInt(out uint value))
                    return false;

                values.Add((ushort)value);
            }

            return true;
        }

        /// <summary>Walks mopper's one-number-per-line output.</summary>
        private sealed class NumberReader
        {
            private readonly string[] _lines;
            private int _at;

            public NumberReader(string text) =>
                _lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            public bool TryFloat(out float value)
            {
                value = 0;

                if (_at >= _lines.Length)
                    return false;

                bool ok = float.TryParse(_lines[_at], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                _at++;
                return ok;
            }

            public bool TryInt(out int value)
            {
                value = 0;

                if (_at >= _lines.Length)
                    return false;

                bool ok = int.TryParse(_lines[_at], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                _at++;
                return ok;
            }

            public bool TryUInt(out uint value)
            {
                value = 0;

                if (!TryInt(out int signed))
                    return false;

                value = unchecked((uint)signed);
                return true;
            }

            public bool TryVector4(out NifVector4 value)
            {
                value = new NifVector4();

                if (!TryFloat(out float x) || !TryFloat(out float y) || !TryFloat(out float z) || !TryFloat(out float w))
                    return false;

                value = new NifVector4(x, y, z, w);
                return true;
            }
        }

        /// <summary>
        /// Serialises a mesh into mopper's input format: a vertex count and vertices,
        /// a triangle count and triangles, then a zero material-index count.
        /// </summary>
        internal static string BuildSimpleMeshInput(
            IReadOnlyList<NifVector3> vertices,
            IReadOnlyList<NifTriangle> triangles)
        {
            var text = new StringBuilder();

            text.Append(vertices.Count).Append('\n');

            foreach (NifVector3 v in vertices)
            {
                text.Append(Format(v.X)).Append(' ')
                    .Append(Format(v.Y)).Append(' ')
                    .Append(Format(v.Z)).Append('\n');
            }

            text.Append(triangles.Count).Append('\n');

            foreach (NifTriangle t in triangles)
                text.Append(t.V1).Append(' ').Append(t.V2).Append(' ').Append(t.V3).Append('\n');

            // mopper reads a material-index count next. It parses each index with
            // operator>> into a uint8, which reads a *character* rather than a
            // number, so anything non-zero here would be misread. Always send none.
            text.Append("0\n");

            return text.ToString();
        }

        /// <summary>
        /// Parses mopper's <c>-msm</c> output: origin, scale, code length, the code
        /// bytes as integers, then a triangle count and per-triangle welding info.
        /// </summary>
        internal static MoppResult? ParseSimpleMeshOutput(string output)
        {
            using var reader = new StringReader(output);
            var numbers = new List<string>();

            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();

                if (line.Length > 0)
                    numbers.Add(line);
            }

            int at = 0;

            if (!TryNextFloat(numbers, ref at, out float x)
                || !TryNextFloat(numbers, ref at, out float y)
                || !TryNextFloat(numbers, ref at, out float z)
                || !TryNextFloat(numbers, ref at, out float scale)
                || !TryNextInt(numbers, ref at, out int length))
            {
                // mopper prints Havok's error text on failure rather than numbers.
                return null;
            }

            if (length <= 0 || at + length > numbers.Count)
                return null;

            byte[] code = new byte[length];

            for (int i = 0; i < length; i++)
            {
                if (!TryNextInt(numbers, ref at, out int value))
                    return null;

                code[i] = (byte)value;
            }

            var welding = new List<ushort>();

            if (TryNextInt(numbers, ref at, out int weldingCount) && weldingCount > 0)
            {
                for (int i = 0; i < weldingCount && TryNextInt(numbers, ref at, out int value); i++)
                    welding.Add((ushort)value);
            }

            return new MoppResult(code, new NifVector3(x, y, z), scale, welding);
        }

        /// <summary>
        /// Runs a generation, turning a hung or broken backend into "no result".
        /// </summary>
        /// <remarks>
        /// The interface says null when generation was not possible, and a backend
        /// that timed out is exactly that. Letting the exception out instead ends the
        /// whole conversion over one shape, which for a sweep means one bad mesh
        /// stops the run.
        /// </remarks>
        private static T? Attempt<T>(Func<T?> generate)
            where T : class
        {
            try
            {
                return generate();
            }
            catch (TimeoutException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private string Run(string mode, string input)
        {
            string executable = NeedsWine && WineCommand.Length > 0 ? WineCommand : _resolvedPath!;

            var start = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (NeedsWine && WineCommand.Length > 0)
                start.ArgumentList.Add(_resolvedPath!);

            start.ArgumentList.Add(mode);
            start.ArgumentList.Add("--");

            using var process = Process.Start(start)
                ?? throw new InvalidOperationException($"could not start {executable}");

            // Read stdout on a separate task: mopper can emit more than a pipe buffer
            // holds, and writing stdin while it blocks on a full stdout would deadlock.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(input);
            process.StandardInput.Close();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                throw new TimeoutException($"mopper did not finish within {Timeout}.");
            }

            // A process exiting does not mean its pipes closed. Under Wine the
            // wineserver holds the write end open after the program is gone, and
            // reading to the end then blocks for ever -- there is no timeout on a
            // pipe read, so a sweep of the whole corpus simply stopped, three and a
            // half hours with one batch half done and nothing to say why.
            if (!Task.WhenAll(stdout, stderr).Wait(Timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone -- which is exactly the case this guards.
                }

                throw new TimeoutException(
                    $"mopper exited but its output did not close within {Timeout}.");
            }

            // Wine writes its own diagnostics to stderr, so a non-empty stderr is not
            // on its own a failure; the output parse decides.
            _ = stderr;
            return stdout.Result;
        }

        private static string Format(float value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        private static bool TryNextFloat(List<string> numbers, ref int at, out float value)
        {
            value = 0;

            if (at >= numbers.Count)
                return false;

            bool ok = float.TryParse(numbers[at], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            at++;
            return ok;
        }

        private static bool TryNextInt(List<string> numbers, ref int at, out int value)
        {
            value = 0;

            if (at >= numbers.Count)
                return false;

            bool ok = int.TryParse(numbers[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            at++;
            return ok;
        }
    }
}
