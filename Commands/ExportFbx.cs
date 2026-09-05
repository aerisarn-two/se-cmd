using LeanMeshIO;
using NIFSharp;
using System.CommandLine;
using SECmd.Conversion;
using SECmd.Fbx;
using SECmd.Nif;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>exportfbx</c>: converts one or more NIF files to FBX.
    /// </summary>
    internal static class ExportFbx
    {
        public static void Register(RootCommand root)
        {
            Option<FileInfo[]> inputOption = new("--input", "-i")
            {
                Description = "NIF files to convert",
                Required = true,
                AllowMultipleArgumentsPerToken = true
            };

            Option<DirectoryInfo> outputOption = new("--output", "-o")
            {
                Description = "Output directory",
                DefaultValueFactory = _ => new DirectoryInfo(Environment.CurrentDirectory)
            };

            Option<string> texturePathOption = new("--textures", "-t")
            {
                Description = "Prefix prepended to texture paths written into materials",
                DefaultValueFactory = _ => string.Empty
            };

            Command command = new("exportfbx", "Convert NIF files to FBX")
            {
                inputOption,
                outputOption,
                texturePathOption
            };

            command.SetAction(parseResult => Execute(
                parseResult.GetValue(inputOption)!,
                parseResult.GetValue(outputOption)!,
                parseResult.GetValue(texturePathOption)!));

            root.Subcommands.Add(command);
        }

        private static int Execute(FileInfo[] inputs, DirectoryInfo outputFolder, string texturePath)
        {
            // Parsing nif.xml is the expensive part of a conversion, so do it once
            // however many files were given.
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

            var options = new NifToFbxOptions { TexturePath = texturePath };
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
                    ConvertOne(input, outputFolder, database, options);
                }
                catch (Exception e) when (e is NifFormatException or IOException)
                {
                    // One bad file should not abandon the rest of a batch.
                    Console.Error.WriteLine($"{input.Name}: {e.Message}");
                    failures++;
                }
            }

            return failures == 0 ? 0 : 1;
        }

        private static void ConvertOne(
            FileInfo input, DirectoryInfo outputFolder, NifXmlDatabase database, NifToFbxOptions options)
        {
            NifModel model = NifModel.Load(input.FullName, database);

            foreach (string warning in model.Warnings)
                Console.Error.WriteLine($"{input.Name}: {warning}");

            var converter = new NifToFbx(model, options);
            FbxDocument document = converter.Convert();

            foreach (string warning in converter.Warnings)
                Console.Error.WriteLine($"{input.Name}: {warning}");

            string outputPath = Path.Combine(
                outputFolder.FullName,
                Path.GetFileNameWithoutExtension(input.Name) + ".fbx");

            document.Save(outputPath);

            Console.WriteLine($"{input.Name} -> {outputPath}");
        }
    }
}
