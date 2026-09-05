using NIFSharp;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a block's extra data through FBX as properties on its node.
    /// </summary>
    /// <remarks>
    /// Almost every NIF has some. <c>BSXFlags</c> alone appears on the root of nearly
    /// all of them and tells the engine whether the file has animation, collision, a
    /// ragdoll, an articulated body — dropping it changes what the game does with the
    /// mesh, and nothing about the file looks wrong afterwards.
    ///
    /// FBX has nowhere for any of it, so it rides along as properties, as constraints
    /// and particle systems do. The blocks are small and various — a flag word, a
    /// string, a bounding box, a behaviour graph path — so they are written field by
    /// field off the nif.xml definition rather than a case per class.
    /// </remarks>
    public static class FbxExtraDataWriter
    {
        /// <summary>The property counting the extra data blocks on a node.</summary>
        public const string CountProperty = "extra_data";

        /// <summary>Prefix on one block's fields, before its index.</summary>
        public const string Prefix = "xd_";

        /// <summary>
        /// Fields the rebuild supplies itself.
        /// </summary>
        /// <remarks>
        /// A block's own name is carried separately, and the controller chain is not
        /// carried at all: an extra data block with a controller is animated through
        /// the sequences, which travel by their own route.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Name", "Next Extra Data"
        };

        /// <summary>
        /// Blocks that travel by another route and must not travel twice.
        /// </summary>
        /// <remarks>
        /// <c>BSXFlags</c> is recalculated from the rebuilt graph rather than carried,
        /// because every bit of it is a fact about that graph. Carrying it as well
        /// would leave the file with two, and the engine reads the first it finds.
        /// </remarks>
        private static readonly HashSet<string> Elsewhere = new(StringComparer.Ordinal)
        {
            "BSXFlags"
        };

        /// <summary>Writes every extra data block a NIF block owns onto its node.</summary>
        /// <returns>The number written.</returns>
        public static int AddExtraData(FbxObject node, NifModel model, NifItem block)
        {
            var blocks = model.GetRefArray(block, "Extra Data List")
                .Where(b => !Elsewhere.Contains(b.Name))
                .ToList();

            if (blocks.Count == 0)
                return 0;

            node.Properties.SetUserString(
                CountProperty, blocks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < blocks.Count; i++)
            {
                string prefix = $"{Prefix}{i}_";

                node.Properties.SetUserString($"{prefix}type", blocks[i].Name);
                node.Properties.SetUserString($"{prefix}name", model.GetString(blocks[i], "Name"));

                NifFieldCodec.Write(
                    model, blocks[i], prefix,
                    (name, value) => node.Properties.SetUserString(name, value),
                    child => Skipped.Contains(child.Name));
            }

            return blocks.Count;
        }

        /// <summary>
        /// Rebuilds the extra data a node carried and hangs it back on the block.
        /// </summary>
        /// <remarks>
        /// Appended rather than assigned, so this can run after something else has
        /// already put a block on the list — the calculated <c>BSXFlags</c> is on the
        /// root before this is reached.
        /// </remarks>
        public static void ReadExtraData(
            FbxObject node, NifModel model, NifItem block, List<string> warnings)
        {
            string text = node.Properties.GetString(CountProperty);

            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int count)
                || count <= 0)
            {
                return;
            }

            var rebuilt = new List<NifItem>();

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{Prefix}{i}_";
                string type = node.Properties.GetString($"{prefix}type");

                if (type.Length == 0)
                    continue;

                if (!model.KnowsBlock(type) || !model.Database.Inherits(type, "NiExtraData"))
                {
                    warnings.Add(
                        $"{model.GetName(block)}: \"{type}\" is not extra data this build knows, it is dropped");

                    continue;
                }

                NifItem extra = model.InsertBlock(type);

                model.SetString(extra, "Name", node.Properties.GetString($"{prefix}name"));

                NifFieldCodec.Read(
                    model, extra, prefix,
                    name => node.Properties.GetString(name) is { Length: > 0 } value ? value : null,
                    child => Skipped.Contains(child.Name));

                rebuilt.Add(extra);
            }

            if (rebuilt.Count > 0)
                Append(model, block, rebuilt);
        }

        /// <summary>Adds blocks to the end of a block's extra data list.</summary>
        public static void Append(NifModel model, NifItem block, IReadOnlyList<NifItem> extra)
        {
            var existing = model.GetRefArray(block, "Extra Data List").ToList();

            NifItem? array = model.SetArraySize(
                block, "Num Extra Data List", "Extra Data List", existing.Count + extra.Count);

            if (array is null)
                return;

            for (int i = 0; i < existing.Count; i++)
                array.Children[i].Value.SetLink(model.IndexOf(existing[i]));

            for (int i = 0; i < extra.Count; i++)
                array.Children[existing.Count + i].Value.SetLink(model.IndexOf(extra[i]));
        }
    }
}
