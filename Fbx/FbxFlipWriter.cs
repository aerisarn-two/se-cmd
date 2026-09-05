using NIFSharp;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries texture flipbook controllers through FBX as properties.
    /// </summary>
    /// <remarks>
    /// A <c>NiFlipController</c> cycles a texture slot through a list of source
    /// textures, using a float interpolator to animate the index. FBX has layered
    /// textures and animated texture transforms, but nothing that swaps one image for
    /// another over time, so — as with a particle system — there is no conversion to
    /// make and only a choice about whether to keep it.
    ///
    /// The *animation* needs nothing from here: the interpolator drives a number, and
    /// a number is what the property tracks already carry (see
    /// <see cref="FbxAnimWriter"/>). What is carried here is the state a curve cannot
    /// hold — which slot is being flipped, and which images it flips between.
    ///
    /// This is a pre-Skyrim construct. Its <c>Delta</c> and <c>Accum Time</c> fields
    /// stop at 10.1.0.103, its usual host <c>NiTexturingProperty</c> is not something
    /// Skyrim uses, and no file in the test corpus has one.
    /// </remarks>
    public static class FbxFlipWriter
    {
        /// <summary>The property counting the flip controllers on a node.</summary>
        public const string CountProperty = "flip_controllers";

        /// <summary>Prefix on one controller's fields, before its index.</summary>
        public const string Prefix = "flip_";

        /// <summary>Fields the rebuild derives rather than reads.</summary>
        /// <remarks>
        /// The source count follows from the list actually carried, and the timing
        /// fields do not exist at Skyrim's version anyway.
        /// </remarks>
        private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
        {
            "Num Sources"
        };

        /// <summary>Whether a block is a flipbook controller.</summary>
        public static bool IsFlipController(NifModel model, NifItem block) =>
            model.BlockInherits(block, "NiFlipController");

        /// <summary>
        /// Writes every flipbook controller reachable from a shape onto its node.
        /// </summary>
        /// <returns>The number written.</returns>
        public static int AddFlipControllers(FbxObject node, NifModel model, NifItem shape)
        {
            var found = new List<NifItem>();

            // The controller hangs off a property, not off the shape, but the node is
            // what an importer has to put it back on.
            foreach (NifItem host in NifAnimAccess.ControllerHosts(model, shape))
            {
                for (NifItem? controller = model.GetRef(host, "Controller");
                     controller is not null;
                     controller = model.GetRef(controller, "Next Controller"))
                {
                    if (IsFlipController(model, controller))
                        found.Add(controller);
                }
            }

            if (found.Count == 0)
                return 0;

            node.Properties.SetUserString(
                CountProperty, found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < found.Count; i++)
                Write(node, model, found[i], $"{Prefix}{i}_");

            return found.Count;
        }

        private static void Write(FbxObject node, NifModel model, NifItem controller, string prefix)
        {
            node.Properties.SetUserString($"{prefix}type", controller.Name);

            NifFieldCodec.Write(
                model, controller, prefix,
                (name, value) => node.Properties.SetUserString(name, value),
                child => Skipped.Contains(child.Name));

            // The sources are the substance: a slot with no images to put in it is a
            // controller that flips between nothing. They are carried by file name,
            // which is the only part of a NiSourceTexture worth keeping — the rest is
            // load settings that the material already re-establishes.
            var sources = model.GetRefArray(controller, "Sources").ToList();

            node.Properties.SetUserString(
                $"{prefix}sources",
                sources.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < sources.Count; i++)
                node.Properties.SetUserString($"{prefix}source_{i}", model.GetString(sources[i], "File Name"));
        }
    }
}
