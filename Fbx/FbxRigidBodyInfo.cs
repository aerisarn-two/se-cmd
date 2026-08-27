using System.Globalization;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a rigid body's mass and collision layer through FBX.
    /// </summary>
    /// <remarks>
    /// These two are carried and the inertia tensor is not, because they are different
    /// kinds of fact. The mass is authored — ck-cmd's own example files give a box and
    /// a sphere of different sizes the same mass, which no density can produce — while
    /// the tensor follows from the mass and the shape, and is computed on import by
    /// <see cref="Conversion.HavokInertia"/>.
    ///
    /// The layer travels because it decides everything else: ck-cmd picks a body's
    /// motion system, quality and solver deactivation from it, and a static body is
    /// given a zero mass whatever it was carrying. A static with a mass is treated as
    /// movable, which is how a piece of scenery ends up falling through the world.
    /// </remarks>
    public static class FbxRigidBodyInfo
    {
        /// <summary>The property the mass travels in.</summary>
        public const string MassProperty = "nif_rb_mass";

        /// <summary>The property the collision layer travels in.</summary>
        public const string LayerProperty = "nif_rb_layer";

        /// <summary>
        /// One simulation scalar: what it is called, and what a body that arrives
        /// without it should get.
        /// </summary>
        /// <param name="Field">The nif.xml field name, under <c>Rigid Body Info</c>.</param>
        /// <param name="Static">The value for a body on a layer Havok never moves.</param>
        /// <param name="Moving">The value for a body that simulates.</param>
        public readonly record struct Scalar(string Field, float Static, float Moving)
        {
            /// <summary>The property this scalar travels in.</summary>
            public string Property => "nif_rb_" + Field.Replace(' ', '_').ToLowerInvariant();

            /// <summary>The default for a body of the given kind.</summary>
            public float Default(bool isStatic) => isStatic ? Static : Moving;
        }

        /// <summary>
        /// The simulation scalars carried verbatim, with the value to fall back on.
        /// </summary>
        /// <remarks>
        /// These are authored, not derived. Across the 14,408 rigid bodies Skyrim
        /// ships: penetration depth takes 2,185 distinct values, friction 16, angular
        /// damping 9, restitution 8, linear damping 5. ck-cmd reads every one of them
        /// straight back off the body when it builds a ragdoll
        /// (`Skeleton.cpp:1003-1028`), which is what they are for.
        ///
        /// Damping is on the list even though that same ragdoll path overrides it --
        /// angular to 0.049805, linear to 0, with `GetAngularDamping()` commented out
        /// beside it. That is a choice about ragdolls, not a statement that the field
        /// is meaningless, and the 51 bodies shipping a zero linear damping against
        /// 14,158 shipping 0.0996 say it is read.
        ///
        /// The fallbacks are Bethesda's own commonest values rather than nif.xml's
        /// defaults, so that a body authored in a DCC tool -- which carries none of
        /// these -- comes out looking like a body Bethesda exported. They agree for
        /// friction, restitution and the two ceilings, and differ for three:
        ///
        /// - Damping. Vanilla holds 0.099609375 and 0.0498046875 where nif.xml says
        ///   0.1 and 0.05. Those are 102/1024 and 51/1024: the exporter quantises
        ///   damping onto a 1/1024 grid, and 99.3% and 99.2% of static bodies sit
        ///   exactly there. nif.xml documents the round number nobody actually writes.
        /// - Penetration depth, which is the one that splits by kind: statics take 0.1
        ///   (62.7%) and movers 0.15 (38.4%, and nif.xml's default). With 2,185
        ///   distinct values it is plainly computed per body, so this is a starting
        ///   point rather than an answer -- but it is the right starting point for
        ///   each kind.
        ///
        /// Three siblings are deliberately absent: `Time Factor`, `Gravity Factor` and
        /// `Rolling Friction Multiplier` hold one value each across all 14,408 bodies
        /// (1, 1 and 0), so nif.xml's default is already the authored value and
        /// carrying them would be ceremony.
        /// </remarks>
        public static readonly Scalar[] Scalars =
        [
            new("Linear Damping", 0.099609375f, 0.099609375f),
            new("Angular Damping", 0.0498046875f, 0.0498046875f),
            new("Friction", 0.5f, 0.5f),
            new("Restitution", 0.4f, 0.4f),
            new("Penetration Depth", 0.1f, 0.15f),
            new("Max Linear Velocity", 104.4f, 104.4f),
            new("Max Angular Velocity", 31.57f, 31.57f)
        ];

        /// <summary>The layer assumed for a body that arrives without one.</summary>
        public const string DefaultLayer = "SKYL_STATIC";

        /// <summary>Records a body's mass and layer on the node standing for it.</summary>
        public static void Write(FbxObject bodyNode, NifModel model, NifItem body)
        {
            if (model.FindItem(body, @"Rigid Body Info\Mass") is { } mass)
            {
                bodyNode.Properties.SetUserString(
                    MassProperty, mass.Value.ToFloat().ToString("R", CultureInfo.InvariantCulture));
            }

            bodyNode.Properties.SetUserString(LayerProperty, FbxCollisionMaterial.LayerOf(model, body));

            foreach (Scalar scalar in Scalars)
            {
                if (model.FindItem(body, $@"Rigid Body Info\{scalar.Field}") is not { } item)
                    continue;

                bodyNode.Properties.SetUserString(
                    scalar.Property, item.Value.ToFloat().ToString("R", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// One carried scalar, or the fallback when the node carried none.
        /// </summary>
        public static float ScalarOf(FbxObject bodyNode, Scalar scalar, bool isStatic) =>
            float.TryParse(
                bodyNode.Properties.GetString(scalar.Property),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : scalar.Default(isStatic);

        /// <summary>The mass carried with a node, or null when none was.</summary>
        public static float? MassOf(FbxObject bodyNode)
        {
            string text = bodyNode.Properties.GetString(MassProperty);

            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float mass)
                ? mass
                : null;
        }

        /// <summary>The collision layer carried with a node, or the static default.</summary>
        public static string LayerOf(FbxObject bodyNode)
        {
            string layer = bodyNode.Properties.GetString(LayerProperty);

            return layer.Length > 0 ? layer : DefaultLayer;
        }

        /// <summary>
        /// Whether a layer is one Havok never moves.
        /// </summary>
        /// <remarks>
        /// ck-cmd's division: animated and biped bodies get box inertia, clutter gets
        /// full dynamics, and everything else is static and loses its mass.
        /// </remarks>
        public static bool IsStatic(string layer) =>
            layer is not ("SKYL_ANIMSTATIC" or "SKYL_BIPED" or "SKYL_CLUTTER");
    }
}
