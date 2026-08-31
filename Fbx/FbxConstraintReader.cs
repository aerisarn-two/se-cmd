using SECmd.Conversion;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Finds the attachment points in a scene and reads what they say.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="FbxConstraintWriter"/>, and reads ck-cmd's own attachment
    /// points too — the naming and the <c>constraint_type</c> property are the same,
    /// and where ck-cmd writes only six limit values (constraint spec §1.3) those are
    /// kept separately so the writer can fall back on them.
    /// </remarks>
    public static class FbxConstraintReader
    {
        /// <summary>Every attachment point in the scene, in object order.</summary>
        public static List<ConstraintImport> ReadConstraints(this FbxScene scene)
        {
            var constraints = new List<ConstraintImport>();

            foreach (FbxObject node in scene.OfClass("Model"))
            {
                if (Read(scene, node) is { } constraint)
                    constraints.Add(constraint);
            }

            return constraints;
        }

        /// <summary>Whether a node is an attachment point.</summary>
        /// <remarks>
        /// A substring test on the separator, as ck-cmd does it, rather than a check
        /// for the suffix: its own <c>isConstraintFbxNode</c> never looks at
        /// <c>_attach_point</c>, so requiring it would reject scenes it produced.
        /// </remarks>
        public static bool IsAttachmentPoint(FbxObject node) =>
            node.Class == "Model"
            && node.Name.Contains(FbxConstraintWriter.NameSeparator, StringComparison.Ordinal);

        private static ConstraintImport? Read(FbxScene scene, FbxObject node)
        {
            if (!IsAttachmentPoint(node))
                return null;

            string name = NameEncoding.Unsanitize(node.Name);

            if (name.EndsWith(FbxConstraintWriter.NameSuffix, StringComparison.Ordinal))
                name = name[..^FbxConstraintWriter.NameSuffix.Length];

            int at = name.IndexOf(FbxConstraintWriter.NameSeparator, StringComparison.Ordinal);

            if (at < 0)
                return null;

            // The parent is entity B and would do as well as the name's first half,
            // but a scene that has been reorganised in a DCC tool may have moved the
            // node; the name is what both tools agree on.
            string other = name[..at];
            string owner = name[(at + FbxConstraintWriter.NameSeparator.Length)..];

            if (owner.Length == 0)
                return null;

            var constraint = new ConstraintImport
            {
                Type = node.Properties.GetString(FbxConstraintWriter.TypeProperty),
                Wrapper = node.Properties.GetString(FbxConstraintWriter.WrapperProperty),
                OwnerName = owner,
                // An empty far-body name means "the parent", unless the constraint says
                // it has no far body at all -- see FbxConstraintWriter.OneSidedProperty.
                // Falling back there pointed Entity B at the owner and joined a body to
                // itself.
                OtherName = other.Length > 0 ? other
                    : node.Properties.GetString(FbxConstraintWriter.OneSidedProperty).Length > 0
                        ? string.Empty
                        : ParentName(scene, node),
                ChainedNames = [.. node.Properties.GetString(FbxConstraintWriter.ChainedProperty)
                    .Split(FbxConstraintWriter.NameSeparator, StringSplitOptions.RemoveEmptyEntries)],
                FrameB = ReadTransform(node)
            };

            foreach (FbxProperty70 property in node.Properties.All)
            {
                string key = property.Name;

                if (key.StartsWith(FbxConstraintWriter.FieldPrefix, StringComparison.Ordinal))
                    constraint.Fields[key[FbxConstraintWriter.FieldPrefix.Length..]] = ValueOf(property);
                else if (property.IsUserDefined && key != FbxConstraintWriter.TypeProperty
                         && key != FbxConstraintWriter.WrapperProperty)
                {
                    constraint.Legacy[key] = ValueOf(property);
                }
            }

            return constraint;
        }

        private static string ParentName(FbxScene scene, FbxObject node) =>
            scene.ParentsOf(node.Id).FirstOrDefault() is { } parent
                ? NameEncoding.Unsanitize(parent.Name)
                : string.Empty;

        private static string ValueOf(FbxProperty70 property) =>
            property.Values.Count > 0 ? property.Values[0]?.ToString() ?? string.Empty : string.Empty;

        /// <summary>The node's own placement, which is the constraint's B frame.</summary>
        private static NifTransform ReadTransform(FbxObject node)
        {
            (double tx, double ty, double tz) = node.Properties.GetVector3("Lcl Translation");
            (double rx, double ry, double rz) = node.Properties.GetVector3("Lcl Rotation");

            return new NifTransform(
                new NifVector3((float)tx, (float)ty, (float)tz),
                NifTransform.RotationFromEulerDegrees((float)rx, (float)ry, (float)rz),
                1f);
        }
    }
}
