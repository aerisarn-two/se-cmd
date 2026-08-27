using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries a collision shape's Havok material and layer through FBX.
    /// </summary>
    /// <remarks>
    /// A collision shape's geometry is tessellated on the way out and refitted on the
    /// way back, so everything the shape's size says survives the trip. Its material
    /// does not: nothing about the triangles records that this box is wood rather than
    /// stone, and the material is what the engine reads for footstep sound, impact
    /// decal and weapon-hit response.
    ///
    /// ck-cmd's answer, which this follows, is to make it an FBX material on the
    /// collision mesh, named after the Havok enum — <c>SKY_HAV_MAT_WOOD</c> — with the
    /// collision layer as a <c>CollisionLayer</c> property on the same material. That
    /// puts it somewhere a DCC tool shows and can edit, rather than in a number nobody
    /// can read, and it means a shape tree with several materials comes back with
    /// several materials.
    ///
    /// The names come from nif.xml's own <c>SkyrimHavokMaterial</c> and
    /// <c>SkyrimLayer</c> enums rather than from a table copied out of ck-cmd, which
    /// keeps the two spellings from drifting apart.
    /// </remarks>
    public static class FbxCollisionMaterial
    {
        /// <summary>The nif.xml enum the material names come from.</summary>
        public const string MaterialEnum = "SkyrimHavokMaterial";

        /// <summary>The nif.xml enum the layer names come from.</summary>
        public const string LayerEnum = "SkyrimLayer";

        /// <summary>The property on the material holding the collision layer.</summary>
        public const string LayerProperty = "CollisionLayer";

        /// <summary>The layer assumed when a material arrives without one.</summary>
        public const string DefaultLayer = "SKYL_STATIC";

        /// <summary>
        /// The shape's material field, which is version-dependent.
        /// </summary>
        /// <remarks>
        /// `HavokMaterial` declares three fields all called `Material`, one per game,
        /// separated only by their version condition — so the name alone finds the
        /// Oblivion one. The Skyrim field is the one typed `SkyrimHavokMaterial`.
        /// </remarks>
        public static NifItem? MaterialField(NifItem shape) => FieldOfType(shape, MaterialEnum);

        private static NifItem? FieldOfType(NifItem item, string type)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Type == type)
                    return child;

                if (child.Children.Count > 0 && !child.Value.IsLink && FieldOfType(child, type) is { } found)
                    return found;
            }

            return null;
        }

        /// <summary>The shape's material as an enum name, or empty if it has none.</summary>
        public static string NameOf(NifModel model, NifItem shape)
        {
            if (MaterialField(shape) is not { } field)
                return string.Empty;

            return model.Database.TryGetEnumOptionName(MaterialEnum, field.Value.ToUInt(), out string name)
                ? name
                : string.Empty;
        }

        /// <summary>The layer a body's filter names, or the static default.</summary>
        public static string LayerOf(NifModel model, NifItem? body)
        {
            if (body is null || FieldOfType(body, LayerEnum) is not { } field)
                return DefaultLayer;

            return model.Database.TryGetEnumOptionName(LayerEnum, field.Value.ToUInt(), out string name)
                ? name
                : DefaultLayer;
        }

        /// <summary>
        /// Puts a material name back onto a rebuilt shape.
        /// </summary>
        /// <returns>Whether the name was recognised and applied.</returns>
        public static bool Apply(NifModel model, NifItem shape, string name)
        {
            if (name.Length == 0
                || MaterialField(shape) is not { } field
                || !model.Database.TryGetEnumOptionValue(MaterialEnum, name, out uint value))
            {
                return false;
            }

            field.Value.SetCount(value);
            return true;
        }

        /// <summary>
        /// Puts a collision layer back onto a rebuilt body's filter.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="LayerOf"/>, which had none. The layer was read
        /// from the body, written into the scene twice — once on the collision material
        /// and once on the body node — read back on the way in, and then used only to
        /// decide whether the body was a static. It was never written to the body it
        /// came from, so every rebuilt body kept the field's default and a body that
        /// had been on, say, layer 10 came back on layer 1.
        ///
        /// That is not cosmetic: the layer decides what a thing collides with, and it
        /// is also the input to the motion profile, so getting it wrong changes the
        /// motion system, the deactivation and the quality along with it.
        /// </remarks>
        /// <returns>Whether the name was recognised and applied.</returns>
        public static bool ApplyLayer(NifModel model, NifItem body, string name)
        {
            if (name.Length == 0
                || FieldOfType(body, LayerEnum) is not { } field
                || !model.Database.TryGetEnumOptionValue(LayerEnum, name, out uint value))
            {
                return false;
            }

            field.Value.SetCount(value);
            return true;
        }
    }
}
