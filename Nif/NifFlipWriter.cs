using NIFSharp;
using System.Globalization;
using SECmd.Fbx;

namespace SECmd.Nif
{
    /// <summary>
    /// Rebuilds texture flipbook controllers from the properties on a node.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="FbxFlipWriter"/>. The rebuilt controller is attached
    /// to the shape's shader property, which is where a controller of this kind lives:
    /// it changes what a property draws, not where a node is.
    /// </remarks>
    public static class NifFlipWriter
    {
        /// <summary>Whether a node carries any flipbook controller.</summary>
        public static bool HasFlipControllers(FbxObject node) =>
            Count(node, FbxFlipWriter.CountProperty) > 0;

        /// <summary>
        /// Builds every flipbook controller the node carries and attaches them.
        /// </summary>
        /// <param name="host">
        /// The block whose controller chain they join — the shader property when the
        /// shape has one, otherwise the shape.
        /// </param>
        public static void WriteFlipControllers(
            this NifModel model, FbxObject node, NifItem shape, NifItem host, List<string> warnings)
        {
            int count = Count(node, FbxFlipWriter.CountProperty);

            if (count <= 0)
                return;

            var fields = Fields(node);

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{FbxFlipWriter.Prefix}{i}_";
                string type = node.Properties.GetString($"{prefix}type");

                if (type.Length == 0 || !model.KnowsBlock(type))
                {
                    warnings.Add($"{model.GetName(shape)}: unknown flip controller \"{type}\", it is dropped");
                    continue;
                }

                NifItem controller = model.InsertBlock(type);

                NifFieldCodec.Read(
                    model, controller, prefix, name => fields.GetValueOrDefault(name));

                WriteSources(model, node, controller, prefix);
                model.SetRef(controller, "Target", host);

                Attach(model, host, controller);
            }
        }

        /// <summary>Rebuilds the images the controller flips between.</summary>
        /// <remarks>
        /// One <c>NiSourceTexture</c> per name, in order, since the index the
        /// interpolator animates is an index into this list.
        /// </remarks>
        private static void WriteSources(
            NifModel model, FbxObject node, NifItem controller, string prefix)
        {
            int count = Count(node, $"{prefix}sources");

            if (model.SetArraySize(controller, "Num Sources", "Sources", count) is not { } array)
                return;

            for (int i = 0; i < count && i < array.Children.Count; i++)
            {
                NifItem texture = model.InsertBlock("NiSourceTexture");

                model.SetString(texture, "File Name", node.Properties.GetString($"{prefix}source_{i}"));

                // Read from a file rather than embedded, which is what every Bethesda
                // texture is and what an empty pixel data field would otherwise mean.
                model.FindItem(texture, "Use External")?.Value.SetCount(1);

                array.Children[i].Value.SetLink(model.IndexOf(texture));
            }
        }

        /// <summary>Adds a controller to the end of a block's chain.</summary>
        /// <remarks>
        /// The end, not the front: a chain's order is the order the controllers were
        /// added in, and a shader property may already have one from the material.
        /// </remarks>
        private static void Attach(NifModel model, NifItem host, NifItem controller)
        {
            if (model.GetRef(host, "Controller") is not { } first)
            {
                model.SetRef(host, "Controller", controller);
                return;
            }

            NifItem last = first;

            while (model.GetRef(last, "Next Controller") is { } next)
                last = next;

            model.SetRef(last, "Next Controller", controller);
        }

        private static Dictionary<string, string> Fields(FbxObject node)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (FbxProperty70 property in node.Properties.All)
            {
                if (property.IsUserDefined && property.Values.Count > 0)
                    fields[property.Name] = property.Values[0]?.ToString() ?? string.Empty;
            }

            return fields;
        }

        private static int Count(FbxObject node, string property) =>
            int.TryParse(
                node.Properties.GetString(property),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}
