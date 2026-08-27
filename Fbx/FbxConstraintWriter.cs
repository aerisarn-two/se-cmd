using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Writes Havok constraints into an FBX scene as tagged attachment points.
    /// </summary>
    /// <remarks>
    /// FBX has constraints of its own, but none of them mean what a Havok constraint
    /// means, so a constraint becomes what it can be honestly represented as: an
    /// empty node placed where the joint is, carrying the descriptor as properties
    /// (spec §4.10). Nothing reads it as a constraint; everything can see where the
    /// joint sits and what it was.
    ///
    /// The descriptor is written field by field, straight off the nif.xml
    /// definition, rather than through a case for each of the seven constraint
    /// types. Those cases would be seven near-identical lists of vectors and
    /// angles, and the ones a hand-written version leaves out are exactly the ones
    /// the corpus turns out to use — the two constraints in it are a stiff spring
    /// and a ball-and-socket chain, both of which FBXWrangler skips.
    /// </remarks>
    public static class FbxConstraintWriter
    {
        /// <summary>Marks the node as an attachment point, as FBXWrangler names it.</summary>
        public const string NameSuffix = "_attach_point";

        /// <summary>Where a chain's ordered list of bodies rides.</summary>
        /// <remarks>
        /// Node names, separated by the same character the attachment point's own name
        /// uses. A chain passes through more bodies than the two an attachment point can
        /// name, and nothing else in the scene records the order.
        /// </remarks>
        public const string ChainedProperty = "hkc_chained_bodies";

        /// <summary>Separates the two body names in an attachment point's name.</summary>
        public const string NameSeparator = "_con_";

        /// <summary>The property naming which kind of constraint this was.</summary>
        public const string TypeProperty = "constraint_type";

        /// <summary>The property naming the block wrapping the descriptor, if any.</summary>
        public const string WrapperProperty = "constraint_wrapper";

        /// <summary>Prefix on every property carrying a descriptor field.</summary>
        public const string FieldPrefix = "hkc_";

        /// <summary>
        /// The descriptor axes that make up a B frame, in the orders the types use.
        /// </summary>
        /// <remarks>
        /// Ragdoll names its axes after what they do; the hinges name theirs after
        /// the rotation they permit. Pivot-only constraints — ball and socket, stiff
        /// spring — have no frame at all and leave the node unrotated, which is
        /// exactly true of them: they constrain a point, not an orientation.
        /// </remarks>
        private static readonly string[][] FrameAxes =
        [
            ["Twist B", "Plane B", "Motor B"],
            ["Axis B", "Perp Axis In B1", "Perp Axis In B2"]
        ];

        /// <summary>
        /// Writes one constraint, given the body nodes it joins.
        /// </summary>
        /// <returns>The node created, or null when neither body was converted.</returns>
        public static FbxObject? AddConstraint(
            FbxScene scene, NifModel model, NifItem constraint,
            IReadOnlyDictionary<NifItem, (FbxObject Node, string Name)> bodies)
        {
            NifItem? wrapper = model.ConstraintWrapper(constraint);
            NifItem descriptor = model.ConstraintDescriptor(constraint);

            (NifItem? entityA, NifItem? entityB) = EntitiesOf(model, constraint);

            bodies.TryGetValue(entityA ?? constraint, out var a);
            bodies.TryGetValue(entityB ?? constraint, out var b);

            // Under the far body, since the frame written here is expressed in its
            // space. A constraint with only one entity -- Bethesda's breakable ones
            // often name just the one -- hangs off whichever it has.
            FbxObject? parent = b.Node ?? a.Node;

            if (parent is null)
                return null;

            // Named far body first, which is also the parent's own name, then the
            // owning body. The order looks redundant and is not: reading the node
            // back, the parent gives one entity and the name's second half the
            // other, so the second half is the only part carrying anything new
            // (constraint spec §1.1).
            string name = $"{b.Name}{NameSeparator}{a.Name}{NameSuffix}";

            FbxObject node = FbxMeshWriter.AddModel(scene, name, "Null", FrameOf(model, descriptor));
            scene.Connect(node, parent);

            node.Properties.SetUserString(TypeProperty, TypeNameOf(model, constraint, descriptor));

            // A wrapped constraint's type property names the descriptor inside it,
            // which is what HKXWrangler expects to read. The wrapper is a separate
            // block and would otherwise be lost, so it is recorded beside it.
            if (wrapper is not null)
                node.Properties.SetUserString(WrapperProperty, constraint.Name);

            WriteFields(model, descriptor, node, string.Empty);

            // The wrapper's own settings sit outside the descriptor: how much force
            // breaks it, and whether breaking removes it. The wrapper itself is
            // skipped, since its live arm is what was just written.
            if (wrapper is not null)
                WriteFields(model, constraint, node, string.Empty, skip: wrapper);

            // A chain's bodies, in order. Entity A and Entity B name only the first
            // pair; a rope of twenty-five is entirely in this list and nowhere else.
            if (model.GetRefArray(constraint, @"Constraint Chain Info\Chained Entities").ToList()
                is { Count: > 0 } chained)
            {
                node.Properties.SetUserString(
                    ChainedProperty,
                    string.Join(
                        NameSeparator,
                        chained.Select(body => bodies.TryGetValue(body, out var entry) ? entry.Name : string.Empty)));
            }

            return node;
        }

        /// <summary>The two rigid bodies a constraint joins.</summary>
        private static (NifItem? A, NifItem? B) EntitiesOf(NifModel model, NifItem constraint)
        {
            // On the constraint itself for the plain types, and on the chain's own
            // info block for the chained ones.
            foreach (string path in new[] { string.Empty, @"Constraint Chain Info\" })
            {
                NifItem? a = model.FindItem(constraint, $"{path}Entity A");
                NifItem? b = model.FindItem(constraint, $"{path}Entity B");

                if (a is not null || b is not null)
                    return (a is null ? null : model.GetBlock(a), b is null ? null : model.GetBlock(b));
            }

            return (null, null);
        }

        /// <summary>The name to record for the constraint's kind.</summary>
        private static string TypeNameOf(NifModel model, NifItem constraint, NifItem descriptor)
        {
            // A wrapped constraint's real type is the name of the live union arm;
            // an unwrapped one's is its own class, minus the Havok prefix.
            if (descriptor != constraint && descriptor.Name.Length > 0 && descriptor.Children.Count > 0)
                return descriptor.Name.Replace(" ", string.Empty);

            string name = constraint.Name;

            if (name.StartsWith("bhk", StringComparison.Ordinal))
                name = name[3..];

            return name.EndsWith("Constraint", StringComparison.Ordinal)
                ? name[..^"Constraint".Length]
                : name;
        }

        /// <summary>
        /// Where the joint sits, in the second body's space.
        /// </summary>
        /// <remarks>
        /// Written as the **transpose** of the frame — the axes end up as the
        /// matrix's columns, where a row-vector matrix like this one would put them
        /// in its rows. That is not a mistake and not a convention mismatch to be
        /// tidied away: it is what ck-cmd writes, and its importer inverts the
        /// rotation again on the way back in (constraint spec §1.2, §3.2). Writing
        /// the frame the upright way round would leave every attachment point
        /// inverted the moment ck-cmd read the file.
        ///
        /// The cost is that the node's visible orientation is the inverse of the
        /// joint's. se-cmd's own round trip does not go through it — the axes are in
        /// the properties too — so only a rigger looking at the node sees it.
        /// </remarks>
        private static NifTransform FrameOf(NifModel model, NifItem descriptor)
        {
            NifVector3 pivot = ScaledVector(model, descriptor, "Pivot B");
            NifMatrix33 rotation = NifMatrix33.Identity;

            foreach (string[] axes in FrameAxes)
            {
                if (!axes.All(a => model.FindItem(descriptor, a) is not null))
                    continue;

                NifVector3 x = Vector(model, descriptor, axes[0]);
                NifVector3 y = Vector(model, descriptor, axes[1]);
                NifVector3 z = Vector(model, descriptor, axes[2]);

                // Degenerate axes mean the file left the frame unset; identity is a
                // truer reading of that than a matrix that collapses space.
                if (Length(x) < 1e-6f || Length(y) < 1e-6f || Length(z) < 1e-6f)
                    break;

                rotation = new NifMatrix33
                {
                    M11 = x.X, M12 = y.X, M13 = z.X,
                    M21 = x.Y, M22 = y.Y, M23 = z.Y,
                    M31 = x.Z, M32 = y.Z, M33 = z.Z
                };

                break;
            }

            return new NifTransform(pivot, rotation, 1f);
        }

        /// <summary>
        /// Writes every live field of a descriptor as a string property.
        /// </summary>
        /// <remarks>
        /// Strings, following the spec, and because a Havok descriptor mixes vectors,
        /// angles, enums and flags: one representation that carries all of them
        /// without a schema beats four that each need one.
        ///
        /// Only live fields are written. A descriptor's definition lists the same
        /// field several times over for different Havok versions, and the conditions
        /// are what say which spelling this file uses.
        /// </remarks>
        private static void WriteFields(
            NifModel model, NifItem parent, FbxObject node, string prefix, NifItem? skip = null)
        {
            NifFieldCodec.Write(
                model, parent, prefix,
                (name, value) => node.Properties.SetUserString($"{FieldPrefix}{name}", value),
                child => child == skip || NifConstraintAccess.IsEntityField(child.Name));
        }

        private static NifVector3 Vector(NifModel model, NifItem parent, string field)
        {
            if (model.FindItem(parent, field) is not { } item)
                return new NifVector3();

            NifVector4 v = item.Value.Get<NifVector4>();
            return new NifVector3(v.X, v.Y, v.Z);
        }

        /// <summary>A pivot, in Skyrim units rather than Havok's metres.</summary>
        private static NifVector3 ScaledVector(NifModel model, NifItem parent, string field)
        {
            NifVector3 v = Vector(model, parent, field);

            return new NifVector3(
                v.X * ShapeTessellator.BhkScaleFactor,
                v.Y * ShapeTessellator.BhkScaleFactor,
                v.Z * ShapeTessellator.BhkScaleFactor);
        }

        private static float Length(NifVector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }
}
