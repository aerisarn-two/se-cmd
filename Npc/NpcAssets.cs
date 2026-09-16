using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SECmd.Npc
{
    /// <summary>
    /// The files a creature is made of, worked out from its record rather than
    /// from a folder somebody picked.
    /// </summary>
    /// <remarks>
    /// A creature is not a folder. `meshes/actors/draugr` holds every draugr
    /// variant, male and female, seven bodies, four helmets and two beards, and
    /// converting the folder converts all of it — where what was wanted was the
    /// one creature the game actually assembles for a given NPC.
    ///
    /// The plugin says which: an NPC names a race, the race names a skeleton and a
    /// default skin, and the skin's armour addons name the body parts for that
    /// race and sex. Following those links gives the set the game itself would
    /// load, and nothing else.
    /// </remarks>
    internal sealed class NpcAssets
    {
        public required string EditorId { get; init; }

        public required FormKey FormKey { get; init; }

        public required string RaceEditorId { get; init; }

        public bool Female { get; init; }

        /// <summary>The rig, from the race's skeletal model.</summary>
        public string? Skeleton { get; init; }

        /// <summary>The behaviour graph the race names, which is its Havok project.</summary>
        /// <remarks>
        /// The record says which project an actor animates with, and nothing else
        /// does. Naming it after the folder the skeleton sits in is right for a
        /// draugr by luck and wrong for most: a dog's skeleton is in
        /// `Actors/Canine/Character Assets Dog` and its project is `DogProject`, a
        /// wolf's is `Actors/Canine/Character Assets Wolf` and `WolfProject`, and a
        /// Nord's is `Actors/Character/Character Assets` with the project depending
        /// on which sex the NPC is -- `DefaultMale` or `DefaultFemale`, which no
        /// folder name can tell you. Guessing cost the dog and every human all 
        /// their clips.
        /// </remarks>
        public string? BehaviorGraph { get; init; }

        /// <summary>The Havok project's name, as the animation cache spells it.</summary>
        /// <remarks>
        /// The record writes a Windows path -- `Actors\Draugr\DraugrProject.hkx` --
        /// and `Path.GetFileNameWithoutExtension` only knows the separator it is
        /// running on, so off Windows it hands the whole thing back. The separators
        /// are squared up first.
        /// </remarks>
        public string? ProjectName =>
            BehaviorGraph is null
                ? null
                : Path.GetFileNameWithoutExtension(BehaviorGraph.Replace('\\', '/'));

        /// <summary>The Havok half, beside the skeleton and named by convention.</summary>
        public string? SkeletonHavok =>
            Skeleton is null ? null : Path.ChangeExtension(Skeleton, ".hkx");

        /// <summary>Each body part, with the record it came from.</summary>
        public List<NpcPart> Parts { get; } = [];

        /// <summary>Everything to convert, in the order it was found.</summary>
        public IEnumerable<string> Meshes =>
            (Skeleton is null ? Array.Empty<string>() : [Skeleton])
            .Concat(Parts.Select(p => p.Model))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Resolves a creature by editor id or form id.
        /// </summary>
        /// <remarks>
        /// Both, because both are how people refer to these. An editor id is what a
        /// human reads in the Creation Kit; a form id is what a save file and a
        /// wiki page carry, and is the only handle for the many records that have
        /// no editor id at all.
        /// </remarks>
        public static NpcAssets? Resolve(ILinkCache cache, string id)
        {
            INpcGetter? npc = Find(cache, id);

            if (npc is null)
                return null;

            bool female = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

            if (!cache.TryResolve<IRaceGetter>(npc.Race.FormKey, out IRaceGetter? race))
                return null;

            var assets = new NpcAssets
            {
                EditorId = npc.EditorID ?? "(no editor id)",
                FormKey = npc.FormKey,
                RaceEditorId = race.EditorID ?? "(no editor id)",
                Female = female,
                Skeleton = female
                    ? race.SkeletalModel?.Female?.File?.GivenPath
                    : race.SkeletalModel?.Male?.File?.GivenPath,
                BehaviorGraph = female
                    ? race.BehaviorGraph?.Female?.File?.GivenPath
                    : race.BehaviorGraph?.Male?.File?.GivenPath,
            };

            // What it wears, if anything, and otherwise what its race wears. A
            // draugr has no worn armour of its own: its body is the race's skin.
            IArmorGetter? skin = null;

            if (!npc.WornArmor.IsNull
                && cache.TryResolve<IArmorGetter>(npc.WornArmor.FormKey, out IArmorGetter? worn))
            {
                skin = worn;
            }
            else if (!race.Skin.IsNull
                && cache.TryResolve<IArmorGetter>(race.Skin.FormKey, out IArmorGetter? raceSkin))
            {
                skin = raceSkin;
            }

            if (skin is not null)
                Collect(cache, skin, race, female, assets, "skin");

            // What it is wearing on top. A draugr's helmet is not part of its
            // body: it is an armour in the outfit the NPC is handed, and without
            // it the creature comes out bare-headed.
            if (!npc.DefaultOutfit.IsNull
                && cache.TryResolve<IOutfitGetter>(npc.DefaultOutfit.FormKey, out IOutfitGetter? outfit))
            {
                foreach (var item in outfit.Items)
                {
                    if (cache.TryResolve<IArmorGetter>(item.FormKey, out IArmorGetter? piece))
                        Collect(cache, piece, race, female, assets, "outfit");
                }
            }

            // And the head, which for a humanoid is where the hair, the eyes and
            // the beard live. A draugr has none of these and a bandit has five.
            foreach (var link in npc.HeadParts)
            {
                if (!cache.TryResolve<IHeadPartGetter>(link.FormKey, out IHeadPartGetter? part))
                    continue;

                if (part.Model?.File?.GivenPath is { Length: > 0 } model)
                {
                    assets.Parts.Add(new NpcPart
                    {
                        Model = model,
                        From = $"head part {part.EditorID ?? part.FormKey.ToString()}",
                    });
                }
            }

            return assets;
        }

        /// <summary>The armour's addons, as the models for this race and sex.</summary>
        private static void Collect(
            ILinkCache cache,
            IArmorGetter armour,
            IRaceGetter race,
            bool female,
            NpcAssets assets,
            string why)
        {
            foreach (var link in armour.Armature)
            {
                if (!cache.TryResolve<IArmorAddonGetter>(link.FormKey, out IArmorAddonGetter? addon))
                    continue;

                // An addon lists the races it fits. One that does not fit this race
                // is somebody else's body part sharing the same armour record.
                bool fits = addon.Race.FormKey == race.FormKey
                    || addon.AdditionalRaces.Any(r => r.FormKey == race.FormKey);

                if (!fits)
                    continue;

                string? model = female
                    ? addon.WorldModel?.Female?.File?.GivenPath
                    : addon.WorldModel?.Male?.File?.GivenPath;

                if (string.IsNullOrWhiteSpace(model))
                    continue;

                assets.Parts.Add(new NpcPart
                {
                    Model = model,
                    From = $"{why}: {armour.EditorID ?? armour.FormKey.ToString()} / "
                        + (addon.EditorID ?? addon.FormKey.ToString()),
                });
            }
        }

        /// <summary>By editor id, or by form id in any of the spellings people use.</summary>
        private static INpcGetter? Find(ILinkCache cache, string id)
        {
            if (cache.TryResolve<INpcGetter>(id, out INpcGetter? byEditorId))
                return byEditorId;

            if (TryFormKey(id, out FormKey key)
                && cache.TryResolve<INpcGetter>(key, out INpcGetter? byFormKey))
            {
                return byFormKey;
            }

            return null;
        }

        /// <summary>
        /// A form id as written down: `0x0001A694`, `1A694`, or with its plugin.
        /// </summary>
        /// <remarks>
        /// The bare hex form is the one people quote, and it carries no plugin, so
        /// the master it belongs to has to be guessed from the load order index in
        /// its top byte — which is what <c>FormKey.Factory</c> wants spelled out.
        /// Every base game record is Skyrim.esm, and the DLC records people look up
        /// carry their own prefix, so the common case resolves and the rest are
        /// asked for in full.
        /// </remarks>
        public static bool TryFormKey(string id, out FormKey key)
        {
            if (FormKey.TryFactory(id, out key))
                return true;

            string hex = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? id[2..]
                : id;

            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint raw))
                return false;

            string plugin = (raw >> 24) switch
            {
                0x01 => "Update.esm",
                0x02 => "Dawnguard.esm",
                0x03 => "HearthFires.esm",
                0x04 => "Dragonborn.esm",
                _ => "Skyrim.esm",
            };

            return FormKey.TryFactory($"{raw & 0x00FFFFFF:X6}:{plugin}", out key);
        }
    }

    /// <summary>One mesh a creature wears, and the records that named it.</summary>
    internal sealed class NpcPart
    {
        public required string Model { get; init; }

        public required string From { get; init; }
    }
}
