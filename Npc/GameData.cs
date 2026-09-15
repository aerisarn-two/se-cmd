using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SECmd.Npc
{
    /// <summary>
    /// The game's master files, opened straight out of a Data folder.
    /// </summary>
    /// <remarks>
    /// Rather than <c>GameEnvironment</c>, which works out the load order from the
    /// launcher's `plugins.txt` in the user's profile. That file is written by the
    /// game, so on a machine that only holds the data — a build box, a Linux box,
    /// anywhere the game has not been run — it does not exist, and the environment
    /// throws before it reads a single record.
    ///
    /// The masters are enough for looking a creature up, and their order is fixed
    /// and public: Skyrim, then Update, then the three add-ons. Anything absent is
    /// skipped, so a plain Skyrim install works and so does a full one.
    /// </remarks>
    internal sealed class GameData : IDisposable
    {
        /// <summary>The masters, in the order the game loads them.</summary>
        private static readonly string[] Masters =
        [
            "Skyrim.esm",
            "Update.esm",
            "Dawnguard.esm",
            "HearthFires.esm",
            "Dragonborn.esm",
        ];

        private readonly List<IDisposable> _open = [];

        public required IReadOnlyList<ISkyrimModGetter> Mods { get; init; }

        public required ILinkCache LinkCache { get; init; }

        public required string DataFolder { get; init; }

        public static GameData Open(string dataFolder)
        {
            var mods = new List<ISkyrimModGetter>();
            var open = new List<IDisposable>();

            foreach (string name in Masters)
            {
                string path = Path.Combine(dataFolder, name);

                if (!File.Exists(path))
                    continue;

                var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                mods.Add(mod);

                if (mod is IDisposable disposable)
                    open.Add(disposable);
            }

            if (mods.Count == 0)
                throw new FileNotFoundException($"No Skyrim masters in {dataFolder}");

            var data = new GameData
            {
                Mods = mods,
                DataFolder = dataFolder,
                LinkCache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>(),
            };

            data._open.AddRange(open);

            return data;
        }

        /// <summary>Every record of a kind, later masters winning.</summary>
        public IEnumerable<T> Winning<T>()
            where T : class, IMajorRecordGetter
        {
            var seen = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();

            for (int i = Mods.Count - 1; i >= 0; i--)
                foreach (T record in Mods[i].EnumerateMajorRecords<T>())
                    if (seen.Add(record.FormKey))
                        yield return record;
        }

        public void Dispose()
        {
            foreach (IDisposable item in _open)
                item.Dispose();

            _open.Clear();
        }
    }
}
