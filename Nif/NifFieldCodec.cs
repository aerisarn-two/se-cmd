using NIFSharp;
using System.Globalization;

namespace SECmd.Nif
{
    /// <summary>
    /// Reads and writes a block's fields as flat name/value text.
    /// </summary>
    /// <remarks>
    /// FBX has nowhere to put a Havok constraint or a particle system, so both are
    /// carried as string properties on a node instead. What they have in common is
    /// the shape of the problem: a subtree of fields nif.xml already describes, of
    /// which only some are live, and which has to come back the same way it went.
    ///
    /// So the walk is written once. Both directions descend in declaration order,
    /// evaluating conditions as they go, which is what makes a name on the way out
    /// find the same field on the way in — the same reason reading a NIF has to be
    /// ordered rather than indexed.
    /// </remarks>
    public static class NifFieldCodec
    {
        /// <summary>The name a field is stored under.</summary>
        public static string Key(string prefix, string fieldName) =>
            $"{prefix}{fieldName.Replace(' ', '_').ToLowerInvariant()}";

        /// <summary>
        /// Whether a field is a link to another block.
        /// </summary>
        /// <remarks>
        /// Block indices mean nothing once exported, so a link is never carried as a
        /// value. What it pointed at is the caller's problem: expressed by the scene's
        /// own structure, carried by name, or not carried at all.
        /// </remarks>
        public static bool IsLink(NifItem item) =>
            item.Value.Type is NifValueType.Link or NifValueType.UpLink;

        /// <summary>Writes every live field under an item.</summary>
        /// <param name="skip">Fields to leave out, over and above the links.</param>
        /// <param name="link">
        /// Called for each link instead of dropping it, when the caller has somewhere
        /// to put one. Given the name the field would have had and the link itself.
        /// </param>
        public static void Write(
            NifModel model, NifItem parent, string prefix,
            Action<string, string> sink, Func<NifItem, bool>? skip = null,
            Action<string, NifItem>? link = null)
        {
            foreach (NifItem child in parent.Children)
            {
                if (child.IsAbstract || !model.EvalCondition(child) || skip?.Invoke(child) == true)
                    continue;

                string name = Key(prefix, child.Name);

                if (child.IsArray)
                {
                    // An array of links carries nothing; an array of anything else is
                    // indexed into the name, since a flat store has no other way to
                    // say which element a value belongs to.
                    for (int i = 0; i < child.Children.Count; i++)
                    {
                        NifItem element = child.Children[i];

                        if (element.Children.Count > 0)
                            Write(model, element, $"{name}_{i}_", sink, skip, link);
                        else if (!IsLink(element))
                            sink($"{name}_{i}", Format(model, element));
                        else
                            link?.Invoke($"{name}_{i}", element);
                    }

                    continue;
                }

                if (child.Children.Count > 0)
                {
                    Write(model, child, $"{name}_", sink, skip, link);
                    continue;
                }

                if (!IsLink(child))
                    sink(name, Format(model, child));
                else
                    link?.Invoke(name, child);
            }
        }

        /// <summary>Fills every live field under an item from stored text.</summary>
        /// <param name="link">The read counterpart of <see cref="Write"/>'s.</param>
        public static void Read(
            NifModel model, NifItem parent, string prefix,
            Func<string, string?> source, Func<NifItem, bool>? skip = null,
            Action<string, NifItem>? link = null)
        {
            foreach (NifItem child in parent.Children)
            {
                if (child.IsAbstract || !model.EvalCondition(child) || skip?.Invoke(child) == true)
                    continue;

                string name = Key(prefix, child.Name);

                if (child.IsArray)
                {
                    // The count sizing this array is a plain field, set a moment ago
                    // in declaration order, so the array can be sized now.
                    child.InvalidateConditionsRecursive();
                    model.UpdateArraySize(child);

                    for (int i = 0; i < child.Children.Count; i++)
                    {
                        NifItem element = child.Children[i];

                        if (element.Children.Count > 0)
                            Read(model, element, $"{name}_{i}_", source, skip, link);
                        else if (!IsLink(element) && source($"{name}_{i}") is { } text)
                            Assign(model, element, text);
                        else if (IsLink(element))
                            link?.Invoke($"{name}_{i}", element);
                    }

                    continue;
                }

                if (child.Children.Count > 0)
                {
                    Read(model, child, $"{name}_", source, skip, link);
                    continue;
                }

                if (IsLink(child))
                    link?.Invoke(name, child);
                else if (source(name) is { } value)
                    Assign(model, child, value);
            }
        }

        /// <summary>Formats one field's value for storage.</summary>
        /// <remarks>
        /// A string is stored as its text, not its index. An index means nothing
        /// outside the file it was written for, and a block carried across as
        /// properties has left that file behind.
        /// </remarks>
        public static string Format(NifModel model, NifItem item) => item.Value.Type switch
        {
            NifValueType.String or NifValueType.StringIndex or NifValueType.FilePath
                or NifValueType.SizedString or NifValueType.ShortString => model.ResolveString(item),

            NifValueType.Vector4 => Format(item.Value.Get<NifVector4>()),
            NifValueType.Vector2 or NifValueType.HalfVector2 => Format(item.Value.Get<NifVector2>()),
            NifValueType.Color3 => Format(item.Value.Get<NifColor3>()),
            NifValueType.Color4 or NifValueType.ByteColor4 => Format(item.Value.Get<NifColor4>()),
            NifValueType.Float or NifValueType.Hfloat => Number(item.Value.ToFloat()),

            // Every spelling of a three-vector, not only the one made of floats. A
            // normal is a `ByteVector3` and a particle copy's positions are
            // `HalfVector3`, and both used to fall past this into the whole-number case
            // below and come back as zero.
            NifValueType.Vector3 or NifValueType.HalfVector3
                or NifValueType.UshortVector3 or NifValueType.ByteVector3
                => Format(item.Value.Get<NifVector3>()),

            // A rotation, in either shape it is written in.
            //
            // These fell through as well, and a matrix read as a whole number is not a
            // near miss -- it is nothing at all. `BSMultiBoundOBB` is the one the game
            // exercises: 37 of its meshes, most of Markarth among them, carried an
            // oriented box whose orientation came back as the identity, which is an
            // oriented box that is not oriented.
            NifValueType.Matrix => Format(item.Value.Get<NifMatrix33>()),
            NifValueType.Quat or NifValueType.QuatXYZW => Format(item.Value.Get<NifQuat>()),

            // Everything else is a whole number, and read as 64 bits because some of
            // them are. `ToUInt` truncates: a `BSVertexDesc` is a `uint64` whose
            // attribute flags live at bit 44, so a particle system carried across as
            // properties came back having lost its UV and full-precision bits and kept
            // only the offsets in the low half.
            _ => item.Value.ToUInt64().ToString(CultureInfo.InvariantCulture)
        };

        /// <summary>Parses one field's stored text back into its value.</summary>
        public static void Assign(NifModel model, NifItem item, string text)
        {
            float[] parts = Numbers(text);

            switch (item.Value.Type)
            {
                case NifValueType.String:
                case NifValueType.StringIndex:
                case NifValueType.FilePath:
                    model.SetString(item, text);
                    break;

                case NifValueType.SizedString:
                case NifValueType.ShortString:
                    item.Value.Set(text);
                    break;

                case NifValueType.Vector4:
                    item.Value.Set(new NifVector4(At(parts, 0), At(parts, 1), At(parts, 2), At(parts, 3)));
                    break;

                case NifValueType.Vector3:
                case NifValueType.HalfVector3:
                case NifValueType.UshortVector3:
                case NifValueType.ByteVector3:
                    item.Value.Set(new NifVector3(At(parts, 0), At(parts, 1), At(parts, 2)));
                    break;

                case NifValueType.Vector2:
                case NifValueType.HalfVector2:
                    item.Value.Set(new NifVector2(At(parts, 0), At(parts, 1)));
                    break;

                case NifValueType.Matrix:
                    item.Value.Set(new NifMatrix33
                    {
                        M11 = At(parts, 0), M12 = At(parts, 1), M13 = At(parts, 2),
                        M21 = At(parts, 3), M22 = At(parts, 4), M23 = At(parts, 5),
                        M31 = At(parts, 6), M32 = At(parts, 7), M33 = At(parts, 8),
                    });
                    break;

                case NifValueType.Quat:
                case NifValueType.QuatXYZW:
                    item.Value.Set(new NifQuat(At(parts, 0), At(parts, 1), At(parts, 2), At(parts, 3)));
                    break;

                case NifValueType.Color3:
                    item.Value.Set(new NifColor3(At(parts, 0), At(parts, 1), At(parts, 2)));
                    break;

                case NifValueType.Color4:
                case NifValueType.ByteColor4:
                    item.Value.Set(new NifColor4(At(parts, 0), At(parts, 1), At(parts, 2), At(parts, 3)));
                    break;

                case NifValueType.Float:
                case NifValueType.Hfloat:
                    item.Value.SetFloat(At(parts, 0));
                    break;

                default:
                    // 64 bits on the way back too, for the same reason.
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong count))
                        item.Value.SetCount(count);

                    break;
            }
        }

        private static float At(float[] parts, int index) => index < parts.Length ? parts[index] : 0f;

        // "R" round-trips a float exactly, which matters because these are read back
        // and written into a file rather than only looked at.
        private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Format(NifVector4 v) => string.Join(' ', Number(v.X), Number(v.Y), Number(v.Z), Number(v.W));

        private static string Format(NifQuat q) => string.Join(' ', Number(q.W), Number(q.X), Number(q.Y), Number(q.Z));

        /// <summary>Nine numbers, row by row, in the order the reader puts them back.</summary>
        private static string Format(NifMatrix33 m) => string.Join(
            ' ',
            Number(m.M11), Number(m.M12), Number(m.M13),
            Number(m.M21), Number(m.M22), Number(m.M23),
            Number(m.M31), Number(m.M32), Number(m.M33));

        private static string Format(NifVector3 v) => string.Join(' ', Number(v.X), Number(v.Y), Number(v.Z));

        private static string Format(NifVector2 v) => string.Join(' ', Number(v.X), Number(v.Y));

        private static string Format(NifColor3 c) => string.Join(' ', Number(c.R), Number(c.G), Number(c.B));

        private static string Format(NifColor4 c) =>
            string.Join(' ', Number(c.R), Number(c.G), Number(c.B), Number(c.A));

        private static float[] Numbers(string text)
        {
            string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = new float[parts.Length];

            for (int i = 0; i < parts.Length; i++)
            {
                values[i] = float.TryParse(
                    parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
            }

            return values;
        }
    }
}
