using NIFSharp;
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

        /// <summary>The nif.xml bitfield holding the rest of a collision filter.</summary>
        public const string FilterFlagsType = "CollisionFilterFlags";

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

        /// <summary>
        /// The shape's material as an enum name, or as its number when it has no name.
        /// </summary>
        /// <remarks>
        /// A Skyrim Havok material is a hashed string, and nif.xml lists the ones the
        /// game ships. A file may hold one it does not list: a mod defines its own
        /// materials in its plugin, and the value in the NIF is all that reaches a
        /// converter -- the name lives in an ESP this has no business reading.
        ///
        /// Dropping it to empty, as this did, means the shape is rebuilt on whatever
        /// the default is: a modded floor of a custom material came back as stone, with
        /// the footstep sound and impact response of stone. So an unrecognised value
        /// travels as its own decimal number and comes back unchanged. Only the naming
        /// is a convenience -- the number is the fact.
        /// </remarks>
        public static string NameOf(NifModel model, NifItem shape)
        {
            if (MaterialField(shape) is not { } field)
                return string.Empty;

            uint value = field.Value.ToUInt();

            return model.Database.TryGetEnumOptionName(MaterialEnum, value, out string name)
                ? name
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
        /// Puts a material back onto a rebuilt shape, by name or by number.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="NameOf"/>: a material nif.xml does not name
        /// arrives as its number, and is written as the number it is. ck-cmd names the
        /// ones it knows in the same way and this follows it, with the difference that
        /// a value it cannot name is carried rather than dropped -- there is no reason
        /// to lose a material for want of a word for it.
        /// </remarks>
        /// <returns>Whether the material was recognised and applied.</returns>
        public static bool Apply(NifModel model, NifItem shape, string name)
        {
            if (name.Length == 0 || MaterialField(shape) is not { } field)
                return false;

            if (!model.Database.TryGetEnumOptionValue(MaterialEnum, name, out uint value)
                && !uint.TryParse(
                        name, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out value))
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
            if (name.Length == 0 || !model.Database.TryGetEnumOptionValue(LayerEnum, name, out uint value))
                return false;

            // Every layer field on the body, not the first one found. A rigid body
            // carries two HavokFilters -- bhkWorldObject's own, and the copy inside
            // Rigid Body Info -- and writing only the first left the copy on the
            // default. They agree in all 14,408 bodies Skyrim ships, so one value
            // rightly fills both; it is having only one of them filled that is wrong.
            var fields = new List<NifItem>();
            CollectFieldsOfType(body, LayerEnum, fields);

            int written = 0;

            foreach (NifItem field in fields)
            {
                // Only the fields this file's version actually has. nif.xml spells the
                // rigid body info three times over for three Havok generations, and the
                // two it is not using are present in the tree and absent from the file.
                if (!model.EvalCondition(field))
                    continue;

                field.Value.SetCount(value);
                written++;
            }

            return written > 0;
        }

        /// <summary>
        /// The rest of a body's collision filter: the byte beside the layer.
        /// </summary>
        /// <remarks>
        /// A `HavokFilter` is a layer, a `CollisionFilterFlags` byte and a group, and
        /// only the layer was carried. The byte is not spare room: it holds
        /// `No Collision`, which stops the body colliding at all, `Linked Group`,
        /// `MOPP Scaled`, and the biped part.
        ///
        /// Little of it is in use and none of it is derivable. Of 3,071 filters in a
        /// 2,500-mesh sample 2,965 are zero, and the 106 that are not fall in two
        /// groups: 84 with `Linked Group` alone, on debris, clutter and animated
        /// scenery, and 13 naming a biped part, every one of them on `SKYL_BIPED` as
        /// nif.xml says.
        ///
        /// Neither follows from the file. `Linked Group` is set on 9 bodies that are
        /// the only body in their file and clear on 557 that share theirs with others,
        /// so a body count does not predict it, and constraints do not either. The
        /// biped part looks as though it should follow from a `BSDismemberSkinInstance`
        /// partition, which names body parts too -- but all 26 bodies carrying one are
        /// in files with no dismember partitions whatever: `skeleton_female.nif` and
        /// `dlc1skullhawkgo.nif` among them. The partitions are on the mesh, the filter
        /// is on the skeleton's ragdoll bodies, and Skyrim keeps those in separate
        /// files, so the file holding one cannot see the other.
        /// </remarks>
        public static uint FilterFlagsOf(NifModel model, NifItem? body) =>
            body is not null && FieldOfType(body, FilterFlagsType) is { } field
                ? field.Value.ToUInt()
                : 0u;

        /// <summary>
        /// Puts that byte back, on every filter this version of the block has.
        /// </summary>
        /// <remarks>
        /// Both of them, as <see cref="ApplyLayer"/> writes both layers: a rigid body
        /// carries its own filter and another inside `Rigid Body Info`, and filling one
        /// leaves the other at its default.
        /// </remarks>
        public static bool ApplyFilterFlags(NifModel model, NifItem body, uint value)
        {
            var fields = new List<NifItem>();
            CollectFieldsOfType(body, FilterFlagsType, fields);

            int written = 0;

            foreach (NifItem field in fields)
            {
                if (!model.EvalCondition(field))
                    continue;

                field.Value.SetCount(value);
                written++;
            }

            return written > 0;
        }

        /// <summary>Every field of the given type on a block, depth first.</summary>
        private static void CollectFieldsOfType(NifItem item, string type, List<NifItem> into)
        {
            foreach (NifItem child in item.Children)
            {
                if (child.Type == type)
                    into.Add(child);
                else if (child.Children.Count > 0 && !child.Value.IsLink)
                    CollectFieldsOfType(child, type, into);
            }
        }
    }
}
