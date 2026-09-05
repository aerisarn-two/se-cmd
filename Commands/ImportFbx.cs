using LeanMeshIO;
using System.CommandLine;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>importfbx</c>: converts one or more FBX files to NIF.
    /// </summary>
    internal static class ImportFbx
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo[]> inputOption = new("--input", "-i")
            {
                Description = "FBX files to convert",
                Required = true,
                AllowMultipleArgumentsPerToken = true
            };

            Option<DirectoryInfo> outputOption = new("--output", "-o")
            {
                Description = "Output directory",
                DefaultValueFactory = _ => new DirectoryInfo(Environment.CurrentDirectory)
            };

            Option<bool> invertUOption = new("--invert-u")
            {
                Description = "Mirror the U texture coordinate",
                DefaultValueFactory = _ => false
            };

            Option<bool> keepVOption = new("--keep-v")
            {
                Description = "Do not mirror V. NIF's V axis is normally the other way up, so leave this off unless the result looks wrong",
                DefaultValueFactory = _ => false
            };

            Option<bool> legendaryOption = new("--le")
            {
                Description = "Target Skyrim Legendary Edition (stream version 83, NiTriShape geometry). "
                    + "The default is Special Edition (stream version 100, BSTriShape geometry)",
                DefaultValueFactory = _ => false
            };

            Command command = new("importfbx", "Convert FBX files to NIF")
            {
                inputOption,
                outputOption,
                invertUOption,
                keepVOption,
                legendaryOption
            };

            command.SetAction(parseResult => Execute(
                parseResult.GetValue(inputOption)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(invertUOption),
                !parseResult.GetValue(keepVOption),
                parseResult.GetValue(legendaryOption)));

            root.Subcommands.Add(command);
        }

        private static int Execute(
            FileInfo[] inputs, DirectoryInfo outputFolder, bool invertU, bool invertV, bool legendaryEdition)
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

            outputFolder.Create();

            int failures = 0;

            foreach (FileInfo input in inputs)
            {
                if (!input.Exists)
                {
                    Console.Error.WriteLine($"{input.FullName}: no such file");
                    failures++;
                    continue;
                }

                try
                {
                    ConvertOne(input, outputFolder, database, invertU, invertV, legendaryEdition);
                }
                catch (Exception e) when (e is NifFormatException or IOException or NotSupportedException)
                {
                    Console.Error.WriteLine($"{input.Name}: {e.Message}");
                    failures++;
                }
            }

            return failures == 0 ? 0 : 1;
        }

        private static void ConvertOne(
            FileInfo input,
            DirectoryInfo outputFolder,
            NifXmlDatabase database,
            bool invertU,
            bool invertV,
            bool legendaryEdition)
        {
            var scene = new FbxScene(FbxDocument.Load(input.FullName));

            var converter = new FbxToNif(scene, new FbxToNifOptions
            {
                InvertU = invertU,
                InvertV = invertV,
                LegendaryEdition = legendaryEdition,

                // The root is named after the file, not after any node in the scene.
                RootName = Path.GetFileNameWithoutExtension(input.Name)
            });

            NifModel model = converter.Convert(database);

            foreach (string warning in converter.Warnings)
                Console.Error.WriteLine($"{input.Name}: {warning}");

            string outputPath = Path.Combine(
                outputFolder.FullName,
                Path.GetFileNameWithoutExtension(input.Name) + ".nif");

            model.Save(outputPath);

            Console.WriteLine($"{input.Name} -> {outputPath}");
        }
    }
}
