using HKFBX.Hkx;
using LeanMeshIO;
using NIFSharp;
using SKAssets.Export;
using System.CommandLine;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>importcreature</c>: one authored FBX back into the files the game reads.
    /// </summary>
    /// <remarks>
    /// A creature's skeleton is stored twice and neither copy is complete.
    /// <c>skeleton.nif</c> has the bone tree, the collision shapes, the bodies and the
    /// constraints; <c>skeleton.hkx</c> has the animation rig, the ragdoll and the
    /// mappers between them. `SkeletonExchange` in SKAssets folds both into one FBX
    /// and takes them apart again, and the taking-apart is what this exposes -- the
    /// conversion belongs to that library, and this is the command line over it.
    ///
    /// **A `skeleton.hkx` cannot be written from an FBX alone.** HKFBX is explicit
    /// about why: the file is a thousand objects and almost all of them are scaffolding
    /// -- motion states, collidables, broad phase handles, constraint atoms, the memory
    /// resource tree -- and rebuilding that from nothing means inventing values some
    /// original already has right. So the writer edits a template, and
    /// <c>--template</c> is which one. Re-importing a creature you exported, that is
    /// its own `skeleton.hkx`; authoring a new one, it is the vanilla creature whose
    /// rig is nearest.
    /// </remarks>
    internal static class ImportCreature
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo> inputOption = new("--input", "-i")
            {
                Description = "The creature's FBX, as written by exportcreature or a DCC tool",
                Required = true
            };

            Option<DirectoryInfo> outputOption = new("--output", "-o")
            {
                Description = "Where the files are written",
                DefaultValueFactory = _ => new DirectoryInfo(Environment.CurrentDirectory)
            };

            Option<FileInfo?> templateOption = new("--template", "-t")
            {
                Description = "An existing skeleton.hkx to write the Havok half into. "
                    + "Without one only skeleton.nif is written, since a packfile "
                    + "cannot be built from nothing"
            };

            Option<bool> flatOption = new("--flat")
            {
                Description = "Write the files side by side rather than in the game's folder layout",
                DefaultValueFactory = _ => false
            };

            Option<string> nameOption = new("--name", "-n")
            {
                Description = "The creature's name, for the folder layout. Defaults to the FBX's own",
                DefaultValueFactory = _ => string.Empty
            };

            Command command = new(
                "importcreature",
                "Take a creature FBX apart into the skeleton files the game reads")
            {
                inputOption, outputOption, templateOption, nameOption, flatOption
            };

            command.SetAction(parseResult => Execute(
                parseResult.GetValue(inputOption)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(templateOption),
                parseResult.GetValue(nameOption)!,
                parseResult.GetValue(flatOption)));

            root.Subcommands.Add(command);
        }

        private static int Execute(
            FileInfo input, DirectoryInfo output, FileInfo? template, string name, bool flat)
        {
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

            if (name.Length == 0)
                name = Path.GetFileNameWithoutExtension(input.Name);

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

            DirectoryInfo assets = flat
                ? output
                : output.CreateSubdirectory(
                    Path.Combine("meshes", "actors", name, "character assets"));

            // `CreateSubdirectory` makes the layout on the way down; a flat run has no
            // subdirectory to make and the folder given may still not exist.
            assets.Create();

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

            string nif = Path.Combine(assets.FullName, "skeleton.nif");
            SkeletonExchange.ImportMesh(document, database).Save(nif);
            Console.WriteLine($"skeleton.nif -> {nif}");

            if (template is null)
            {
                Console.Error.WriteLine(
                    "no --template given, so skeleton.hkx is not written: a Havok packfile "
                    + "is edited from an original rather than built, and there is nothing "
                    + "here to edit");

                return 0;
            }

            string hkx = Path.Combine(assets.FullName, "skeleton.hkx");
            HkxSkeletonFile.Write(template.FullName, SkeletonExchange.ImportHavok(document), hkx);
            Console.WriteLine($"skeleton.hkx -> {hkx}");

            return 0;
        }
    }
}
