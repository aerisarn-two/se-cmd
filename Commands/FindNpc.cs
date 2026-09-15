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

            Option<string?> usedOption = new("--used", "-u")
            {
                Description = "Count how often each creature of a race is actually placed in the world",
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
                usedOption,
                dataOption,
            };

            command.SetAction(result => Execute(
                result.GetValue(idOption),
                result.GetValue(likeOption),
                result.GetValue(usedOption),
                result.GetValue(dataOption)!));

            root.Subcommands.Add(command);
        }

        public static void Execute(string? id, string? like, string? used, DirectoryInfo data)
        {
            if (id is null && like is null && used is null)
            {
                Console.WriteLine("Give --input an editor id or form id, --like something to search "
                    + "for, or --used a race to count placements of.");
                return;
            }

            using GameData game = GameData.Open(data.FullName);
            ILinkCache cache = game.LinkCache;

            if (like is not null)
            {
                Search(game, like);
                return;
            }

            if (used is not null)
            {
                Used(game, used);
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

        /// <summary>
        /// How often each creature of a race is actually put in the world.
        /// </summary>
        /// <remarks>
        /// "Which draugr" has an answer the plugin can give: every placed actor
        /// names the NPC it is an instance of, so counting them says which record
        /// the game leans on. A record with no placements is a template something
        /// else is levelled from; one with hundreds is the one a player meets.
        /// </remarks>
        private static void Used(GameData game, string raceLike)
        {
            var wanted = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, INpcGetter>();

            foreach (INpcGetter npc in game.Winning<INpcGetter>())
            {
                if (!game.LinkCache.TryResolve<IRaceGetter>(npc.Race.FormKey, out IRaceGetter? race))
                    continue;

                if (race.EditorID is null
                    || !race.EditorID.Contains(raceLike, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                wanted[npc.FormKey] = npc;
            }

            Console.WriteLine($"  {wanted.Count} NPC record(s) of a race like \"{raceLike}\"");

            var placements = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, int>();

            foreach (IPlacedNpcGetter placed in game.Winning<IPlacedNpcGetter>())
            {
                var key = placed.Base.FormKey;

                if (wanted.ContainsKey(key))
                    placements[key] = placements.GetValueOrDefault(key) + 1;
            }

            Console.WriteLine($"  placed directly: {placements.Count} of them");

            // And through levelled lists, which is how a player actually meets
            // most of them: a dungeon places a list, the game picks from it. A
            // record placed nowhere directly can still be the commonest one in the
            // game, and the two counts answer different questions -- what is put
            // in the world by hand, and what turns up when it is played.
            var listHolds = new Dictionary<Mutagen.Bethesda.Plugins.FormKey,
                HashSet<Mutagen.Bethesda.Plugins.FormKey>>();

            var lists = game.Winning<ILeveledNpcGetter>().ToList();

            foreach (ILeveledNpcGetter list in lists)
                listHolds[list.FormKey] = Contents(game, list, lists.ToDictionary(l => l.FormKey), [], wanted);

            var byList = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, int>();
            int listBases = 0;
            int holding = listHolds.Count(h => h.Value.Count > 0);

            foreach (IPlacedNpcGetter placed in game.Winning<IPlacedNpcGetter>())
            {
                if (!listHolds.TryGetValue(placed.Base.FormKey, out var holds))
                    continue;

                listBases++;

                foreach (var npcKey in holds)
                    byList[npcKey] = byList.GetValueOrDefault(npcKey) + 1;
            }

            var total = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, (int Direct, int ByList)>();

            foreach (var key in placements.Keys.Concat(byList.Keys).Distinct())
            {
                total[key] = (placements.GetValueOrDefault(key), byList.GetValueOrDefault(key));
            }

            Console.WriteLine($"  named by {holding} levelled list(s), "
                + $"of which {listBases} are placed as references");

            if (holding > 0 && listBases == 0)
            {
                Console.WriteLine("  (so the levelled column is zero: these masters place NPC "
                    + "records and template them from lists, rather than placing the lists)");
            }
            Console.WriteLine();
            Console.WriteLine($"    {"direct",6} {"levelled",9}  {"editor id",-44} form id");

            foreach (var (key, counts) in total
                         .OrderByDescending(t => t.Value.Direct + t.Value.ByList)
                         .Take(15))
            {
                Console.WriteLine($"    {counts.Direct,6} {counts.ByList,9}  "
                    + $"{wanted[key].EditorID,-44} {key}");
            }
        }

        /// <summary>Which wanted NPCs a levelled list can produce, nesting included.</summary>
        private static HashSet<Mutagen.Bethesda.Plugins.FormKey> Contents(
            GameData game,
            ILeveledNpcGetter list,
            IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, ILeveledNpcGetter> lists,
            HashSet<Mutagen.Bethesda.Plugins.FormKey> seen,
            IReadOnlyDictionary<Mutagen.Bethesda.Plugins.FormKey, INpcGetter> wanted)
        {
            var found = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();

            if (!seen.Add(list.FormKey) || list.Entries is null)
                return found;

            foreach (var entry in list.Entries)
            {
                var key = entry.Data?.Reference.FormKey;

                if (key is null)
                    continue;

                if (wanted.ContainsKey(key.Value))
                    found.Add(key.Value);
                else if (lists.TryGetValue(key.Value, out ILeveledNpcGetter? nested))
                    found.UnionWith(Contents(game, nested, lists, seen, wanted));
            }

            return found;
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
