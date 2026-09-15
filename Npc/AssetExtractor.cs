using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace SECmd.Npc
{
    /// <summary>
    /// Pulls a creature's files out of the game's archives onto disk.
    /// </summary>
    /// <remarks>
    /// The conversion works on files, and a creature's files are inside BSAs. A
    /// loose file on disk wins over the archived one, exactly as the game reads
    /// them, so an install with a mod's replacement body extracts that body.
    ///
    /// Everything lands under the folder it has in the game, because that is what
    /// the rest of the tool expects to be handed and what a texture path inside a
    /// NIF is written relative to.
    /// </remarks>
    internal sealed class AssetExtractor
    {
        private readonly string _data;
        private readonly List<IArchiveReader> _archives = [];

        public AssetExtractor(string dataFolder)
        {
            _data = dataFolder;

            // Meshes first, then everything else: a file is looked for in the
            // archives most likely to hold it before the rest are opened.
            foreach (string path in Directory.GetFiles(dataFolder, "*.bsa")
                         .OrderByDescending(p => Path.GetFileName(p)
                             .Contains("Meshes", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    _archives.Add(Archive.CreateReader(GameRelease.SkyrimSE, path));
                }
                catch (Exception)
                {
                    // An archive this build cannot read is not worth failing over;
                    // whatever is missing is reported by the caller as missing.
                }
            }
        }

        /// <summary>Where a file came from, or that it was not found.</summary>
        public readonly record struct Found(string Relative, string? Written, string From);

        /// <summary>
        /// Writes each wanted file under <paramref name="into"/>, keeping its path.
        /// </summary>
        /// <param name="wanted">Paths relative to Data, in the game's spelling.</param>
        public List<Found> Extract(IEnumerable<string> wanted, string into)
        {
            var results = new List<Found>();

            foreach (string relative in wanted)
            {
                string normal = relative.Replace('/', '\\').TrimStart('\\');
                string target = Path.Combine(into, normal.Replace('\\', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // A loose file wins, as it does in the game.
                string loose = Path.Combine(_data, normal.Replace('\\', Path.DirectorySeparatorChar));

                if (File.Exists(loose))
                {
                    File.Copy(loose, target, overwrite: true);
                    results.Add(new Found(relative, target, "loose"));
                    continue;
                }

                bool done = false;

                foreach (IArchiveReader archive in _archives)
                {
                    foreach (var file in archive.Files)
                    {
                        if (!file.Path.Replace('/', '\\')
                                .Equals(normal, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        File.WriteAllBytes(target, file.GetBytes());
                        results.Add(new Found(relative, target, "archive"));
                        done = true;
                        break;
                    }

                    if (done)
                        break;
                }

                if (!done)
                    results.Add(new Found(relative, null, "not found"));
            }

            return results;
        }
    }
}
