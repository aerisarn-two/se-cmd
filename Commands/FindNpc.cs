using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using SECmd.Npc;
using System.CommandLine;

namespace SECmd.Commands
{
    /// <summary>
    /// <c>findnpc</c>: what files a creature is made of, and where to look it up.
    /// </summary>
    /// <remarks>
    /// The looking-up half of exporting a creature by name. Converting
    /// `meshes/actors/draugr` converts every draugr there is — seven bodies, four
    /// helmets, two beards, male and female — when what was wanted was the one
    /// creature the game assembles for a given NPC. The plugin knows which files
    /// those are; this says so before anything is converted.
    ///
    /// It also searches, because nobody remembers editor ids: `--like draugr`
    /// lists what there is to pick from.
    /// </remarks>
    internal static class FindNpc
    {
        public static void Register(RootCommand root)
        {
            Option<string?> idOption = new("--input", "-i")
            {
                Description = "The creature's editor id or form id",
            };

            Option<string?> likeOption = new("--like", "-l")
            {
                Description = "List creatures whose editor id contains this, rather than resolving one",
            };

            Option<DirectoryInfo> dataOption = new("--data", "-d")
            {
                Description = "The game's Data folder",
                DefaultValueFactory = _ => new DirectoryInfo(
                    GameEnvironment.Typical.Skyrim(SkyrimRelease.SkyrimSE).DataFolderPath.Path),
            };

            Command command = new("findnpc", "Say which files a creature is made of")
            {
                idOption,
                likeOption,
                dataOption,
            };

            command.SetAction(result => Execute(
                result.GetValue(idOption),
                result.GetValue(likeOption),
                result.GetValue(dataOption)!));

            root.Subcommands.Add(command);
        }

        public static void Execute(string? id, string? like, DirectoryInfo data)
        {
            if (id is null && like is null)
            {
                Console.WriteLine("Give --input an editor id or form id, or --like something to search for.");
                return;
            }

            using GameData game = GameData.Open(data.FullName);
            ILinkCache cache = game.LinkCache;

            if (like is not null)
            {
                Search(game, like);
                return;
            }

            NpcAssets? assets = NpcAssets.Resolve(cache, id!);

            if (assets is null)
            {
                Console.WriteLine($"No NPC answers to \"{id}\". Try --like to search.");
                return;
            }

            Report(assets);
        }

        private static void Search(GameData game, string like)
        {
            int found = 0;

            foreach (INpcGetter npc in game.Winning<INpcGetter>())
            {
                if (npc.EditorID is null
                    || !npc.EditorID.Contains(like, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Console.WriteLine($"  {npc.EditorID,-40} {npc.FormKey}");

                if (++found >= 40)
                {
                    Console.WriteLine("  ... and more; narrow the search");
                    break;
                }
            }

            if (found == 0)
                Console.WriteLine($"  nothing with {like} in its editor id");
        }

        private static void Report(NpcAssets assets)
        {
            Console.WriteLine($"{assets.EditorId}  {assets.FormKey}");
            Console.WriteLine($"  race     {assets.RaceEditorId}, {(assets.Female ? "female" : "male")}");
            Console.WriteLine($"  skeleton {assets.Skeleton ?? "(the race names none)"}");

            if (assets.Parts.Count == 0)
            {
                Console.WriteLine("  no body parts: neither the NPC nor its race names a skin");
                return;
            }

            Console.WriteLine($"  {assets.Parts.Count} part(s):");

            foreach (NpcPart part in assets.Parts)
                Console.WriteLine($"    {part.Model,-52} from {part.From}");
        }
    }
}
