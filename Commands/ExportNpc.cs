using LeanMeshIO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using NIFSharp;
using SECmd.Npc;
using HKSK.Model;
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

            Option<DirectoryInfo?> meshesOption = new("--meshes", "-m")
            {
                Description = "An extracted 'meshes' folder holding the animation cache, "
                    + "so the creature's clips can be found",
                DefaultValueFactory = _ => null,
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
                meshesOption,
                keepOption,
            };

            command.SetAction(result => Execute(
                result.GetValue(idOption)!,
                result.GetValue(outOption)!,
                result.GetValue(dataOption)!,
                result.GetValue(meshesOption),
                result.GetValue(keepOption)));

            root.Subcommands.Add(command);
        }

        /// <summary>Every texture the extracted meshes name, once each.</summary>
        /// <remarks>
        /// Read out of the NIFs rather than asked of the plugin, because the plugin
        /// does not know: a record names an armour, the armour names a mesh, and only
        /// the mesh names the image. A slot a shape left empty is skipped, and so is a
        /// path already seen -- a draugr's six shapes share three texture sets between
        /// them.
        /// </remarks>
        private static List<string> TexturesOf(
            IEnumerable<AssetExtractor.Found> found, NifXmlDatabase? database)
        {
            database ??= NifXmlDatabase.LoadEmbedded();

            var wanted = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (AssetExtractor.Found item in found)
            {
                if (item.Written is not { } path
                    || !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NifModel model;

                try
                {
                    model = NifModel.Load(path, database);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    continue;
                }

                foreach (NifItem block in model.Blocks)
                {
                    if (block.Name != "BSShaderTextureSet"
                        || model.FindItem(block, "Textures") is not { } textures)
                    {
                        continue;
                    }

                    foreach (NifItem slot in textures.Children)
                    {
                        string texture = slot.Value.AsString();

                        if (texture.Length > 0 && seen.Add(texture))
                            wanted.Add(texture);
                    }
                }
            }

            return wanted;
        }

        /// <summary>The actor a skeleton belongs to, from the folder it sits in.</summary>
        /// <remarks>
        /// `Actors\Draugr\Character Assets\Skeleton.nif` is the Draugr's. The
        /// folder above `character assets` is the name the animation cache uses.
        /// </remarks>
        private static string ActorName(string skeleton)
        {
            string[] parts = skeleton.Replace('/', '\\').Split('\\',
                StringSplitOptions.RemoveEmptyEntries);

            for (int i = parts.Length - 1; i > 0; i--)
                if (parts[i].Equals("character assets", StringComparison.OrdinalIgnoreCase))
                    return parts[i - 1];

            return parts.Length > 1 ? parts[^2] : "";
        }

        public static void Execute(
            string id, DirectoryInfo into, DirectoryInfo data,
            DirectoryInfo? meshes, bool keep)
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

            var meshBodies = found
                .Where(f => f.Written is not null
                    && f.Written != skeleton
                    && f.Written.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.Written!)
                .ToList();

            // The clips. A creature's animations hang off a Havok actor project in
            // the animation cache, which lives in an extracted `meshes` folder
            // rather than in an archive -- so it is asked for rather than assumed.
            //
            // Which project is the record's to say, not the folder's. The race names
            // a behaviour graph -- `Actors/Draugr/DraugrProject.hkx` -- and that is
            // the project. Reading it off the skeleton's folder instead is right for
            // a draugr by luck and wrong for most: a dog's skeleton sits in
            // `Character Assets Dog` while its project is `DogProject`, and a Nord's
            // project is `DefaultMale` or `DefaultFemale` depending on the NPC's sex,
            // which no folder name knows. Both came back with none of their clips.
            //
            // The folder still answers where the record does not, since a race with
            // no graph is one this has nothing better to go on for.
            ActorProject? project = null;
            string actor = npc.ProjectName ?? ActorName(npc.Skeleton);

            if (meshes is not null && Directory.Exists(meshes.FullName))
            {
                try
                {
                    SkyrimCache cache = SkyrimCache.Load(meshes.FullName);
                    project = cache.OpenActor(actor) ?? cache.OpenActor(actor + "Project");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    Console.WriteLine($"  the animation cache would not open: {error.Message}");
                }

                Console.WriteLine(project is null
                    ? $"  no Havok project for {actor} in that cache, so no clips"
                    : $"  clips from the {actor} project");
            }
            else
            {
                Console.WriteLine("  no --meshes given, so no clips");
            }

            // The textures the meshes name, beside the FBX rather than in the staging
            // folder. A NIF names them relative to Data -- `textures\actors\draugr\
            // Draugr.dds` -- and the FBX carries that spelling through, so a reader
            // resolves them against the FBX's own folder. Without them the creature
            // opens untextured and nothing says why: every image is there, pointing at
            // a file nobody fetched.
            List<string> textures = TexturesOf(found, database: null);

            if (textures.Count > 0)
            {
                List<AssetExtractor.Found> art = extractor.Extract(textures, into.FullName);
                int got = art.Count(a => a.Written is not null);

                Console.WriteLine($"  {got} of {textures.Count} textures");

                foreach (AssetExtractor.Found item in art.Where(a => a.Written is null))
                    Console.WriteLine($"    missing  {item.Relative}");
            }

            var assets = new CreatureAssets(npc.EditorId, skeleton, rig!, meshBodies, project!);

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
