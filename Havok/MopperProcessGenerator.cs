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
        public CompressedMeshResult? GenerateCompressedMesh(
            IReadOnlyList<MoppGeometry> geometries, int materials = 1)
        {
            if (!IsAvailable || geometries.Count == 0)
                return null;

            // -ccmm carries the material table, so Havok can give each chunk the
            // material of the triangles that built it. A mopper without it -- the stock
            // niftools build, where -ccm is upstream and -ccmm is not -- writes nothing
            // this can read, so the old command is tried after it.
            //
            // Asked once per binary, not once per shape. Attempt retries three times
            // before giving up, so probing on every mesh made an older mopper pay nine
            // failed runs for each one and took a corpus sweep from one minute to five.
            if (SupportsMaterials)
            {
                if (Attempt(() => ParseCompressedMeshOutput(
                        Run("-ccmm", BuildCompressedMeshInput(geometries, materials)))) is { } carried)
                {
                    // It worked, so this binary has the option. Nothing that happens
                    // to a later mesh can unsay that.
                    _materialsProven = true;
                    return carried;
                }

                // Only when it declined, never when it merely failed.
                //
                // This latch is process-wide and one-way: it exists to ask a binary
                // once whether it has -ccmm, since probing per mesh made an older
                // mopper pay nine failed runs for every shape. But it was flipped by
                // any failure at all, and three timeouts under load are a failure. One
                // congested mesh early in a run therefore turned -ccmm off for
                // everything after it, and every later multi-material collision was
                // refused outright -- 1,564 of the 1,876 compressed-mesh shapes in a
                // whole-corpus sweep lost their collision that way, all but five of
                // them without mopper having been run at all.
                //
                // A missing option is deterministic: the process starts and exits
                // non-zero, every time. A timeout or a broken pipe is not, and says
                // nothing about what this binary supports.
                //
                // Nor does anything, once -ccmm has been seen to work. That is the
                // fault this latch actually had: a shape Havok will not index fails
                // -ccmm exactly as a mopper without -ccmm does, so the first
                // unbuildable mesh in a run was read as a binary that cannot carry
                // materials, and every multi-material collision after it was refused
                // without mopper being asked. Over the whole corpus that turned some
                // eighty meshes Havok genuinely rejects into 1,572 that lost their
                // collision.
                if (!Transient && !_materialsProven)
                    _supportsMaterials = false;
            }

            // Falling back with several materials would silently merge them into one
            // substance, which is worse than saying so.
            if (materials > 1)
            {
                _reason = "this mopper has no -ccmm, so a collision mesh with more than "
                    + "one material cannot be built. Rebuild mopper from the fork that has it.";

                return null;
            }

            return Attempt(() => ParseCompressedMeshOutput(Run("-ccm", BuildCompressedMeshInput(geometries))));
        }

        /// <summary>
        /// Serialises geometries into mopper's <c>-ccm</c> input: a geometry count,
        /// then per geometry a vertex list and a triangle list.
        /// </summary>
        internal static string BuildCompressedMeshInput(
            IReadOnlyList<MoppGeometry> geometries, int? materials = null)
        {
            var text = new StringBuilder();

            // The table comes first under -ccmm, and not at all under -ccm.
            if (materials is { } count)
                text.Append(count).Append('\n');

            text.Append(geometries.Count).Append('\n');

            foreach (MoppGeometry geometry in geometries)
            {
                if (materials is not null)
                    text.Append(geometry.Material).Append('\n');

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

            /// <remarks>
            /// Every line the backend means as output is one number, so a line that
            /// is not one is not output. Havok reports through a callback while it
            /// works -- inconsistent triangle winding, which the game's own meshes
            /// provoke by the dozen -- and an older mopper prints those to stdout,
            /// where they land in the middle of the numbers. One dock building
            /// produced 96 such lines among 17,637 numbers, and the whole collision
            /// was dropped as a generation that failed.
            ///
            /// Filtering whole lines rather than tokens is what makes this safe: a
            /// warning has numbers *in* it, so anything finer would read "triangle 40"
            /// as the number 40.
            /// </remarks>
            public NumberReader(string text) =>
                _lines = [.. text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => float.TryParse(
                        line, NumberStyles.Float, CultureInfo.InvariantCulture, out _))];

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
            Failures = [];
            Transient = false;

            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    if (generate() is { } result)
                        return result;

                    Note(attempt, "produced nothing this caller could read"
                                  + (Diagnostics is { Length: > 0 } said ? $": {said}" : string.Empty));
                }
                catch (MoppBackendException e)
                {
                    Note(attempt, e.Message);
                }
                catch (TimeoutException e)
                {
                    // Try again: the usual cause is contention, not this geometry.
                    Transient = true;
                    Note(attempt, e.Message);
                }
                catch (IOException e)
                {
                    // As above -- a pipe that broke rather than a shape that cannot
                    // be built.
                    Transient = true;
                    Note(attempt, e.Message);
                }

                if (attempt + 1 < Attempts)
                    Thread.Sleep(TimeSpan.FromMilliseconds(200 * (attempt + 1)));
            }

            return null;
        }

        /// <summary>
        /// Records one attempt that did not work, whether or not a later one does.
        /// </summary>
        /// <remarks>
        /// A retry that succeeds hides the attempt that did not, and a backend that
        /// *crashed* on the first try is worth knowing about even when the second
        /// worked: it means some model kills it, and the only way to find which is to
        /// be told while the model is in hand.
        /// </remarks>
        private static void Note(int attempt, string what) =>
            Failures = [.. Failures, $"attempt {attempt + 1} of {Attempts} {what}"];

        /// <inheritdoc/>
        public IReadOnlyList<string> LastFailures => Failures;

        [ThreadStatic]
        private static IReadOnlyList<string>? _failures;

        private static IReadOnlyList<string> Failures
        {
            get => _failures ?? [];
            set => _failures = value;
        }

        /// <summary>
        /// How many times a generation is tried before it counts as impossible.
        /// </summary>
        /// <remarks>
        /// A backend that fails because of the shape fails every time; one that fails
        /// because forty of it are running at once does not. `smdetod02` converts on
        /// its own, five times out of five, and lost its whole collision object part
        /// way through a sweep of the corpus -- so the same input did not produce the
        /// same file twice, which is worse than a shape that is always missing.
        /// </remarks>
        /// <summary>Whether the resolved mopper understands <c>-ccmm</c>.</summary>
        /// <remarks>
        /// Assumed until one run says otherwise, then remembered. The flag is this
        /// fork's; a stock niftools mopper has only <c>-ccm</c>.
        /// </remarks>
        private static bool SupportsMaterials => _supportsMaterials;


        private static volatile bool _supportsMaterials = true;

        /// <summary>Whether a -ccmm run has ever succeeded in this process.</summary>
        private static volatile bool _materialsProven;

        /// <summary>
        /// Whether the last <see cref="Attempt"/> failed for a reason that might not
        /// happen again -- a timeout or a broken pipe rather than a refusal.
        /// </summary>
        /// <remarks>
        /// A caller that means to conclude something lasting from a failure has to know
        /// this. A backend that timed out says nothing about what the backend can do.
        /// </remarks>
        [ThreadStatic]
        private static bool Transient;

        private const int Attempts = 3;

        /// <summary>
        /// How many backends may run at once.
        /// </summary>
        /// <remarks>
        /// A sweep converts meshes in parallel and most of them want a MOPP tree, so
        /// without this there are as many Wine processes as the thread pool feels like
        /// starting. They contend for the same wineserver, and what comes back is a
        /// timeout, or a connection reset, on a shape that converts perfectly well on
        /// its own — `mrkmillbase01` did exactly that, ten times out of ten alone and
        /// once in a sweep of the corpus.
        ///
        /// A quarter of the cores. Not a fix: a reset is contention, and running fewer
        /// at once only makes it less likely. The retry is what makes it survivable
        /// and the report is what makes it visible; this just reduces how often either
        /// is needed.
        ///
        /// Override with <c>SECMD_MOPP_CONCURRENCY</c>, which is the knob to reach for
        /// when a machine disagrees with the guess — either way round.
        /// </remarks>
        private static readonly SemaphoreSlim Running = new(Concurrency());

        /// <summary>How many backends this machine should run at once.</summary>
        private static int Concurrency()
        {
            if (int.TryParse(
                    Environment.GetEnvironmentVariable("SECMD_MOPP_CONCURRENCY"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int configured)
                && configured > 0)
            {
                return configured;
            }

            return Math.Max(1, Environment.ProcessorCount / 4);
        }

        private string Run(string mode, string input)
        {
            Running.Wait();

            try
            {
                return Execute(mode, input);
            }
            finally
            {
                Running.Release();
            }
        }

        private string Execute(string mode, string input)
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

            try
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The backend is already gone -- it died before it had read its input.
                // "Broken pipe" is what that looks like from here and says nothing
                // useful; the exit code below says what actually happened, so let the
                // process be waited on rather than reporting the symptom.
            }

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
            // on its own a failure; the output parse decides. It is kept rather than
            // dropped, because when the parse does fail this is the only account of
            // why there is.
            Diagnostics = Meaningful(stderr.Result);

            // An exit code is the one thing that says the backend *died* rather than
            // declining. A crash under Wine leaves a plausible-looking empty stdout,
            // and without this it is indistinguishable from a shape Havok would not
            // index -- so a model that crashes mopper looks exactly like a model
            // mopper dislikes, and neither gets found.
            if (process.ExitCode != 0)
            {
                throw new MoppBackendException(
                    $"{mode} exited with code {process.ExitCode}"
                    + (Diagnostics is { Length: > 0 } said ? $": {said}" : string.Empty));
            }

            return stdout.Result;
        }

        /// <inheritdoc/>
        public string? LastDiagnostics => Diagnostics;

        [ThreadStatic]
        private static string? Diagnostics;

        /// <summary>Chatter about running Wine rather than about the shape.</summary>
        private static readonly System.Text.RegularExpressions.Regex WineChatter =
            new(@"^[0-9a-f]{3,4}:(fixme|err|warn|trace):", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The backend's own words, with the noise of running it stripped out.
        /// </summary>
        private static string? Meaningful(string stderr)
        {
            var lines = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => !WineChatter.IsMatch(line))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (lines.Count == 0)
                return null;

            // Havok repeats itself: one badly wound mesh is ninety-six near-identical
            // complaints. Enough to know what it is, not enough to bury the warning
            // this is attached to.
            const int Keep = 3;

            return lines.Count <= Keep
                ? string.Join("; ", lines)
                : string.Join("; ", lines.Take(Keep)) + $" (and {lines.Count - Keep} more)";
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
