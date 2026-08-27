using SECmd.Fbx;
using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Builds the blocks that hold a model's animation.
    /// </summary>
    /// <remarks>
    /// A NIF does not attach animation to the nodes it moves. It hangs a
    /// <c>NiControllerManager</c> off the root, holding one
    /// <c>NiControllerSequence</c> per animation, each listing the nodes it drives
    /// by name. Two more blocks make that indirection work: a
    /// <c>NiMultiTargetTransformController</c> naming every node any sequence
    /// touches, and a <c>NiDefaultAVObjectPalette</c> mapping those names back to
    /// blocks so the engine can resolve a sequence without walking the tree.
    ///
    /// Follows the spec's §5.6, which is FBXWrangler's shape for this.
    /// </remarks>
    public static class NifAnimWriter
    {
        /// <summary>Manager flags: active, and driven by the animation system.</summary>
        private const uint ManagerFlags = 12;

        /// <summary>Transform controller flags, as FBXWrangler writes them.</summary>
        private const uint TransformControllerFlags = 44;

        /// <summary>Play once and hold, which is what an exported take means.</summary>
        /// <summary>Rotation stored as three separate axis groups.</summary>
        private const uint XyzRotationKey = 4;

        /// <summary>
        /// The value Gamebryo reads as "no base transform", so the interpolator falls
        /// back to the node's own.
        /// </summary>
        /// <remarks>
        /// Not a number anyone computed: it is <c>-FLT_MAX</c>, whose bit pattern
        /// <c>0xFF7FFFFF</c> is what the files show. Writing a real transform here
        /// instead would override the node's rest pose on every channel the keys do
        /// not cover.
        /// </remarks>
        private const float UnsetTransform = float.MinValue;

        /// <summary>
        /// Writes every sequence, returning the manager, or null when there is
        /// nothing to write.
        /// </summary>
        /// <param name="nodes">The blocks a track can name, by name.</param>
        /// <param name="warnings">Collects tracks whose node does not exist.</param>
        public static NifItem? WriteAnimations(
            this NifModel model,
            NifItem root,
            IReadOnlyList<AnimSequence> sequences,
            IReadOnlyDictionary<string, NifItem> nodes,
            List<string> warnings)
        {
            // Resolve first: a sequence with no resolvable target is a sequence with
            // nothing to write, and the manager should not exist for it.
            var resolved = new List<(AnimSequence Sequence, List<(AnimTrack Track, NifItem Node)> Tracks)>();
            var targets = new List<NifItem>();

            foreach (AnimSequence sequence in sequences)
            {
                // The export invents this one to carry controllers that were attached
                // to their targets rather than named by any sequence (see
                // NifAnimAccess.ReadStandaloneControllers). Writing it back as a real
                // sequence would answer a question the source never asked: it would
                // put a controller manager, a sequence, an object palette and a text
                // key block into a file that had none of them, and leave the
                // controllers themselves unattached to what they control.
                if (sequence.Name == NifAnimAccess.DefaultSequenceName)
                {
                    WriteStandaloneControllers(model, sequence, nodes, warnings);
                    continue;
                }

                var tracks = new List<(AnimTrack, NifItem?)>();

                foreach (AnimTrack track in sequence.Tracks)
                {
                    // A controlled block names its node by string, so an entry whose
                    // node is not in this file is still a valid entry -- the game's
                    // own meshes have them, eleven sequences of a spriggan naming two
                    // leaf nodes that were never here. What it cannot have is a
                    // controller attached to that node, which is handled below.
                    nodes.TryGetValue(track.NodeName, out NifItem? node);

                    tracks.Add((track, node));

                    if (node is null)
                        continue;

                    // Only nodes whose transform moves belong in the target list: it
                    // is what the transform controller drives, and a node listed
                    // there without transform keys would be driven to nothing.
                    if (track.Curves.Any(c => c.HasKeys) && !targets.Contains(node))
                        targets.Add(node);
                }

                if (tracks.Count > 0)
                    resolved.Add((sequence, tracks));
            }

            if (resolved.Count == 0)
                return null;

            NifItem manager = model.InsertBlock("NiControllerManager");
            model.SetRef(manager, "Target", root);
            model.FindItem(manager, "Flags")?.Value.SetCount(ManagerFlags);
            model.FindItem(manager, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(manager, "Phase")?.Value.SetFloat(0f);

            NifItem controller = WriteMultiTargetController(model, root, targets);
            model.SetRef(manager, "Next Controller", controller);

            model.SetRef(manager, "Object Palette", WritePalette(model, root, targets));

            // The manager is reached through the root's controller chain, which is
            // the only thing that makes it part of the file rather than a loose block.
            model.SetRef(root, "Controller", manager);

            var built = new List<NifItem>();

            // Shared across sequences on purpose: a controller is attached to what it
            // drives once, and every sequence that animates it names that same block.
            var attached = new Dictionary<(NifItem Host, string Type, string Id), NifItem>();

            // Controllers with nowhere to hang, for entries naming a node that is not
            // in this file. Shared across sequences for the same reason.
            var unattached = new Dictionary<(string, string, string), NifItem>();

            foreach ((AnimSequence sequence, var tracks) in resolved)
                built.Add(WriteSequence(model, manager, sequence, tracks, attached, unattached));

            if (model.SetArraySize(manager, "Num Controller Sequences", "Controller Sequences", built.Count)
                is { } list)
            {
                for (int i = 0; i < built.Count && i < list.Children.Count; i++)
                    list.Children[i].Value.SetLink(model.IndexOf(built[i]));
            }

            return manager;
        }

        /// <summary>
        /// One controller naming every node any sequence moves.
        /// </summary>
        /// <remarks>
        /// The engine binds a sequence's tracks through this list rather than through
        /// each node's own controller chain, so a node missing from it stays still
        /// however many keys name it.
        /// </remarks>
        /// <summary>
        /// The controller a sequence's entry drives, attached to its host.
        /// </summary>
        /// <remarks>
        /// Attached controllers and sequences are two halves of one arrangement. The
        /// controller hangs on the thing it drives and holds a *blend* interpolator,
        /// which is the slot the manager writes into as it mixes; each sequence holds
        /// its own interpolator with the actual keys and names the controller it feeds.
        ///
        /// One controller serves every sequence, so this is created once per host,
        /// class and id, and found again after that.
        /// </remarks>
        private static NifItem? AttachedController(
            NifModel model,
            NifItem node,
            AnimProperty property,
            Dictionary<(NifItem Host, string Type, string Id), NifItem> attached)
        {
            if (property.ControllerType.Length == 0 || !model.KnowsBlock(property.ControllerType))
                return null;

            NifItem host = HostFor(model, node, property.ControllerType);
            var key = (host, property.ControllerType, property.ControllerId);

            if (!attached.TryGetValue(key, out NifItem? controller))
            {
                controller = model.InsertBlock(property.ControllerType);

                model.SetRef(controller, "Target", host);
                model.FindItem(controller, "Flags")?.Value.SetCount(StandaloneControllerFlags);
                model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
                model.FindItem(controller, "Phase")?.Value.SetFloat(0f);

                // Which of several same-typed controllers this is. nif.xml states
                // the field per class as GetCtlrID(): a particle modifier controller
                // finds its modifier by name, an extra-data controller its data.
                WriteControllerId(model, controller, property.ControllerId);

                Attach(model, host, controller);

                attached[key] = controller;
            }

            BlendInto(model, controller, property);

            return controller;
        }

        /// <summary>
        /// Gives a controller the blend slot this property mixes through.
        /// </summary>
        /// <remarks>
        /// A blend interpolator holds no keys. It is where the manager writes the mixed
        /// value of whatever is playing, so there is one per slot rather than one per
        /// sequence, and a controller that has been given one already keeps it.
        ///
        /// Some controllers drive two things at once. nif.xml spells out the case that
        /// matters here: <c>NiPSysEmitterCtlr</c>'s two interpolators are
        /// <c>['BirthRate', 'EmitterActive']</c>, the second on
        /// <c>Visibility Interpolator</c> — so its boolean track belongs in that slot
        /// of the same controller, not on a second controller of the same class.
        /// </remarks>
        private static void BlendInto(NifModel model, NifItem controller, AnimProperty property)
        {
            string field = SlotFor(model, controller, property);

            if (model.GetRef(controller, field) is not null)
                return;

            string blend = property switch
            {
                { IsColor: true } => "NiBlendPoint3Interpolator",
                { IsBoolean: true } => "NiBlendBoolInterpolator",
                _ => "NiBlendFloatInterpolator"
            };

            if (model.KnowsBlock(blend))
                model.SetRef(controller, field, model.InsertBlock(blend));
        }

        /// <summary>
        /// Which of a controller's interpolator slots a track belongs in.
        /// </summary>
        /// <remarks>
        /// The track says so itself when it came from a file that said so: nif.xml
        /// spells `NiPSysEmitterCtlr`'s two as <c>['BirthRate', 'EmitterActive']</c>,
        /// for `Interpolator` and `Visibility Interpolator` respectively, and both a
        /// sequence's `Interpolator ID` and the attached-controller reader carry that
        /// spelling across.
        ///
        /// Where it does not — a scene authored in a DCC tool, where nothing named the
        /// slot — a boolean track on a controller that has a visibility slot is what
        /// that slot is for.
        /// </remarks>
        private static string SlotFor(NifModel model, NifItem controller, AnimProperty property)
        {
            // A controller with slots of its own names them, and so does the track.
            // A BSProceduralLightningController holds nine, called
            // "Interpolator 2: Mutation" and so on, and a sequence's Interpolator ID
            // for one of them is "Mutation" -- nif.xml's own name for the slot, with
            // the spaces gone.
            if (property.InterpolatorId.Length > 0
                && NamedSlot(model, controller, property.InterpolatorId) is { } named)
            {
                return named;
            }

            if (model.FindItem(controller, "Visibility Interpolator") is null)
                return "Interpolator";

            if (property.InterpolatorId == EmitterActiveId)
                return "Visibility Interpolator";

            if (property.InterpolatorId.Length > 0)
                return "Interpolator";

            return property.IsBoolean ? "Visibility Interpolator" : "Interpolator";
        }

        /// <summary>nif.xml's spelling for the visibility half of an emitter controller.</summary>
        private const string EmitterActiveId = "EmitterActive";

        /// <summary>
        /// The interpolator field a track's id names, if the controller has one.
        /// </summary>
        /// <remarks>
        /// nif.xml spells a multi-slot controller's fields as
        /// <c>Interpolator &lt;n&gt;: &lt;what it drives&gt;</c>, and the id a sequence
        /// stores is the second half with its spaces removed. Matching them puts each
        /// of the nine tracks a lightning controller carries back in the slot it came
        /// from; without it they all went to `Interpolator`, which such a controller
        /// does not even have, and eight of the nine were lost.
        /// </remarks>
        private static string? NamedSlot(NifModel model, NifItem controller, string id)
        {
            foreach (NifItem child in controller.Children)
            {
                if (child.IsArray || child.Value.Type != NifValueType.Link)
                    continue;

                int colon = child.Name.IndexOf(':');
                string spelled = colon < 0 ? child.Name : child.Name[(colon + 1)..];

                if (Squeeze(spelled) == Squeeze(id))
                    return child.Name;
            }

            return null;

            static string Squeeze(string text) => text.Replace(" ", string.Empty);
        }

        /// <summary>
        /// Puts property animation back where it was: on the thing it controls.
        /// </summary>
        /// <remarks>
        /// A controller that no sequence names is attached directly to its host and
        /// runs on its own. FBX has no way to say that — every animation there belongs
        /// to a stack — so the export gathers them into an invented sequence and this
        /// undoes the invention.
        ///
        /// Which host depends on what the controller drives. A shader's fade or a
        /// texture's scroll is controlled from the shader property, an alpha threshold
        /// from the alpha property, and visibility from the node itself, so the
        /// controller's own class decides where it is hung.
        /// </remarks>
        private static void WriteStandaloneControllers(
            NifModel model,
            AnimSequence sequence,
            IReadOnlyDictionary<string, NifItem> nodes,
            List<string> warnings)
        {
            foreach (AnimTrack track in sequence.Tracks)
            {
                if (!nodes.TryGetValue(track.NodeName, out NifItem? node))
                {
                    warnings.Add(
                        $"{track.NodeName}: no node of that name, its property animation is dropped");

                    continue;
                }

                // A track with curves of its own moves the node, which is a transform
                // controller attached to it rather than anything a sequence names. A
                // track that only poses it is the same controller holding a
                // data-less interpolator.
                if (track.Curves.Any(c => c.HasKeys) || track.Pose is not null)
                    WriteTransformController(model, node, track);

                // One controller per class and id, not per track. A controller can
                // drive two values at once -- an emitter's birth rate and whether it
                // is emitting at all -- and building one per property left the second
                // as a duplicate controller of the same class, fighting the first.
                foreach (var group in track.Properties
                             .Where(Carries)
                             .Select((Property, Index) => (Property, Index))
                             .GroupBy(p => GroupKey(p.Property, p.Index))
                             .Select(g => g.Select(p => p.Property).ToList()))
                {
                    string type = group[0].ControllerType;
                    string id = group[0].ControllerId;

                    if (!model.KnowsBlock(type))
                    {
                        warnings.Add(
                            $"{track.NodeName}: {type} is not a block this build knows, "
                            + "its animation is dropped");

                        continue;
                    }

                    // A controller of this class may already be somewhere on this
                    // node, rebuilt by a carrier that owns more of it than its keys --
                    // a flipbook comes back complete with its texture list, needing
                    // only the interpolator that says which frame is showing. Adding a
                    // second would leave two fighting over one property, so the search
                    // covers every chain the node has rather than the one this would
                    // have picked.
                    (NifItem host, NifItem? found) = FindController(model, node, type, id);

                    bool existing = found is not null;
                    NifItem controller = found ?? model.InsertBlock(type);

                    model.SetRef(controller, "Target", host);

                    // Which of several same-typed controllers this is has to be set
                    // before the slots are filled: the search above reads it back.
                    WriteControllerId(model, controller, id);

                    foreach (AnimProperty property in group)
                    {
                        model.SetRef(
                            controller,
                            SlotFor(model, controller, property),
                            WriteValueInterpolator(model, property, 0f));
                    }

                    if (!existing)
                    {
                        model.FindItem(controller, "Flags")?.Value.SetCount(StandaloneControllerFlags);
                        model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
                        model.FindItem(controller, "Phase")?.Value.SetFloat(0f);
                    }

                    // The controller's own span is the span of the keys it holds; a
                    // bare controller has no sequence to take one from.
                    var times = group
                        .SelectMany(p => p.Curves)
                        .SelectMany(c => c.Keys)
                        .Select(k => k.Time)
                        .ToList();

                    model.FindItem(controller, "Start Time")?.Value.SetFloat(times.Count > 0 ? times.Min() : 0f);
                    model.FindItem(controller, "Stop Time")?.Value.SetFloat(times.Count > 0 ? times.Max() : 0f);

                    if (!existing)
                        Attach(model, host, controller);
                }
            }
        }

        /// <summary>
        /// Hangs a transform controller on a node, for a track that moves it.
        /// </summary>
        /// <remarks>
        /// The counterpart of the transform half of an invented sequence: keys that
        /// belong to the node itself rather than to a property of it. A controller of
        /// this kind that no sequence names is attached and runs on its own, so it is
        /// rebuilt the same way -- with its own interpolator holding the keys, since
        /// there is no manager here to blend into.
        /// </remarks>
        private static void WriteTransformController(NifModel model, NifItem node, AnimTrack track)
        {
            NifItem controller = model.InsertBlock("NiTransformController");

            model.SetRef(controller, "Interpolator", WriteInterpolator(model, track, 0f));
            model.SetRef(controller, "Target", node);

            model.FindItem(controller, "Flags")?.Value.SetCount(StandaloneControllerFlags);
            model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(controller, "Phase")?.Value.SetFloat(0f);

            // The controller's span is the span of the keys it holds; a bare
            // controller has no sequence to take one from.
            var times = track.Curves.SelectMany(c => c.Keys).Select(k => k.Time).ToList();

            model.FindItem(controller, "Start Time")?.Value.SetFloat(times.Count > 0 ? times.Min() : 0f);
            model.FindItem(controller, "Stop Time")?.Value.SetFloat(times.Count > 0 ? times.Max() : 0f);

            Attach(model, node, controller);
        }

        /// <summary>
        /// The controller a sequence entry names when its node is not in the file.
        /// </summary>
        /// <remarks>
        /// A controller hangs on the thing it drives, and there is nothing here to
        /// hang it on -- so it hangs on nothing, which is exactly what the game's own
        /// files do. `sprigganmatron` holds two `BSNiAlphaPropertyTestRefController`
        /// with no target, on no chain, reachable only because eleven sequence entries
        /// each name them.
        ///
        /// It still gets a blend interpolator: that is the slot the manager mixes into
        /// while the sequences play, and a controller without one is a controller the
        /// sequences write nowhere.
        ///
        /// One per class and id, as an attached one is, so eleven entries naming the
        /// same controller get the same controller.
        /// </remarks>
        private static NifItem? Unattached(
            NifModel model,
            string nodeName,
            AnimProperty property,
            Dictionary<(string, string, string), NifItem> unattached)
        {
            if (property.ControllerType.Length == 0 || !model.KnowsBlock(property.ControllerType))
                return null;

            // Keyed by the node too. An attached controller is told apart by the block
            // it hangs on; one that hangs on nothing has only the name of the node it
            // would have hung on, and the spriggan's two are the same class with no
            // ids -- distinguished solely by naming SprigganBodyLeaves01:0 and :1.
            var key = (nodeName, property.ControllerType, property.ControllerId);

            if (unattached.TryGetValue(key, out NifItem? controller))
                return controller;

            controller = model.InsertBlock(property.ControllerType);

            model.FindItem(controller, "Flags")?.Value.SetCount(StandaloneControllerFlags);
            model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(controller, "Phase")?.Value.SetFloat(0f);

            WriteControllerId(model, controller, property.ControllerId);
            BlendInto(model, controller, property);

            unattached[key] = controller;

            return controller;
        }

        /// <summary>
        /// Which controller a track's property belongs to.
        /// </summary>
        /// <remarks>
        /// Two properties share a controller only when they occupy *different slots*
        /// of it, which is what a non-empty interpolator id says.
        /// `NiPSysEmitterCtlr`'s `BirthRate` and `EmitterActive` name two slots of one
        /// controller and belong together.
        ///
        /// Sharing a class and an id is not enough, and this is the trap: a skull lock
        /// hangs two `NiFloatExtraDataController` on one node, both named
        /// `hkVis:Skull01`, both driving the one slot such a controller has. They
        /// cannot be the same controller — it would have to hold two interpolators in
        /// a slot that takes one — so grouping on the id alone rebuilt one where there
        /// were two.
        ///
        /// Where no slot is named, each property gets its own. A shader can carry
        /// several `BSEffectShaderPropertyFloatController`s — one fading, another
        /// scrolling — and nothing in a track distinguishes them, so grouping them by
        /// class alone would rebuild one where there were nine.
        /// </remarks>
        private static (string Type, string Id, int Ordinal) GroupKey(AnimProperty property, int index) =>
            property.InterpolatorId.Length > 0
                ? (property.ControllerType, property.ControllerId, -1)
                : (property.ControllerType, property.ControllerId, index);

        /// <summary>
        /// Whether a track's property says enough to rebuild a controller from.
        /// </summary>
        /// <remarks>
        /// A constant counts. It holds one value for the whole sequence rather than
        /// none — the next sequence can say something else — and treating it as
        /// nothing dropped the controller that held it, along with its other
        /// interpolator and that one's keys.
        /// </remarks>
        private static bool Carries(AnimProperty property) =>
            property.ControllerType.Length > 0
            && (property.Curves.Any(c => c.HasKeys)
                || property.Constant is not null
                || property.Empty
                || property.CarriedInterpolator is not null);

        /// <summary>
        /// Records which of several same-typed controllers on a target this one is.
        /// </summary>
        /// <remarks>
        /// nif.xml states the field per class, as what
        /// <c>NiInterpController::GetCtlrID()</c> returns: a particle modifier
        /// controller finds its modifier by name, an extra-data controller its data,
        /// and a shader property controller names the variable it drives — a number
        /// rather than a name, which is how the game writes it into a sequence.
        ///
        /// The mirror of <see cref="NifAnimAccess.ControllerIdOf"/>. Nothing wrote the
        /// controlled variable before this, so every shader controller was rebuilt
        /// driving variable 0 whatever it had driven.
        /// </remarks>
        private static void WriteControllerId(NifModel model, NifItem controller, string id)
        {
            if (id.Length == 0)
                return;

            if (NifAnimAccess.ControlledVariable(model, controller) is { } controlled)
            {
                // A sequence that named this controller some other way is left alone
                // rather than having a number invented for it: the field has a meaning
                // and a wrong one aims the animation at the wrong variable.
                if (uint.TryParse(
                        id,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out uint variable))
                {
                    controlled.Value.SetCount(variable);
                }

                return;
            }

            string field = model.FindItem(controller, "Modifier Name") is not null
                ? "Modifier Name"
                : "Extra Data Name";

            if (model.FindItem(controller, field) is not null)
                model.SetString(controller, field, id);
        }

        /// <summary>Active, and playing forwards on a loop, which is what a bare controller does.</summary>
        private const uint StandaloneControllerFlags = 0x000C;

        /// <summary>
        /// The block a controller of this class hangs from.
        /// </summary>
        /// <remarks>
        /// Falls back to the node, which is where a controller with no property of its
        /// own belongs and where an unrecognised one does least harm.
        /// </remarks>
        private static NifItem HostFor(NifModel model, NifItem node, string controllerType)
        {
            string? field = controllerType switch
            {
                _ when controllerType.Contains("ShaderProperty", StringComparison.Ordinal) => "Shader Property",
                _ when controllerType.Contains("AlphaProperty", StringComparison.Ordinal) => "Alpha Property",
                _ => null
            };

            return field is not null && model.GetRef(node, field) is { } property ? property : node;
        }

        /// <summary>
        /// A controller of this class already on the node or one of its properties.
        /// </summary>
        /// <returns>
        /// The host to hang it from, and the controller if one is already there.
        /// </returns>
        private static (NifItem Host, NifItem? Controller) FindController(
            NifModel model, NifItem node, string type, string id = "")
        {
            foreach (NifItem host in NifAnimAccess.ControllerHosts(model, node))
            {
                for (NifItem? controller = model.GetRef(host, "Controller");
                     controller is not null;
                     controller = model.GetRef(controller, "Next Controller"))
                {
                    if (controller.Name != type)
                        continue;

                    // A particle system carries several modifier controllers of the
                    // same class, one per modifier, and they are told apart by the id
                    // nif.xml gives the class. Matching on class alone gave them all
                    // the first controller's keys.
                    if (id.Length > 0 && IdOf(model, controller) != id)
                        continue;

                    // Only one still waiting for its keys. A carrier that rebuilt a
                    // controller without an interpolator left it for this; one that
                    // already has an interpolator is a different controller that
                    // happens to share a class, and a shader can easily carry several
                    // -- one scrolling U, another scrolling V.
                    if (model.GetRef(controller, "Interpolator") is null)
                        return (host, controller);
                }
            }

            return (HostFor(model, node, type), null);
        }

        /// <summary>The id a controller records, in whichever field its class uses.</summary>
        /// <remarks>
        /// The reader's own answer, not a second implementation of it. This was a
        /// separate copy that knew about `Modifier Name` and `Extra Data Name` only,
        /// and when the reader learned that a shader controller is identified by the
        /// variable it drives, this did not: the ids stopped matching, every candidate
        /// was skipped, and a controller already on the node was rebuilt beside itself.
        /// Two functions that have to agree about the same question should be one.
        /// </remarks>
        private static string IdOf(NifModel model, NifItem controller) =>
            NifAnimAccess.ControllerIdOf(model, controller);

        /// <summary>Adds a controller to the end of a host's chain.</summary>
        /// <remarks>
        /// The end rather than the front, so controllers keep the order they were read
        /// in: a chain is walked in order and two controllers on one property can
        /// disagree about what they set.
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

        private static NifItem WriteMultiTargetController(
            NifModel model, NifItem root, List<NifItem> targets)
        {
            NifItem controller = model.InsertBlock("NiMultiTargetTransformController");

            model.SetRef(controller, "Target", root);
            model.FindItem(controller, "Flags")?.Value.SetCount(TransformControllerFlags);
            model.FindItem(controller, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(controller, "Phase")?.Value.SetFloat(0f);
            model.FindItem(controller, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(controller, "Stop Time")?.Value.SetFloat(0f);

            if (model.SetArraySize(controller, "Num Extra Targets", "Extra Targets", targets.Count)
                is { } extra)
            {
                for (int i = 0; i < targets.Count && i < extra.Children.Count; i++)
                    extra.Children[i].Value.SetLink(model.IndexOf(targets[i]));
            }

            return controller;
        }

        /// <summary>The name-to-block table the engine resolves sequences through.</summary>
        private static NifItem WritePalette(NifModel model, NifItem root, List<NifItem> targets)
        {
            NifItem palette = model.InsertBlock("NiDefaultAVObjectPalette");
            model.SetRef(palette, "Scene", root);

            // The root is in the palette too, because a sequence may name it as its
            // accumulation root.
            var all = new List<NifItem> { root };
            all.AddRange(targets.Where(t => t != root));

            if (model.SetArraySize(palette, "Num Objs", "Objs", all.Count) is not { } objects)
                return palette;

            for (int i = 0; i < all.Count && i < objects.Children.Count; i++)
            {
                NifItem entry = objects.Children[i];

                // A SizedString, not a table index: the palette is meant to be
                // readable without the header.
                model.FindItem(entry, "Name")?.Value.Set(model.GetName(all[i]));
                model.FindItem(entry, "AV Object")?.Value.SetLink(model.IndexOf(all[i]));
            }

            return palette;
        }

        private static NifItem WriteSequence(
            NifModel model, NifItem manager, AnimSequence sequence,
            List<(AnimTrack Track, NifItem? Node)> tracks,
            Dictionary<(NifItem Host, string Type, string Id), NifItem> attached,
            Dictionary<(string, string, string), NifItem> unattached)
        {
            NifItem block = model.InsertBlock("NiControllerSequence");

            model.SetString(block, "Name", sequence.Name);
            model.SetRef(block, "Manager", manager);
            // The node the sequence accumulates against. Synthesised from whichever
            // block happened to be first when the sequence did not carry one, which is
            // still the fallback -- but a sequence that named one keeps it.
            model.SetString(
                block,
                "Accum Root Name",
                sequence.AccumRootName.Length > 0 ? sequence.AccumRootName : model.GetName(model.Blocks[0]));

            // Sequences play from zero; where they sat on the source timeline is not
            // something the engine has any use for.
            float length = MathF.Max(sequence.Stop - sequence.Start, 0f);

            model.FindItem(block, "Start Time")?.Value.SetFloat(0f);
            model.FindItem(block, "Stop Time")?.Value.SetFloat(length);
            model.FindItem(block, "Frequency")?.Value.SetFloat(1f);
            model.FindItem(block, "Weight")?.Value.SetFloat(1f);
            // What the sequence does at its end, as the source said. This was a constant
            // named CycleClamp holding zero -- and nif.xml's zero is CYCLE_LOOP, clamp
            // being 2 -- so every sequence in every file this wrote looped, including
            // the ones meant to play once and stop.
            model.FindItem(block, "Cycle Type")?.Value.SetCount(sequence.CycleType);

            model.SetRef(block, "Text Keys", WriteTextKeys(model, length));

            // A node's transform and each of its properties are separate blocks
            // here, though they arrived as one track.
            var entries = new List<(AnimTrack Track, NifItem? Node, AnimProperty? Property)>();

            foreach ((AnimTrack track, NifItem? node) in tracks)
            {
                // Keys, or a pose held for the whole sequence -- both are the node's
                // own transform rather than a property of it.
                if (track.Curves.Any(c => c.HasKeys) || track.Pose is not null)
                    entries.Add((track, node, null));

                foreach (AnimProperty property in track.Properties.Where(
                             p => p.Curves.Any(c => c.HasKeys)
                                  || p.Constant is not null
                                  || p.Empty
                                  || p.CarriedInterpolator is not null))
                {
                    entries.Add((track, node, property));
                }
            }

            if (model.SetArraySize(block, "Num Controlled Blocks", "Controlled Blocks", entries.Count)
                is not { } controlled)
            {
                return block;
            }

            for (int i = 0; i < entries.Count && i < controlled.Children.Count; i++)
            {
                (AnimTrack track, NifItem? node, AnimProperty? property) = entries[i];
                NifItem entry = controlled.Children[i];

                // The track's name rather than the node's: they agree when the node is
                // here, and when it is not the track's is all there is.
                model.SetString(entry, "Node Name", node is null ? track.NodeName : model.GetName(node));

                if (property is null)
                {
                    model.SetRef(entry, "Interpolator", WriteInterpolator(model, track, sequence.Start));
                    model.SetString(entry, "Controller Type", "NiTransformController");
                    continue;
                }

                model.SetRef(entry, "Interpolator",
                    WriteValueInterpolator(model, property, sequence.Start));

                // The four strings that say which controller on which sub-object
                // this drives. Without them the keys exist but belong to nothing.
                model.SetString(entry, "Controller Type", property.ControllerType);
                model.SetString(entry, "Controller ID", property.ControllerId);
                model.SetString(entry, "Interpolator ID", property.InterpolatorId);
                model.SetString(entry, "Property Type", property.PropertyType);

                // And the block itself. A sequence names a controller that is also
                // attached to what it drives, and the attached copy holds a blend
                // interpolator rather than keys -- that is the slot the manager mixes
                // every playing sequence into. Without it the sequences describe an
                // animation with nothing to apply it.
                // A carried interpolator is put back into the entry that named it and
                // nothing more. What owned it in the source is the sequence machinery
                // -- a fish walking a spline is driven from the multi-target
                // controller, not from one attached to its node -- and inventing an
                // attached controller here gave every one a transform controller and a
                // blend interpolator the file never had.
                if (node is not null
                    && property.CarriedInterpolator is null
                    && AttachedController(model, node, property, attached) is { } controller)
                {
                    model.SetRef(entry, "Controller", controller);
                }
                else if (node is null
                         && Unattached(model, track.NodeName, property, unattached) is { } loose)
                {
                    model.SetRef(entry, "Controller", loose);
                }
            }

            return block;
        }

        /// <summary>Data blocks built so far, per model being written.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            NifModel, Dictionary<int, NifItem>> SharedData = [];

        /// <summary>An interpolator of this track's kind, pointed at data already built.</summary>
        private static NifItem Interpolator(NifModel model, AnimProperty property, NifItem data)
        {
            NifItem interpolator = model.InsertBlock(InterpolatorClass(model, property));

            model.SetRef(interpolator, "Data", data);

            return interpolator;
        }

        /// <summary>Writes a named track as a float, boolean or point interpolator.</summary>
        /// <remarks>
        /// All three are the same shape — an interpolator pointing at a data block
        /// holding one key group — and differ only in what a key holds. A boolean
        /// track written as floats would leave the engine reading four bytes per key
        /// where it expects one, and every key after the first would be wrong.
        ///
        /// A colour's three curves are merged back onto shared times the way a
        /// translation's are, since a NIF key holds the whole point.
        /// </remarks>
        private static NifItem WriteValueInterpolator(NifModel model, AnimProperty property, float offset)
        {
            // A constant holds one value for the whole sequence and has no data block
            // at all -- that absence is the representation, not a missing piece of
            // one, so writing a one-key block instead would be a different animation
            // that happens to look the same.
            // An interpolator this layer never modelled, put back as it was. Nothing
            // about a spline path or a look-at is a curve, so there is nothing to
            // rebuild it from except the fields themselves.
            if (property.CarriedInterpolator is { } carried
                && FbxInterpolatorCodec.Rebuild(model, carried) is { } whole)
            {
                return whole;
            }

            // An interpolator that holds nothing: no data block, and its Value left at
            // the default nif.xml gives it, which is the sentinel meaning "none".
            // The block exists because the file had one, and says as little.
            if (property.Empty)
                return model.InsertBlock(InterpolatorClass(model, property));

            if (property.Constant is { } constant)
                return WriteConstantInterpolator(model, property, constant);

            string dataType = property switch
            {
                { IsColor: true } => "NiPosData",
                { IsBoolean: true } => "NiBoolData",
                _ => "NiFloatData"
            };

            // Two interpolators can share one data block, and the game's files do it.
            // Rebuilding each one's keys separately turns one block into two, which is
            // the file changed -- keyed on which block it was rather than on the keys
            // in it, since identical data side by side is also something the game
            // ships (§5.2.1).
            var shared = SharedData.GetOrCreateValue(model);

            if (property.DataId >= 0 && shared.TryGetValue(property.DataId, out NifItem? already))
                return Interpolator(model, property, already);

            NifItem data = model.InsertBlock(dataType);

            if (property.DataId >= 0)
                shared[property.DataId] = data;

            var times = property.IsColor
                ? MergedTimes(property.Curves)
                : property.Curve.Keys.Select(k => k.Time).ToList();

            NifItem keys = SizeGroup(model, data, "Data", times.Count, KeyTypeOf(property.Curves));

            for (int i = 0; i < times.Count && i < keys.Children.Count; i++)
            {
                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(times[i] - offset);

                NifItem? value = model.FindItem(keys.Children[i], "Value");

                if (property.IsColor)
                {
                    value?.Value.Set(new NifVector3(
                        Sample(property.Curves[0], times[i]),
                        Sample(property.Curves[1], times[i]),
                        Sample(property.Curves[2], times[i])));
                }
                else if (property.IsBoolean)
                {
                    value?.Value.SetCount(property.Curve.Keys[i].Value != 0f ? 1u : 0u);
                }
                else
                {
                    value?.Value.SetFloat(property.Curve.Keys[i].Value);
                }
            }

            NifItem interpolator = model.InsertBlock(InterpolatorClass(model, property));

            model.SetRef(interpolator, "Data", data);
            return interpolator;
        }

        /// <summary>
        /// The interpolator class a track wants.
        /// </summary>
        /// <remarks>
        /// The carried one when it is there and fits what the track drives, since the
        /// obvious class is not always the right one: a NiBoolTimelineInterpolator is
        /// a NiBoolInterpolator that cannot skip a key between updates, and rebuilding
        /// it as its base loses that quietly.
        /// </remarks>
        private static string InterpolatorClass(NifModel model, AnimProperty property)
        {
            string fallback = property switch
            {
                { IsColor: true } => "NiPoint3Interpolator",
                { IsBoolean: true } => "NiBoolInterpolator",
                _ => "NiFloatInterpolator"
            };

            return property.InterpolatorType.Length > 0
                   && model.KnowsBlock(property.InterpolatorType)
                   && model.Database.Inherits(property.InterpolatorType, fallback)
                ? property.InterpolatorType
                : fallback;
        }

        /// <summary>An interpolator holding one value and no keys.</summary>
        private static NifItem WriteConstantInterpolator(
            NifModel model, AnimProperty property, float constant)
        {
            NifItem interpolator = model.InsertBlock(InterpolatorClass(model, property));

            if (model.FindItem(interpolator, "Value") is { } value)
            {
                if (property.IsColor)
                    value.Value.Set(new NifVector3(constant, constant, constant));
                else if (property.IsBoolean)
                    value.Value.SetCount(constant != 0f ? 1u : 0u);
                else
                    value.Value.SetFloat(constant);
            }

            return interpolator;
        }

        /// <summary>
        /// The start and end markers every sequence needs.
        /// </summary>
        /// <remarks>
        /// Skyrim looks these up by name to know where a sequence begins and ends;
        /// a sequence without them is loaded but never plays.
        /// </remarks>
        private static NifItem WriteTextKeys(NifModel model, float length)
        {
            NifItem keys = model.InsertBlock("NiTextKeyExtraData");

            if (model.SetArraySize(keys, "Num Text Keys", "Text Keys", 2) is not { } list
                || list.Children.Count < 2)
            {
                return keys;
            }

            model.FindItem(list.Children[0], "Time")?.Value.SetFloat(0f);
            model.SetString(list.Children[0], "Value", "start");

            model.FindItem(list.Children[1], "Time")?.Value.SetFloat(length);
            model.SetString(list.Children[1], "Value", "end");

            return keys;
        }

        private static NifItem WriteInterpolator(NifModel model, AnimTrack track, float offset)
        {
            NifItem interpolator = model.InsertBlock("NiTransformInterpolator");

            // A track with no *transform* keys holds a pose instead: the transform the
            // node takes for the whole sequence, in the interpolator's own Transform,
            // with no data block at all. That absence is the representation.
            //
            // The node's own curves, not the track's every curve. A track carries the
            // node's properties too -- a visibility controller, a shader fade -- and
            // asking whether any of those had keys said "this transform is animated"
            // about a transform that is not, and wrote an empty data block for it.
            if (!track.Curves.Any(c => c.HasKeys) && track.Pose is { } pose)
            {
                WriteTransform(model, interpolator, pose.Translation, pose.Rotation, pose.Scale);
                return interpolator;
            }

            NifItem data = model.InsertBlock("NiTransformData");

            WriteTranslations(model, data, track, offset);
            WriteRotations(model, data, track, offset);
            WriteScales(model, data, track, offset);

            model.SetRef(interpolator, "Data", data);

            // The base transform is left unset so the node's own is used for whatever
            // the keys do not drive.
            WriteTransform(
                model, interpolator,
                new NifVector3(UnsetTransform, UnsetTransform, UnsetTransform),
                new NifQuat(UnsetTransform, UnsetTransform, UnsetTransform, UnsetTransform),
                UnsetTransform);

            return interpolator;
        }

        /// <summary>Fills an interpolator's own transform.</summary>
        private static void WriteTransform(
            NifModel model, NifItem interpolator, NifVector3 translation, NifQuat rotation, float scale)
        {
            model.FindItem(interpolator, @"Transform\Translation")?.Value.Set(translation);
            model.FindItem(interpolator, @"Transform\Rotation")?.Value.Set(rotation);
            model.FindItem(interpolator, @"Transform\Scale")?.Value.SetFloat(scale);
        }

        private static void WriteTranslations(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            // FBX keys each axis independently; a NIF translation key is one vector,
            // so the axes have to be sampled onto one shared set of times.
            var times = MergedTimes(track.Translation);

            if (times.Count == 0)
                return;

            NifItem keys = SizeGroup(model, data, "Translations", times.Count,
                KeyTypeOf(track.Translation));

            for (int i = 0; i < times.Count && i < keys.Children.Count; i++)
            {
                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(times[i] - offset);

                model.FindItem(keys.Children[i], "Value")?.Value.Set(new NifVector3(
                    Sample(track.Translation[0], times[i]),
                    Sample(track.Translation[1], times[i]),
                    Sample(track.Translation[2], times[i])));
            }
        }

        /// <summary>
        /// Rotation keys, written as three separate axis groups.
        /// </summary>
        /// <remarks>
        /// The XYZ form is used rather than quaternions because it is the one that
        /// survives the trip: FBX keys Euler axes independently and at different
        /// times, and packing those into quaternions would force every axis onto a
        /// shared timeline and lose any winding past a half turn.
        /// </remarks>
        private static void WriteRotations(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            const float ToRadians = MathF.PI / 180f;

            if (!track.Rotation.Any(c => c.HasKeys))
                return;

            // The count field must say one for the XYZ form; the real counts live in
            // the groups themselves.
            model.FindItem(data, "Num Rotation Keys")?.Value.SetCount(1);
            data.InvalidateConditionsRecursive();

            model.FindItem(data, "Rotation Type")?.Value.SetCount(XyzRotationKey);
            data.InvalidateConditionsRecursive();

            if (model.FindItem(data, "XYZ Rotations") is not { } groups)
                return;

            model.UpdateArraySize(groups);

            for (int axis = 0; axis < 3 && axis < groups.Children.Count; axis++)
            {
                AnimCurve curve = track.Rotation[axis];
                NifItem group = groups.Children[axis];

                NifItem keys = SizeGroup(model, group, string.Empty, curve.Keys.Count,
                    KeyTypeOf([curve]));

                for (int i = 0; i < curve.Keys.Count && i < keys.Children.Count; i++)
                {
                    model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(curve.Keys[i].Time - offset);
                    model.FindItem(keys.Children[i], "Value")?.Value.SetFloat(curve.Keys[i].Value * ToRadians);
                }
            }
        }

        private static void WriteScales(NifModel model, NifItem data, AnimTrack track, float offset)
        {
            var times = MergedTimes(track.Scale);

            if (times.Count == 0)
                return;

            NifItem keys = SizeGroup(model, data, "Scales", times.Count, KeyTypeOf(track.Scale));

            for (int i = 0; i < times.Count && i < keys.Children.Count; i++)
            {
                model.FindItem(keys.Children[i], "Time")?.Value.SetFloat(times[i] - offset);

                // NIF scales uniformly. X is the axis a NIF-sourced file keyed all
                // three of, and the only sensible pick when they disagree.
                model.FindItem(keys.Children[i], "Value")?.Value.SetFloat(Sample(track.Scale[0], times[i]));
            }
        }

        /// <summary>
        /// Sizes a <c>KeyGroup</c> and states its interpolation.
        /// </summary>
        /// <remarks>
        /// The order is forced: the interpolation field does not exist until the
        /// count says there are keys, and the keys' own layout depends on the
        /// interpolation, since quadratic keys carry tangents and the others do not.
        /// </remarks>
        private static NifItem SizeGroup(
            NifModel model, NifItem parent, string field, int count, uint keyType)
        {
            string prefix = field.Length > 0 ? $@"{field}\" : string.Empty;

            model.FindItem(parent, $"{prefix}Num Keys")?.Value.SetCount((uint)count);
            parent.InvalidateConditionsRecursive();

            model.FindItem(parent, $"{prefix}Interpolation")?.Value.SetCount(keyType);
            parent.InvalidateConditionsRecursive();

            NifItem keys = model.FindItem(parent, $"{prefix}Keys")!;
            model.UpdateArraySize(keys);
            return keys;
        }

        /// <summary>
        /// The NIF key type for a channel, taking the smoothest its axes ask for.
        /// </summary>
        /// <remarks>
        /// A group has one interpolation for all its keys, so axes that disagree have
        /// to be reconciled. Taking the smoothest keeps a curve that was authored
        /// smooth from becoming a set of straight lines; the reverse would be visible.
        /// </remarks>
        private static uint KeyTypeOf(IReadOnlyList<AnimCurve> curves)
        {
            const uint Linear = 1, Quadratic = 2, Const = 5;

            uint best = Const;

            foreach (AnimKey key in curves.SelectMany(c => c.Keys))
            {
                uint type = key.Interpolation switch
                {
                    AnimInterpolation.Constant => Const,
                    AnimInterpolation.Linear => Linear,
                    _ => Quadratic
                };

                // Const is the coarsest and quadratic the smoothest, but the enum
                // does not order them that way, so rank explicitly.
                if (Rank(type) > Rank(best))
                    best = type;
            }

            return best;

            static int Rank(uint type) => type switch { Const => 0, Linear => 1, _ => 2 };
        }

        /// <summary>Every time any axis of a channel is keyed at, in order.</summary>
        private static List<float> MergedTimes(IReadOnlyList<AnimCurve> curves)
        {
            var times = new SortedSet<float>();

            foreach (AnimCurve curve in curves)
            {
                foreach (AnimKey key in curve.Keys)
                    times.Add(key.Time);
            }

            return [.. times];
        }

        /// <summary>
        /// A curve's value at a time, interpolating between the keys around it.
        /// </summary>
        /// <remarks>
        /// Needed because merging the axes onto shared times asks each axis for
        /// values at times it was not keyed at. Linear is the honest reading:
        /// inventing a smooth fit through points that were never authored would
        /// overshoot between them.
        /// </remarks>
        private static float Sample(AnimCurve curve, float time)
        {
            if (curve.Keys.Count == 0)
                return 0f;

            if (time <= curve.Keys[0].Time)
                return curve.Keys[0].Value;

            for (int i = 1; i < curve.Keys.Count; i++)
            {
                if (time > curve.Keys[i].Time)
                    continue;

                AnimKey before = curve.Keys[i - 1];
                AnimKey after = curve.Keys[i];

                if (before.Interpolation == AnimInterpolation.Constant)
                    return before.Value;

                float span = after.Time - before.Time;

                return span <= 0f
                    ? after.Value
                    : before.Value + (after.Value - before.Value) * ((time - before.Time) / span);
            }

            return curve.Keys[^1].Value;
        }
    }
}
