using HKFBX.Codec;
using HKFBX.Fbx;
using HKFBX.Hkx;
using HKFBX.Model;
using HKSK.Model;
using LeanMeshIO;
using NIFSharp;
using SKAssets.Export;
using System.CommandLine;
using System.Text;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>convert</c>: whatever you dropped on it, converted, with a log saying what
    /// it found and what it did.
    /// </summary>
    /// <remarks>
    /// The other commands each want one kind of thing in one direction, which is
    /// right when you know what you have and tiresome when you are looking. This one
    /// takes files and folders in any mixture, asks SKAssets what each of them is,
    /// and converts it the way that kind is converted -- NIF and Havok out to FBX,
    /// FBX back to NIF and Havok, a creature's folder out as one scene holding all of
    /// it.
    ///
    /// It is also what happens when files are dropped on the executable, since that
    /// hands the program a list of paths and nothing else.
    ///
    /// Everything lands in one <c>out</c> folder and everything is written down. A
    /// converter that quietly skips half of what it was given is worse than one that
    /// refuses, so every input gets a line: what it was taken to be, what came of it,
    /// and where a conversion needed something that was not there.
    /// </remarks>
    internal static class Convert
    {
        public static void Register(RootCommand root)
        {
            Argument<string[]> inputs = new("paths")
            {
                Description = "Files and folders to convert. Anything not recognised is reported, not guessed at",
                Arity = ArgumentArity.OneOrMore
            };

            Option<DirectoryInfo?> outputOption = new("--output", "-o")
            {
                Description = "Where the results go. Defaults to an 'out' folder beside the first input"
            };

            Option<DirectoryInfo?> meshesOption = new("--meshes", "-m")
            {
                Description = "An extracted 'meshes' folder holding the animation cache, so a creature's "
                    + "clips can be found. Looked for above the input when not given"
            };

            Option<FileInfo?> templateOption = new("--template", "-t")
            {
                Description = "A skeleton.hkx to write a scene's Havok half into. A packfile is edited "
                    + "from an original rather than built, so without one the Havok half is reported and skipped"
            };

            Option<bool> recurseOption = new("--recurse", "-r")
            {
                Description = "Walk into folders that are not creatures",
                DefaultValueFactory = _ => true
            };

            Command command = new("convert", "Convert anything: NIF, HKX, FBX, or a creature's folder")
            {
                inputs, outputOption, meshesOption, templateOption, recurseOption
            };

            command.SetAction(parseResult => Execute(
                parseResult.GetValue(inputs)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(meshesOption),
                parseResult.GetValue(templateOption),
                parseResult.GetValue(recurseOption)));

            root.Subcommands.Add(command);
        }

        private static int Execute(
            string[] paths, DirectoryInfo? output, DirectoryInfo? meshes, FileInfo? template, bool recurse)
        {
            NifXmlDatabase database;

            try
            {
                database = NifXmlDatabase.LoadEmbedded();
            }
            catch (NifFormatException e)
            {
                Console.Error.WriteLine($"could not load the NIF format description: {e.Message}");
                return 1;
            }

            DirectoryInfo into = output ?? new DirectoryInfo(Path.Combine(FolderOf(paths[0]), "out"));
            into.Create();

            SkyrimCache? cache = OpenCache(meshes?.FullName ?? CacheAbove(paths[0]));
            var log = new Log(into);

            log.Say($"se-cmd convert, {DateTime.Now:yyyy-MM-dd HH:mm}");
            log.Say($"  into {into.FullName}");
            log.Say(cache is null
                ? "  no animation cache found, so a creature's clips will be skipped"
                : $"  animation cache: {cache.ProjectNames.Count()} projects");
            log.Say(string.Empty);

            foreach (string path in Walk(paths, database, cache, recurse, log))
                One(path, database, cache, into, template, log);

            log.Say(string.Empty);
            log.Say($"{log.Converted} converted, {log.Skipped} skipped, {log.Failed} failed");
            log.Close();

            return log.Failed > 0 ? 1 : 0;
        }

        /// <summary>
        /// The paths to work on, with folders opened where they are not creatures.
        /// </summary>
        private static IEnumerable<string> Walk(
            string[] paths, NifXmlDatabase database, SkyrimCache? cache, bool recurse, Log log)
        {
            foreach (string path in paths)
            {
                if (!Directory.Exists(path))
                {
                    yield return path;
                    continue;
                }

                if (AssetRecognition.Of(path, database, cache).Kind == AssetKind.Creature || !recurse)
                {
                    yield return path;
                    continue;
                }

                // A folder that is not a creature is a bag of files, and the useful
                // thing to do with it is the useful thing for each file in it.
                string[] inside = [.. Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(file => Converts(Path.GetExtension(file)))
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)];

                log.Say($"{Name(path)}: a folder, {inside.Length} file(s) to look at");

                foreach (string creature in Creatures(path, database, cache, log))
                    yield return creature;

                foreach (string file in inside.Where(f => !UnderACreature(f, path, database, cache)))
                    yield return file;
            }
        }

        /// <summary>The creature folders inside a folder, so each is taken whole.</summary>
        private static IEnumerable<string> Creatures(
            string path, NifXmlDatabase database, SkyrimCache? cache, Log log)
        {
            foreach (string folder in Directory
                .EnumerateFiles(path, "skeleton.nif", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (AssetRecognition.Of(folder, database, cache).Kind == AssetKind.Creature)
                    yield return folder;
            }
        }

        /// <summary>Whether a file belongs to a creature folder already being taken whole.</summary>
        private static bool UnderACreature(string file, string root, NifXmlDatabase database, SkyrimCache? cache)
        {
            string? folder = Path.GetDirectoryName(file);

            while (folder is not null && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(folder, "skeleton.nif"))) return true;

                // A creature's animations sit beside its character assets, not in
                // them, so the folder above counts as well.
                if (Directory.Exists(Path.Combine(folder, "character assets"))
                    && File.Exists(Path.Combine(folder, "character assets", "skeleton.nif")))
                {
                    return true;
                }

                folder = Path.GetDirectoryName(folder);
            }

            return false;
        }

        /// <summary>The extensions this converts, which is what a drop is checked against.</summary>
        public static bool Converts(string extension) =>
            extension.ToLowerInvariant() is ".nif" or ".hkx" or ".fbx";

        /// <summary>
        /// Whether a path is something to convert: a folder, or a file of a kind
        /// this reads.
        /// </summary>
        public static bool Handles(string path) =>
            Directory.Exists(path) || (File.Exists(path) && Converts(Path.GetExtension(path)));

        /// <summary>One input, recognised and converted.</summary>
        private static void One(
            string path, NifXmlDatabase database, SkyrimCache? cache,
            DirectoryInfo into, FileInfo? template, Log log)
        {
            RecognisedAsset what = AssetRecognition.Of(path, database, cache);
            log.Say($"{Name(path)}: {what.Summary}");

            try
            {
                switch (what.Kind)
                {
                    case AssetKind.Creature:
                        Creature(what, database, into, log);
                        break;

                    case AssetKind.Mesh:
                        Mesh(path, database, into, log);
                        break;

                    case AssetKind.HavokSkeleton:
                        HavokSkeleton(path, into, log);
                        break;

                    case AssetKind.HavokAnimation:
                        HavokAnimation(path, into, log);
                        break;

                    case AssetKind.Scene:
                        Scene(path, what, database, cache, into, template, log);
                        break;

                    default:
                        log.Skip(what.Problem ?? "nothing this converts");
                        break;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log.Fail(e.Message);
            }
        }

        private static void Creature(RecognisedAsset what, NifXmlDatabase database, DirectoryInfo into, Log log)
        {
            CreatureAssets assets = what.Creature!;
            string target = Path.Combine(into.FullName, assets.Name + ".fbx");

            FbxDocument scene = CreatureExchange.Export(assets, database, out CreatureReport report);
            scene.Save(target);

            log.Say($"    {report}");

            foreach ((string mesh, string why) in report.Unmerged)
                log.Say($"    {mesh} would not merge: {why}");

            if (report.Unbound.Count > 0)
                log.Say($"    bound to nothing: {string.Join(", ", report.Unbound.Take(8))}");

            log.Wrote(target);
        }

        private static void Mesh(string path, NifXmlDatabase database, DirectoryInfo into, Log log)
        {
            string target = Path.Combine(into.FullName, Path.GetFileNameWithoutExtension(path) + ".fbx");

            NifModel model = NifModel.Load(path, database);

            foreach (string warning in model.Warnings)
                log.Say($"    {warning}");

            new NIFBX.Conversion.NifToFbx(model).Convert().Save(target);
            log.Wrote(target);
        }

        private static void HavokSkeleton(string path, DirectoryInfo into, Log log)
        {
            string target = Path.Combine(into.FullName, Path.GetFileNameWithoutExtension(path) + ".fbx");

            FbxSkeletonWriter.Build(HkxSkeletonFile.Read(path)).Save(target);
            log.Wrote(target);
        }

        /// <summary>
        /// A clip on its own, over the rig it was authored against.
        /// </summary>
        /// <remarks>
        /// The rig is not in the animation file and has to be found: it is the
        /// skeleton.hkx of the creature the clip sits under. Without it there is
        /// nothing to bind the tracks to, and an FBX of unbound curves is not worth
        /// writing.
        ///
        /// The travel is not in the file either -- it is in the cache -- so a clip
        /// converted on its own animates in place. Converting the creature instead
        /// takes the travel with it.
        /// </remarks>
        private static void HavokAnimation(string path, DirectoryInfo into, Log log)
        {
            if (RigFor(path) is not { } rig)
            {
                log.Skip("no skeleton.hkx above it, and a clip cannot be rigged to nothing");
                return;
            }

            string target = Path.Combine(into.FullName, Path.GetFileNameWithoutExtension(path) + ".fbx");

            (SplineAnimationData spline, IReadOnlyList<short> trackToBone, _) =
                HkxAnimationFile.ReadAnimation(path);

            Skeleton skeleton = HkxAnimationFile.ReadSkeleton(rig);
            SampledAnimation animation = new MopperAnimationCodec().Decompress(spline);

            FbxAnimationWriter.Build(
                skeleton,
                animation with { TrackToBone = trackToBone },
                Path.GetFileNameWithoutExtension(path)).Save(target);

            log.Say($"    rigged to {Name(rig)}; the travel stays in the cache, so it animates in place");
            log.Wrote(target);
        }

        private static void Scene(
            string path, RecognisedAsset what, NifXmlDatabase database, SkyrimCache? cache,
            DirectoryInfo into, FileInfo? template, Log log)
        {
            SceneContents contents = what.Contents!;

            if (contents.IsEmpty)
            {
                log.Skip("the scene holds no mesh, no rig and no clips");
                return;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            ActorProject? project = cache?.OpenActor(name + "Project") ?? cache?.OpenActor(name);

            FbxDocument document;
            using (FileStream stream = File.OpenRead(path)) document = FbxDocument.Load(stream);

            if (contents.HasClips && project is null)
                log.Say("    it carries clips and no project was found for it, so they stay in the scene");

            CreatureImport back = CreatureExchange.Import(document, database, project);
            DirectoryInfo folder = into.CreateSubdirectory(name);

            foreach ((string file, NifModel model) in back.Meshes.OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                string target = Path.Combine(folder.FullName, file);
                model.Save(target);
                log.Wrote(target);
            }

            if (back.Havok is { } havok)
            {
                string? original = template?.FullName ?? RigFor(path);

                if (original is null)
                {
                    log.Skip("a skeleton.hkx is edited from an original and none was given; "
                        + "pass --template to write the Havok half");
                }
                else
                {
                    string target = Path.Combine(folder.FullName, "skeleton.hkx");
                    HkxSkeletonFile.Write(original, havok, target);
                    log.Say($"    Havok half written from {Name(original)}");
                    log.Wrote(target);
                }
            }

            if (back.Clips is { } clips)
            {
                log.Say($"    {clips}");
                log.Also(clips.Clips.Count);

                foreach ((string stack, string why) in clips.Failed)
                    log.Say($"    {stack} would not write back: {why}");

                cache!.Save();
                log.Say("    the animation cache was saved where it was read from");
            }
        }

        /// <summary>The rig a file sits under, if any folder above it holds one.</summary>
        private static string? RigFor(string path)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(path));

            while (folder is not null)
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(folder, "skeleton.hkx"),
                    Path.Combine(folder, "character assets", "skeleton.hkx"),
                })
                {
                    if (File.Exists(candidate)) return candidate;
                }

                folder = Path.GetDirectoryName(folder);
            }

            return null;
        }

        /// <summary>The extracted meshes folder above a path, if there is one.</summary>
        private static string? CacheAbove(string path)
        {
            string? folder = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));

            while (folder is not null)
            {
                if (File.Exists(Path.Combine(folder, SkyrimCache.AnimationDataFileName)))
                    return folder;

                folder = Path.GetDirectoryName(folder);
            }

            return null;
        }

        private static SkyrimCache? OpenCache(string? meshes)
        {
            if (meshes is null || !Directory.Exists(meshes)) return null;

            try { return SkyrimCache.Load(meshes); }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        }

        private static string FolderOf(string path) =>
            Directory.Exists(path)
                ? Path.GetFullPath(path)
                : Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory;

        /// <summary>
        /// What to call a path in the log.
        /// </summary>
        /// <remarks>
        /// The last two segments for a folder, because the interesting half of
        /// <c>chicken/character assets</c> is the half every creature shares a name
        /// with the others in.
        /// </remarks>
        private static string Name(string path)
        {
            string trimmed = Path.TrimEndingDirectorySeparator(path);

            if (!Directory.Exists(path))
                return Path.GetFileName(trimmed);

            string? above = Path.GetDirectoryName(trimmed);

            return above is null
                ? Path.GetFileName(trimmed)
                : Path.Combine(Path.GetFileName(above), Path.GetFileName(trimmed));
        }

        /// <summary>
        /// What happened, on the console and in a file beside the results.
        /// </summary>
        /// <remarks>
        /// Both, because the two are read at different times: the console while it
        /// runs, the file when somebody asks why a creature came out without its
        /// animations. A converter that leaves no account of itself makes that
        /// question unanswerable.
        /// </remarks>
        private sealed class Log(DirectoryInfo into)
        {
            private readonly StringBuilder _text = new();

            public int Converted { get; private set; }

            public int Skipped { get; private set; }

            public int Failed { get; private set; }

            public void Say(string line)
            {
                Console.WriteLine(line);
                _text.AppendLine(line);
            }

            public void Wrote(string path)
            {
                Converted++;
                Say($"    -> {Path.GetFileName(path)}");
            }

            /// <summary>Counts work already described in a line of its own.</summary>
            public void Also(int count) => Converted += count;

            public void Skip(string why)
            {
                Skipped++;
                Say($"    skipped: {why}");
            }

            public void Fail(string why)
            {
                Failed++;
                Say($"    failed: {why}");
            }

            public void Close()
            {
                string path = Path.Combine(into.FullName, "conversion-log.txt");

                try { File.WriteAllText(path, _text.ToString()); }
                catch (IOException e) { Console.Error.WriteLine($"could not write the log: {e.Message}"); }
            }
        }
    }
}
