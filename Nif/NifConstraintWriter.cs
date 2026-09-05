using NIFSharp;
using System.Globalization;
using SECmd.Conversion;

namespace SECmd.Nif
{
    /// <summary>
    /// Rebuilds Havok constraints from the attachment points in a scene.
    /// </summary>
    /// <remarks>
    /// ck-cmd does this by way of Havok: HKXWrangler turns the attachment points into
    /// <c>hkpConstraintInstance</c>s and FBXWrangler converts those back into blocks.
    /// Going through Havok costs it four of the nine constraint types, since only
    /// ragdolls, hinges and limited hinges have a form on both sides — and a plain
    /// hinge is demoted to a limited one on the way (constraint spec §3.6). This goes
    /// straight from the scene to the block instead.
    ///
    /// Two sources of truth, in order. A scene se-cmd exported carries the whole
    /// descriptor as <c>hkc_</c> properties, so the block is filled straight from
    /// them. A scene ck-cmd exported carries six limit values and the joint's
    /// placement, so the descriptor is derived from those instead — which is all
    /// ck-cmd's own importer ever had.
    /// </remarks>
    public static class NifConstraintWriter
    {
        /// <summary>
        /// The block each <c>constraint_type</c> stands for.
        /// </summary>
        /// <remarks>
        /// HKXWrangler recognises only <c>Ragdoll</c> and turns everything else into a
        /// limited hinge (spec §3.3). That is not done here: it would turn the stiff
        /// spring in the test corpus into a hinge nobody authored, which is a worse
        /// answer than declining to import it.
        /// </remarks>
        /// <remarks>
        /// Matched without regard to case. The name comes from nif.xml's own union arm
        /// with the spaces taken out, and nif.xml spells `Ball and Socket` with a small
        /// `and` -- so the key it produces is `BallandSocket`, which an ordinal lookup
        /// misses. `LimitedHinge` and `StiffSpring` only ever worked because nif.xml
        /// happens to capitalise their second word.
        /// </remarks>
        private static readonly Dictionary<string, string> BlockTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Ragdoll"] = "bhkRagdollConstraint",
            ["Hinge"] = "bhkHingeConstraint",
            ["LimitedHinge"] = "bhkLimitedHingeConstraint",
            ["BallAndSocket"] = "bhkBallAndSocketConstraint",
            ["StiffSpring"] = "bhkStiffSpringConstraint",
            ["Prismatic"] = "bhkPrismaticConstraint",
            ["Malleable"] = "bhkMalleableConstraint",
            ["BallSocketConstraintChain"] = "bhkBallSocketConstraintChain"
        };

        /// <summary>
        /// The limits ck-cmd writes, and the descriptor fields they correspond to.
        /// </summary>
        private static readonly Dictionary<string, string> LegacyFields = new(StringComparer.Ordinal)
        {
            ["coneMaxAngle"] = "Cone Max Angle",
            ["planeMinAngle"] = "Plane Min Angle",
            ["planeMaxAngle"] = "Plane Max Angle",
            ["twistMinAngle"] = "Twist Min Angle",
            ["twistMaxAngle"] = "Twist Max Angle",
            ["maxFriction"] = "Max Friction",
            ["minAngle"] = "Min Angle",
            ["maxAngle"] = "Max Angle"
        };

        /// <summary>
        /// Writes every constraint whose bodies exist.
        /// </summary>
        /// <param name="bodies">The rigid bodies, by the name of the node they came from.</param>
        public static void WriteConstraints(
            this NifModel model,
            IReadOnlyList<ConstraintImport> constraints,
            IReadOnlyDictionary<string, NifItem> bodies,
            List<string> warnings)
        {
            foreach (ConstraintImport constraint in constraints)
            {
                if (!bodies.TryGetValue(constraint.OwnerName, out NifItem? owner))
                {
                    warnings.Add(
                        $"constraint on \"{constraint.OwnerName}\": no such collision body, it is dropped");

                    continue;
                }

                bodies.TryGetValue(constraint.OtherName, out NifItem? other);

                if (Build(model, constraint, warnings) is not { } block)
                    continue;

                LinkEntities(model, block, owner, other);
                LinkChain(model, block, constraint.ChainedNames, bodies);
                Attach(model, owner, block);
            }
        }

        private static NifItem? Build(NifModel model, ConstraintImport constraint, List<string> warnings)
        {
            string type = constraint.Wrapper.Length > 0 ? constraint.Wrapper : BlockTypeOf(constraint.Type);

            if (type.Length == 0)
            {
                warnings.Add(
                    $"constraint on \"{constraint.OwnerName}\": unknown type \"{constraint.Type}\", it is dropped");

                return null;
            }

            NifItem block = model.InsertBlock(type);

            // A wrapper picks its descriptor by a type number, so that has to be set
            // before the union arm it selects exists to be written into.
            if (constraint.Wrapper.Length > 0)
                SelectWrappedType(model, block, constraint.Type);

            NifItem descriptor = model.ConstraintDescriptor(block);

            if (constraint.HasFields)
            {
                ReadFields(model, descriptor, constraint.Fields, string.Empty);

                // The wrapper's own settings live outside the descriptor, and the
                // union is skipped because its live arm was just filled in.
                if (descriptor != block)
                    ReadFields(model, block, constraint.Fields, string.Empty, skip: model.ConstraintWrapper(block));
            }
            else
            {
                WriteFromFrame(model, descriptor, constraint);
            }

            return block;
        }

        private static string BlockTypeOf(string type) => BlockTypes.GetValueOrDefault(type, string.Empty);

        /// <summary>hkpConstraintData::ConstraintType, by the name the arm carries.</summary>
        private static readonly Dictionary<string, uint> ConstraintTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["BallAndSocket"] = 0,
                ["Hinge"] = 1,
                ["LimitedHinge"] = 2,
                ["Prismatic"] = 6,
                ["Ragdoll"] = 7,
                ["StiffSpring"] = 8,
                ["Malleable"] = 13,
            };

        /// <summary>Points a wrapper's union at the arm the descriptor needs.</summary>
        private static void SelectWrappedType(NifModel model, NifItem block, string type)
        {
            // The numbers are hkpConstraintData::ConstraintType, which nif.xml
            // conditions each arm on.
            //
            // Matched without regard to case, for the reason `BlockTypes` is: the name
            // is nif.xml's arm with its spaces removed, and `Ball and Socket` becomes
            // `BallandSocket`. Missing it fell through to the default and wrote a
            // ragdoll where the file said ball and socket -- on `breakableboard01` and
            // `trapmace01`, whose breakable constraints are the only wrapped ball and
            // socket joints the game ships.
            if (!ConstraintTypes.TryGetValue(type, out uint value))
                value = 7;

            if (model.ConstraintWrapper(block) is not { } wrapper)
                return;

            model.FindItem(wrapper, "Type")?.Value.SetCount(value);
            block.InvalidateConditionsRecursive();
        }

        /// <summary>
        /// Fills a descriptor from the properties, walking it exactly as the writer
        /// did so the names line up by construction.
        /// </summary>
        private static void ReadFields(
            NifModel model, NifItem parent, IReadOnlyDictionary<string, string> fields,
            string prefix, NifItem? skip = null)
        {
            NifFieldCodec.Read(
                model, parent, prefix,
                name => fields.GetValueOrDefault(name),
                child => child == skip || NifConstraintAccess.IsEntityField(child.Name));
        }

        /// <summary>
        /// Derives a descriptor from the joint's placement and ck-cmd's six limits.
        /// </summary>
        /// <remarks>
        /// All that a scene from ck-cmd carries. The B frame is the node's own
        /// transform with the pivot back in Havok's metres; the A frame is left at
        /// zero rather than being recomputed from the hierarchy, since the scene
        /// gives no better answer than the one ck-cmd's own recomputation produces —
        /// and that one copies the pivot's X into all three components (spec §3.2).
        /// </remarks>
        private static void WriteFromFrame(NifModel model, NifItem descriptor, ConstraintImport constraint)
        {
            NifVector3 pivot = constraint.FrameB.Translation;

            SetVector(model, descriptor, "Pivot B", new NifVector3(
                pivot.X / ShapeTessellator.BhkScaleFactor,
                pivot.Y / ShapeTessellator.BhkScaleFactor,
                pivot.Z / ShapeTessellator.BhkScaleFactor));

            NifMatrix33 r = constraint.FrameB.Rotation;

            // Read out of the columns, because the node carries the transpose of the
            // frame -- this is where HKXWrangler's own rotation inverse sits (spec
            // §1.2, §3.2). Reading the rows instead gives every axis of every joint
            // the wrong way round, which nothing about the file would show.
            foreach (string[] axes in new[]
                     {
                         new[] { "Twist B", "Plane B", "Motor B" },
                         ["Axis B", "Perp Axis In B1", "Perp Axis In B2"]
                     })
            {
                if (model.FindItem(descriptor, axes[0]) is null)
                    continue;

                SetVector(model, descriptor, axes[0], new NifVector3(r.M11, r.M21, r.M31));
                SetVector(model, descriptor, axes[1], new NifVector3(r.M12, r.M22, r.M32));
                SetVector(model, descriptor, axes[2], new NifVector3(r.M13, r.M23, r.M33));
                break;
            }

            foreach ((string property, string field) in LegacyFields)
            {
                if (constraint.Legacy.TryGetValue(property, out string? text)
                    && model.FindItem(descriptor, field) is { } item)
                {
                    item.Value.SetFloat(ParseFloat(text));
                }
            }
        }

        private static void SetVector(NifModel model, NifItem parent, string field, NifVector3 value)
        {
            if (model.FindItem(parent, field) is { } item)
                item.Value.Set(new NifVector4(value.X, value.Y, value.Z, 0f));
        }

        /// <summary>Points the constraint at the two bodies it joins.</summary>
        private static void LinkEntities(NifModel model, NifItem block, NifItem owner, NifItem? other)
        {
            // A chain keeps its entities on its own info block rather than inline.
            string prefix = model.FindItem(block, "Entity A") is not null
                ? string.Empty
                : @"Constraint Chain Info\";

            model.FindItem(block, $"{prefix}Entity A")?.Value.SetLink(model.IndexOf(owner));

            model.FindItem(block, $"{prefix}Entity B")?.Value
                .SetLink(other is null ? -1 : model.IndexOf(other));

            model.FindItem(block, $"{prefix}Num Entities")?.Value.SetCount(2);
        }

        /// <summary>Relinks the bodies a constraint chain passes through, in order.</summary>
        /// <remarks>
        /// Entity A and Entity B name only the first pair. A chain of twenty-five bodies
        /// keeps the rest here and nowhere else, so unlike the entity fields -- which the
        /// scene hierarchy can reconstruct -- this list has to travel, and does, by node
        /// name.
        /// </remarks>
        private static void LinkChain(
            NifModel model, NifItem block, IReadOnlyList<string> names,
            IReadOnlyDictionary<string, NifItem> bodies)
        {
            if (names.Count == 0
                || model.FindItem(block, @"Constraint Chain Info\Chained Entities") is null)
            {
                return;
            }

            var chained = new List<NifItem>(names.Count);

            foreach (string name in names)
            {
                if (bodies.TryGetValue(name, out NifItem? body))
                    chained.Add(body);
            }

            if (model.SetArraySize(
                    block,
                    @"Constraint Chain Info\Num Entities A",
                    @"Constraint Chain Info\Chained Entities",
                    chained.Count) is not { } list)
            {
                return;
            }

            for (int i = 0; i < chained.Count && i < list.Children.Count; i++)
                list.Children[i].Value.SetLink(model.IndexOf(chained[i]));
        }

        /// <summary>Adds the constraint to the body that owns it.</summary>
        private static void Attach(NifModel model, NifItem owner, NifItem constraint)
        {
            if (model.FindItem(owner, "Constraints") is not { } existing)
                return;

            int count = existing.Children.Count;

            if (model.SetArraySize(owner, "Num Constraints", "Constraints", count + 1) is { } list
                && count < list.Children.Count)
            {
                list.Children[count].Value.SetLink(model.IndexOf(constraint));
            }
        }

        private static float ParseFloat(string text) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
    }
}
