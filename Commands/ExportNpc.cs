using LeanMeshIO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using NIFSharp;
using SECmd.Npc;
using SKAssets.Export;
using System.CommandLine;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>exportnpc</c>: one creature, named by its record, as one FBX.
    /// </summary>
    /// <remarks>
    /// The whole chain in one command. The plugin says which files the creature is
    /// made of (<see cref="NpcAssets"/>), the archives are asked for those files
    /// (<see cref="AssetExtractor"/>), and the creature conversion that already
    /// exists turns them into a scene.
    ///
    /// Converting `meshes/actors/draugr` instead converts every draugr there is.
    /// This converts the one the game would build.
    /// </remarks>
    internal static class ExportNpc
    {
        public static void Register(RootCommand root)
        {
            Option<string> idOption = new("--input", "-i")
            {
                Description = "The creature's editor id or form id",
                Required = true,
            };

            Option<DirectoryInfo> outOption = new("--output", "-o")
            {
                Description = "Where the FBX and the files it was built from go",
                DefaultValueFactory = _ => new DirectoryInfo(
                    Path.Combine(Environment.CurrentDirectory, "out")),
            };

            Option<DirectoryInfo> dataOption = new("--data", "-d")
            {
                Description = "The game's Data folder",
                DefaultValueFactory = _ => new DirectoryInfo(
                    GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE).DataFolderPath.Path),
            };

            Option<bool> keepOption = new("--keep")
            {
                Description = "Leave the extracted game files beside the FBX",
                DefaultValueFactory = _ => true,
            };

            Command command = new("exportnpc", "Export the creature a record configures, as one FBX")
            {
                idOption,
                outOption,
                dataOption,
                keepOption,
            };

            command.SetAction(result => Execute(
                result.GetValue(idOption)!,
                result.GetValue(outOption)!,
                result.GetValue(dataOption)!,
                result.GetValue(keepOption)));

            root.Subcommands.Add(command);
        }

        public static void Execute(string id, DirectoryInfo into, DirectoryInfo data, bool keep)
        {
            Console.WriteLine($"se-cmd {Build.Version} exportnpc, {DateTime.Now:yyyy-MM-dd HH:mm}");

            using GameData game = GameData.Open(data.FullName);
            NpcAssets? npc = NpcAssets.Resolve(game.LinkCache, id);

            if (npc is null)
            {
                Console.WriteLine($"  no NPC answers to \"{id}\"; try findnpc --like");
                return;
            }

            Console.WriteLine($"  {npc.EditorId} ({npc.FormKey}), {npc.RaceEditorId}, "
                + $"{(npc.Female ? "female" : "male")}");

            if (npc.Skeleton is null)
            {
                Console.WriteLine("  its race names no skeleton, so there is nothing to build on");
                return;
            }

            Directory.CreateDirectory(into.FullName);
            string staging = Path.Combine(into.FullName, npc.EditorId + ".files");

            // Everything the record named, plus the Havok half of the skeleton,
            // which no record names because it is found beside the NIF.
            var wanted = new List<string>();

            foreach (string mesh in npc.Meshes)
                wanted.Add(Path.Combine("meshes", mesh));

            wanted.Add(Path.Combine("meshes", npc.SkeletonHavok!));

            var extractor = new AssetExtractor(data.FullName);
            List<AssetExtractor.Found> found = extractor.Extract(wanted, staging);

            foreach (AssetExtractor.Found item in found)
            {
                Console.WriteLine(item.Written is null
                    ? $"    missing  {item.Relative}"
                    : $"    {item.From,-8} {item.Relative}");
            }

            string? skeleton = found.FirstOrDefault(
                f => f.Relative.EndsWith(npc.Skeleton, StringComparison.OrdinalIgnoreCase)).Written;

            if (skeleton is null)
            {
                Console.WriteLine("  the skeleton is not in this install; nothing to convert");
                return;
            }

            string? rig = found.FirstOrDefault(
                f => f.Relative.EndsWith(npc.SkeletonHavok!, StringComparison.OrdinalIgnoreCase)).Written;

            var meshes = found
                .Where(f => f.Written is not null
                    && f.Written != skeleton
                    && f.Written.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Written!)
                .ToList();

            var assets = new CreatureAssets(npc.EditorId, skeleton, rig!, meshes, null!);

            var database = NifXmlDatabase.LoadEmbedded();
            FbxDocument scene = CreatureExchange.Export(assets, database, out CreatureReport report);

            string target = Path.Combine(into.FullName, npc.EditorId + ".fbx");
            scene.Save(target);

            Console.WriteLine($"  {report}");

            foreach ((string mesh, string why) in report.Unmerged)
                Console.WriteLine($"    {mesh} would not merge: {why}");

            Console.WriteLine($"  -> {target}");

            if (!keep && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }
}
