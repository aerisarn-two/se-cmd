using NIFSharp;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries what a collision object is, and how it keeps in step with its node.
    /// </summary>
    /// <remarks>
    /// <c>bhkCOFlags</c> says how the engine keeps a body and the node it hangs from in
    /// step: <c>SET_LOCAL</c> reads the body's transform as local to the node rather
    /// than as a world transform, <c>SYNC_ON_UPDATE</c> makes the collision follow the
    /// node when it is animated, <c>RESET_TRANS</c> puts it back afterwards. None of it
    /// is visible in the shape, and none of it can be worked out from the scene.
    ///
    /// Rebuilding it as a bare <c>ACTIVE</c> is what the importer did, and it is wrong
    /// in the direction that is hardest to notice: the collision is still there, still
    /// the right size, still in roughly the right place, and stops tracking the thing
    /// it belongs to.
    /// </remarks>
    public static class FbxCollisionObject
    {
        /// <summary>The property naming the collision object's class.</summary>
        /// <remarks>
        /// A <c>bhkBlendCollisionObject</c> is what makes a file a skeleton — the
        /// BSXFlags calculation defines <c>isSkeleton</c> as having one (see
        /// `bsxflags-spec.md` §3.1), so rebuilding it as a plain
        /// <c>bhkCollisionObject</c> does not merely lose a class, it changes what the
        /// engine thinks the file is. On a cow skeleton it takes the flags from 0xC6
        /// to 0x8A: no ragdoll, no dynamic bodies.
        ///
        /// ck-cmd builds the blend form only when exporting a rig, a mode this port
        /// does not have, so there is nothing to follow: the class is carried.
        /// </remarks>
        public const string TypeProperty = "nif_collision_type";

        /// <summary>The property naming the body's class.</summary>
        /// <remarks>
        /// The two go together. ck-cmd pairs a blend collision object with a plain
        /// <c>bhkRigidBody</c> and an ordinary one with <c>bhkRigidBodyT</c>, which
        /// applies its own transform — a skeleton's bodies are placed by their bones.
        /// </remarks>
        public const string BodyTypeProperty = "nif_body_type";

        /// <summary>Properties for the gains a blend object carries.</summary>
        public const string HeirGainProperty = "nif_blend_heir_gain";

        /// <inheritdoc cref="HeirGainProperty"/>
        public const string VelGainProperty = "nif_blend_vel_gain";

        /// <summary>The property the flag word travels in.</summary>
        public const string Property = "nif_collision_flags";

        /// <summary>What a collision object gets when nothing travelled with it.</summary>
        /// <remarks>Bit 0, <c>BHKCO_ACTIVE</c>, which is what a fresh body needs at minimum.</remarks>
        public const uint Default = 1;

        /// <summary>Records what the collision object is, and its flags.</summary>
        public static void Write(FbxObject bodyNode, NifModel model, NifItem collision, NifItem body)
        {
            bodyNode.Properties.SetUserString(TypeProperty, collision.Name);
            bodyNode.Properties.SetUserString(BodyTypeProperty, body.Name);

            if (model.FindItem(collision, "Flags") is { } flags)
            {
                bodyNode.Properties.SetUserString(
                    Property,
                    flags.Value.ToUInt().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach ((string field, string property) in
                     new[] { ("Heir Gain", HeirGainProperty), ("Vel Gain", VelGainProperty) })
            {
                if (model.FindItem(collision, field) is { } gain)
                {
                    bodyNode.Properties.SetUserString(
                        property, gain.Value.ToFloat().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>The collision object class to rebuild, or the fallback.</summary>
        public static string TypeOf(FbxObject bodyNode, NifModel model, string fallback) =>
            ClassOf(bodyNode, model, TypeProperty, "bhkNiCollisionObject", fallback);

        /// <summary>The body class to rebuild, or the fallback.</summary>
        public static string BodyTypeOf(FbxObject bodyNode, NifModel model, string fallback) =>
            ClassOf(bodyNode, model, BodyTypeProperty, "bhkWorldObject", fallback);

        private static string ClassOf(
            FbxObject bodyNode, NifModel model, string property, string ancestor, string fallback)
        {
            string name = bodyNode.Properties.GetString(property);

            return name.Length > 0 && model.KnowsBlock(name) && model.Database.Inherits(name, ancestor)
                ? name
                : fallback;
        }

        /// <summary>Puts the gains back on a rebuilt blend object.</summary>
        public static void ReadGains(FbxObject bodyNode, NifModel model, NifItem collision)
        {
            foreach ((string field, string property) in
                     new[] { ("Heir Gain", HeirGainProperty), ("Vel Gain", VelGainProperty) })
            {
                if (model.FindItem(collision, field) is { } gain
                    && float.TryParse(
                        bodyNode.Properties.GetString(property),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float value))
                {
                    gain.Value.SetFloat(value);
                }
            }
        }

        /// <summary>Puts them back on a rebuilt collision object.</summary>
        public static void Read(FbxObject bodyNode, NifModel model, NifItem collision)
        {
            string text = bodyNode.Properties.GetString(Property);

            uint value = uint.TryParse(
                text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
                ? parsed
                : Default;

            model.FindItem(collision, "Flags")?.Value.SetCount(value);
        }
    }
}
