using HKFBX.Hkx;
using HKFBX.Model;
using NIFSharp;
using SKAssets.Export;
using System.CommandLine;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>exportcreature</c>: a creature's two skeleton files as one FBX.
    /// </summary>
    /// <remarks>
    /// The other end of <see cref="ImportCreature"/>. A creature's skeleton is stored
    /// twice and neither copy is complete -- <c>skeleton.nif</c> has the bone tree,
    /// the shapes, the bodies and the constraints, <c>skeleton.hkx</c> has the
    /// animation rig, the ragdoll and the mappers -- and a DCC tool can open neither.
    /// `SkeletonExchange` folds them into one scene, and over the 45 creatures that
    /// ship both files with a ragdoll the trip back reproduces all 45 byte for byte.
    ///
    /// The `.hkx` is optional and it costs something to leave out: without it the
    /// ragdoll bone names and the rig's bone list have to be guessed rather than read,
    /// which is right for a creature being authored from nothing and wrong for one
    /// being edited.
    /// </remarks>
    internal static class ExportCreature
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo> meshOption = new("--input", "-i")
            {
                Description = "The creature's skeleton.nif",
                Required = true
            };

            Option<FileInfo?> havokOption = new("--havok", "-k")
            {
                Description = "Its skeleton.hkx. Without one the ragdoll names and the "
                    + "rig's bone list are inferred rather than read"
            };

            Option<FileInfo?> outputOption = new("--output", "-o")
            {
                Description = "Where the FBX is written. Defaults to the NIF's name beside it"
            };

            Command command = new(
                "exportcreature",
                "Fold a creature's skeleton.nif and skeleton.hkx into one FBX")
            {
                meshOption, havokOption, outputOption
            };

            command.SetAction(parseResult => Execute(
                parseResult.GetValue(meshOption)!,
                parseResult.GetValue(havokOption),
                parseResult.GetValue(outputOption)));

            root.Subcommands.Add(command);
        }

        private static int Execute(FileInfo mesh, FileInfo? havok, FileInfo? output)
        {
            foreach (FileInfo? file in new[] { mesh, havok })
            {
                if (file is not null && !file.Exists)
                {
                    Console.Error.WriteLine($"{file.FullName}: no such file");
                    return 1;
                }
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

            NifModel model = NifModel.Load(mesh.FullName, database);

            foreach (string warning in model.Warnings)
                Console.Error.WriteLine($"{mesh.Name}: {warning}");

            SkeletonFile? skeleton = havok is null ? null : HkxSkeletonFile.Read(havok.FullName);

            string path = output?.FullName
                ?? Path.ChangeExtension(mesh.FullName, ".fbx");

            SkeletonExchange.Export(model, skeleton).Save(path);

            Console.WriteLine($"{mesh.Name}{(havok is null ? "" : " + " + havok.Name)} -> {path}");

            return 0;
        }
    }
}
