using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries the controllers on a block that animate nothing.
    /// </summary>
    /// <remarks>
    /// The animation layer recognises a controller by what its interpolator drives
    /// (§5A.4). A controller that holds no interpolator drives nothing that layer can
    /// see, so it is invisible to it — and there is nothing else in the file that would
    /// bring it back.
    ///
    /// These are not animation. <c>NiPSysUpdateCtlr</c> is the switch that makes a
    /// particle system run at all; <c>BSLagBoneController</c> makes a bone trail behind
    /// the one above it by a fixed amount, which is a property of the skeleton rather
    /// than of a timeline. Both say something about the thing they hang on, so they
    /// travel with it, as properties on its node.
    ///
    /// Controllers that *do* hold an interpolator are left alone: they are animation
    /// and go the other way, and carrying them here as well would rebuild them twice.
    /// </remarks>
    public static class FbxNodeControllers
    {
        /// <summary>The property counting the block's structural controllers.</summary>
        public const string CountProperty = "particle_controllers";

        /// <summary>Prefix on one structural controller's fields, before its index.</summary>
        public const string Prefix = "npc_";

        /// <summary>
        /// Whether a controller says something about the block rather than about a
        /// timeline.
        /// </summary>
        /// <remarks>
        /// Two things disqualify one. Holding an interpolator — in either slot, since
        /// an emitter's on/off track lives in the second — makes it animation, which
        /// travels by its own route; carrying it here as well would rebuild it twice.
        ///
        /// And the sequence machinery is not a controller on a node in the sense that
        /// matters. A <c>NiControllerManager</c> holds no interpolator of its own, but
        /// it *is* the animation layer, rebuilt from the sequences — carrying it here
        /// put a manager back into a file whose animation had been turned off.
        /// </remarks>
        /// And a controller a *sequence* names is rebuilt from that sequence, which is
        /// the <paramref name="sequenced"/> set the caller passes in. Holding no
        /// interpolator of its own is not enough to be structural:
        /// <c>BSProceduralLightningController</c> holds nine, none of them called
        /// `Interpolator`, and every one of them is driven from a sequence.
        private static bool IsStructural(NifModel model, NifItem controller) =>
            !Animated(model, controller, "Interpolator")
            && !Animated(model, controller, "Visibility Interpolator")
            && !model.BlockInherits(controller, "NiControllerManager")
            && !model.BlockInherits(controller, "NiMultiTargetTransformController");

        /// <summary>Whether a slot holds something the animation layer can carry.</summary>
        /// <remarks>
        /// Holding an interpolator is not enough. A <c>NiTransformController</c> whose
        /// interpolator is a <c>NiPathInterpolator</c> or a
        /// <c>NiLookAtInterpolator</c> drives nothing a curve on an FBX property can
        /// express, so the animation layer declines it — and it used to fall between
        /// the two routes, carried by neither.
        /// </remarks>
        private static bool Animated(NifModel model, NifItem controller, string field)
        {
            if (model.GetRef(controller, field) is not { } interpolator)
                return false;

            // A blend interpolator holds no keys: it is the slot a controller manager
            // mixes every playing sequence into, so it is the animation layer's own
            // mark that this controller is half of a sequenced pair. An LE file names
            // its controllers by type string rather than by reference, so the pair
            // cannot always be found from the sequence end.
            return model.BlockInherits(interpolator, "NiBlendInterpolator")
                   || NifAnimAccess.ReadsInterpolator(model, interpolator);
        }

        /// <summary>Fields rebuilt from the chain rather than carried.</summary>
        private static bool Rebuilt(NifItem child) => child.Name is "Next Controller" or "Target";

        /// <summary>Records the controllers on a block that hold no interpolator.</summary>
        /// <param name="sequenced">
        /// Controllers a sequence names, which the animation route rebuilds. Passing
        /// none carries every structural controller, which is right only for a file
        /// with no sequences.
        /// </param>
        public static void Write(
            FbxObject node, NifModel model, NifItem block, IReadOnlySet<NifItem>? sequenced = null)
        {
            var controllers = new List<NifItem>();

            for (NifItem? controller = model.GetRef(block, "Controller");
                 controller is not null;
                 controller = model.GetRef(controller, "Next Controller"))
            {
                if (IsStructural(model, controller) && sequenced?.Contains(controller) != true)
                    controllers.Add(controller);
            }

            if (controllers.Count == 0)
                return;

            node.Properties.SetUserString(
                CountProperty,
                controllers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            for (int i = 0; i < controllers.Count; i++)
            {
                string prefix = $"{Prefix}{i}_";

                node.Properties.SetUserString($"{prefix}type", controllers[i].Name);

                void Sink(string name, string value) => node.Properties.SetUserString(name, value);

                NifFieldCodec.Write(
                    model, controllers[i], prefix, Sink, Rebuilt,
                    (name, link) =>
                    {
                        // An interpolator this controller points at travels whole, its
                        // own fields and its data block's. A BSProceduralLightningController
                        // holds nine under names of its own, and with no sequence driving
                        // them nothing else would bring them back.
                        if (link.Value.Type != NifValueType.UpLink)
                            FbxInterpolatorCodec.Write(model, model.GetBlock(link), $"{name}_", Sink);
                    });
            }
        }

        /// <summary>Rebuilds the controllers that animate nothing, onto the block.</summary>
        public static void Read(
            FbxObject node,
            NifModel model,
            NifItem block,
            List<string> warnings,
            Action<NifItem, string>? aimsAt = null)
        {
            string text = node.Properties.GetString(CountProperty);

            if (!int.TryParse(text, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int count))
            {
                return;
            }

            for (int i = 0; i < count; i++)
            {
                string prefix = $"{Prefix}{i}_";
                string type = node.Properties.GetString($"{prefix}type");

                if (type.Length == 0)
                    continue;

                if (!model.KnowsBlock(type) || !model.Database.Inherits(type, "NiTimeController"))
                {
                    warnings.Add(
                        $"{model.GetName(block)}: \"{type}\" is not a controller this build knows, "
                        + "it is dropped");

                    continue;
                }

                NifItem controller = model.InsertBlock(type);

                string? Source(string name) =>
                    node.Properties.GetString(name) is { Length: > 0 } value ? value : null;

                NifFieldCodec.Read(
                    model, controller, prefix, Source, Rebuilt,
                    (name, link) =>
                    {
                        if (link.Value.Type == NifValueType.UpLink)
                            return;

                        if (FbxInterpolatorCodec.Read(
                                model, $"{name}_", Source, "NiInterpolator", 0, aimsAt)
                            is { } interpolator)
                        {
                            link.Value.SetLink(model.IndexOf(interpolator));
                        }
                    });

                model.SetRef(controller, "Target", block);

                Attach(model, block, controller);
            }
        }

        /// <summary>Adds a controller to the end of a block's chain.</summary>
        /// <remarks>
        /// The end rather than the front, so controllers keep the order they were read
        /// in: a chain is walked in order and two on one block can disagree.
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
    }
}
