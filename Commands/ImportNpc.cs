using HKFBX.Hkx;
using HKSK.Model;
using LeanMeshIO;
using NIFSharp;
using SKAssets.Export;
using System.CommandLine;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>importnpc</c>: a creature scene back into the files the game reads.
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="ExportNpc"/>. That command asks the plugin which
    /// files a creature is made of and folds all of them into one FBX; this takes
    /// such a scene apart again, whatever a DCC tool did to it in between.
    ///
    /// <c>importcreature</c> already imports a creature FBX and is not this. It is
    /// the command line over `SkeletonExchange`, which knows about the two skeleton
    /// files and nothing else, so it answers a scene holding a draugr's body, its
    /// hair and its armour with one `skeleton.nif` holding all of them -- which is
    /// not how the game keeps them and not what went in. `CreatureExchange` is the
    /// one that remembers: the export writes the file each node came from onto the
    /// node, and the import hands back one model per file. So the two commands are
    /// separate because the two libraries answer different questions, and the one
    /// to reach for is the one that matches how the scene was made.
    ///
    /// **The clips are reported, not written.** Writing an animation back means
    /// writing a packfile into the creature's project and adding a line to the
    /// character's animation list in the cache -- edits to an extracted game folder,
    /// not to the output directory. That is a different kind of act from writing a
    /// NIF into a folder of one's own, so it is asked for separately rather than
    /// done as a side effect of a conversion.
    /// </remarks>
    internal static class ImportNpc
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo> inputOption = new("--input", "-i")
            {
                Description = "The creature's FBX, as written by exportnpc or a DCC tool",
                Required = true,
            };

            Option<DirectoryInfo> outputOption = new("--output", "-o")
            {
                Description = "Where the files are written, laid out as the scene remembers them",
                DefaultValueFactory = _ => new DirectoryInfo(
                    Path.Combine(Environment.CurrentDirectory, "out")),
            };

            Option<FileInfo?> templateOption = new("--template", "-t")
            {
                Description = "An existing skeleton.hkx to write the Havok half into. "
                    + "Without one the ragdoll is read but not written, since a packfile "
                    + "is edited from an original rather than built",
            };

            Command command = new(
                "importnpc",
                "Take a creature FBX apart into every file it was built from")
            {
                inputOption, outputOption, templateOption,
            };

            command.SetAction(result => Execute(
                result.GetValue(inputOption)!,
                result.GetValue(outputOption)!,
                result.GetValue(templateOption)));

            root.Subcommands.Add(command);
        }

        private static int Execute(FileInfo input, DirectoryInfo into, FileInfo? template)
        {
            Console.WriteLine($"se-cmd {Build.Version} importnpc, {DateTime.Now:yyyy-MM-dd HH:mm}");

            if (!input.Exists)
            {
                Console.Error.WriteLine($"{input.FullName}: no such file");
                return 1;
            }

            if (template is not null && !template.Exists)
            {
                Console.Error.WriteLine($"{template.FullName}: no such file");
                return 1;
            }

            FbxDocument document;

            try
            {
                document = FbxDocument.Load(input.FullName);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"{input.Name}: {e.Message}");
                return 1;
            }

            SceneContents contents = CreatureExchange.Inspect(document);

            Console.WriteLine($"  the scene holds {contents}");

            if (contents.IsEmpty)
            {
                Console.Error.WriteLine(
                    "  nothing here is a creature: no mesh, no rig and no clips");

                return 1;
            }

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

            // No project, so the clips stay in the scene. What they are is already in
            // `contents`, and saying so is the point of printing it above.
            CreatureImport creature = CreatureExchange.Import(document, database);

            Directory.CreateDirectory(into.FullName);

            foreach ((string source, NifModel model) in creature.Meshes)
            {
                // The source is the path the file had under the creature's folder, so
                // a draugr's DLC armour lands back in its subfolder rather than beside
                // the body it is worn over.
                string target = Path.Combine(into.FullName, source.Replace('\\', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                model.Save(target);

                Console.WriteLine($"    {source}");
            }

            if (creature.Havok is null)
                return 0;

            if (template is null)
            {
                Console.Error.WriteLine(
                    "  the scene carries a rig, but no --template was given, so skeleton.hkx "
                    + "is not written: a Havok packfile is edited from an original rather "
                    + "than built, and there is nothing here to edit");

                return 0;
            }

            string hkx = Path.Combine(into.FullName, "skeleton.hkx");

            HkxSkeletonFile.Write(template.FullName, creature.Havok, hkx);
            Console.WriteLine($"    skeleton.hkx ({creature.Havok.Bodies.Count} bodies)");

            return 0;
        }
    }
}
