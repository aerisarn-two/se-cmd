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
                        {
                            FbxInterpolatorCodec.Write(model, model.GetBlock(link), $"{name}_", Sink);
                            return;
                        }

                        // A pointer travels as the name of what it points at, the way the
                        // interpolator codec carries one. Following it instead would copy
                        // the node, which comes back attached to nothing.
                        //
                        // `NiBSBoneLODController` is why: it groups a skeleton's bones by
                        // the level of detail that still moves them, and every entry of
                        // every group is a pointer. Carried as nothing, all 55 of a wolf
                        // skeleton's came back null -- a bone LOD controller that names no
                        // bones, on the three creature skeletons that have one.
                        if (model.GetBlock(link) is { } aimed
                            && model.GetName(aimed) is { Length: > 0 } named)
                        {
                            Sink($"{name}{FbxInterpolatorCodec.PointerSuffix}", named);
                        }
                    });
            }
        }


        /// <summary>The prefix for a sequenced controller's own fields.</summary>
        public const string AnimatedFieldPrefix = "nac_";

        /// <summary>
        /// Records the class-specific fields of the controllers the animation route
        /// rebuilds.
        /// </summary>
        /// <remarks>
        /// <see cref="Write"/> deliberately skips a controller a sequence drives, since
        /// the animation layer builds that one. But the animation layer knows a
        /// controller by its keys, and a class that declares fields of its own beyond
        /// `NiTimeController` loses them: a `BSPSysMultiTargetEmitterCtlr` came back
        /// with `Max Emitters` zero against files holding anything from 2 to 99, on 83
        /// meshes of a 3,000-mesh sample.
        ///
        /// `NifAnimWriter` already expects this to be somebody's job -- it looks for a
        /// controller "rebuilt by a carrier that owns more of it than its keys" before
        /// making one -- but only the flipbook had such a carrier. This is that carrier
        /// for every other class.
        ///
        /// Which fields, asked of the schema rather than listed: everything the class
        /// declares that `NiTimeController` does not. Links are left out, since a link
        /// means nothing outside the file it was written in and the ones that matter are
        /// derived instead; so are the fields the animation layer owns, which is the
        /// whole of the base class -- flags, frequency, phase and the span.
        ///
        /// Keyed by class and by the same controller id `NifAnimWriter` uses to tell two
        /// controllers of one class apart, so a node with several is unambiguous.
        /// </remarks>
        public static void WriteAnimatedFields(
            FbxObject node, NifModel model, NifItem block, IReadOnlySet<NifItem>? sequenced = null)
        {
            for (NifItem? controller = model.GetRef(block, "Controller");
                 controller is not null;
                 controller = model.GetRef(controller, "Next Controller"))
            {
                // The ones Write already carries whole are not this carrier's business.
                if (IsStructural(model, controller) && sequenced?.Contains(controller) != true)
                    continue;

                foreach ((NifItem item, string key) in AnimatedFieldsOf(model, controller))
                    node.Properties.SetUserString(key, NifFieldCodec.Format(model, item));
            }
        }

        /// <summary>Puts those fields back, once the animation has built the chain.</summary>
        public static void ReadAnimatedFields(FbxObject node, NifModel model, NifItem block)
        {
            for (NifItem? controller = model.GetRef(block, "Controller");
                 controller is not null;
                 controller = model.GetRef(controller, "Next Controller"))
            {
                foreach ((NifItem item, string key) in AnimatedFieldsOf(model, controller))
                {
                    if (node.Properties.GetString(key) is { Length: > 0 } text)
                        NifFieldCodec.Assign(model, item, text);
                }
            }
        }

        /// <summary>A controller's own scalar fields, with the name each travels under.</summary>
        private static IEnumerable<(NifItem Item, string Key)> AnimatedFieldsOf(
            NifModel model, NifItem controller)
        {
            string id = NifAnimAccess.ControllerIdOf(model, controller);

            foreach (NifFieldDef field in FbxNodeType.OwnFields(model, controller.Name, "NiTimeController"))
            {
                if (model.FindItem(controller, field.Name) is not { Children.Count: 0 } item
                    || NifFieldCodec.IsLink(item))
                {
                    continue;
                }

                yield return (item, $"{AnimatedFieldPrefix}{controller.Name}_{id}_{field.Name}");
            }
        }


        /// <summary>The property recording the order of a block's controller chain.</summary>
        public const string ChainOrderProperty = "nac_order";

        /// <summary>Records the order the controllers sit in on the block.</summary>
        /// <remarks>
        /// A controller chain is a linked list, and the two routes that rebuild one --
        /// the structural carrier and the animation layer -- hang controllers on in
        /// whatever order they arrive, which is not the order the file had.
        ///
        /// A particle system's chain follows a rule and is derived rather than carried
        /// (`FbxToNif.OrderParticleControllerChains`). A shader's does not. Measured
        /// over all 22,047 meshes, of 2,660 chains whose every controller names a
        /// `Controlled Variable`, 2,157 have it descending, 146 ascending and 357
        /// neither -- `glowdust01` runs 7, 8, 6. That is an authored order, not a
        /// derivable one, so it travels.
        ///
        /// As a list of class and controller id, which is what tells two controllers of
        /// one class apart, and read back as a sort key. A controller the list does not
        /// name keeps its place at the end rather than being dropped.
        /// </remarks>
        public static void WriteChainOrder(FbxObject node, NifModel model, NifItem block)
        {
            var order = new List<string>();

            foreach (NifItem controller in Chain(model, block))
                order.Add($"{controller.Name}|{NifAnimAccess.ControllerIdOf(model, controller)}");

            if (order.Count > 1)
                node.Properties.SetUserString(ChainOrderProperty, string.Join("\u001f", order));
        }

        /// <summary>Puts the chain back in that order, once it has been rebuilt.</summary>
        public static void ReadChainOrder(FbxObject node, NifModel model, NifItem block)
        {
            if (node.Properties.GetString(ChainOrderProperty) is not { Length: > 0 } text)
                return;

            var wanted = new Dictionary<string, int>(StringComparer.Ordinal);
            string[] parts = text.Split('\u001f');

            for (int i = 0; i < parts.Length; i++)
                wanted.TryAdd(parts[i], i);

            var chain = Chain(model, block).ToList();

            if (chain.Count < 2)
                return;

            var ordered = chain
                .Select((controller, position) => (
                    Controller: controller,
                    Place: wanted.TryGetValue(
                        $"{controller.Name}|{NifAnimAccess.ControllerIdOf(model, controller)}",
                        out int found) ? found : int.MaxValue,
                    position))
                .OrderBy(x => x.Place)
                .ThenBy(x => x.position)
                .Select(x => x.Controller)
                .ToList();

            if (ordered.SequenceEqual(chain))
                return;

            model.SetRef(block, "Controller", ordered[0]);

            for (int i = 0; i < ordered.Count; i++)
                model.SetRef(ordered[i], "Next Controller", i + 1 < ordered.Count ? ordered[i + 1] : null);
        }

        /// <summary>A block's controllers, in the order they are linked.</summary>
        private static IEnumerable<NifItem> Chain(NifModel model, NifItem block)
        {
            var seen = new List<NifItem>();

            for (NifItem? controller = model.GetRef(block, "Controller");
                 controller is not null;
                 controller = model.GetRef(controller, "Next Controller"))
            {
                if (seen.Contains(controller))
                    yield break;

                seen.Add(controller);
                yield return controller;
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
                        {
                            // Resolved once every node exists, since a pointer may name
                            // one this walk has not reached yet.
                            if (aimsAt is not null
                                && Source($"{name}{FbxInterpolatorCodec.PointerSuffix}")
                                    is { Length: > 0 } named)
                            {
                                aimsAt(link, named);
                            }

                            return;
                        }

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
