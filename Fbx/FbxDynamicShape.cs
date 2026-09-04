using System.Globalization;
using SECmd.Nif;

namespace SECmd.Fbx
{
    /// <summary>
    /// Carries the fourth component of a dynamic shape's vertex buffer.
    /// </summary>
    /// <remarks>
    /// `BSDynamicTriShape` keeps a second array of four-float vertices that the engine
    /// rewrites as the mesh moves — a cloak, a hanging chain. Three of those floats are
    /// the position, and they need no carrying: they are the mesh, and they travel as
    /// geometry like any other vertex.
    ///
    /// The fourth is neither position nor anything FBX has a place for. Its values sit
    /// in [-1, 1] and differ between vertices that share a position, which is what a
    /// tangent-frame component does at a seam — but that is an inference, and writing a
    /// guess into a buffer the engine reads every frame is worse than carrying the
    /// number across unexamined. So it is carried, and nothing about it is assumed.
    /// </remarks>
    public static class FbxDynamicShape
    {
        /// <summary>The property holding the fourth component, one per vertex.</summary>
        public const string Property = "dynamic_vertex_w";

        /// <summary>The property a buffer with no mesh beside it travels in, whole.</summary>
        /// <remarks>
        /// Only the fourth components travel normally, because the other three are the
        /// mesh's own positions and come back with it. A shape whose static data is
        /// switched off has no mesh: `hairshorthumanfold` carries 643 dynamic vertices,
        /// `Data Size` of zero and no triangle array, so nothing else would bring its
        /// positions across and the buffer came back empty.
        /// </remarks>
        public const string WholeProperty = "dynamic_vertex_xyzw";

        /// <summary>Records the buffer's fourth components, if the shape has any.</summary>
        public static void Write(FbxObject geometry, NifModel model, NifItem shape)
        {
            if (!model.BlockInherits(shape, "BSDynamicTriShape")
                || model.FindItem(shape, "Vertices") is not { Children.Count: > 0 } buffer)
            {
                return;
            }

            geometry.Properties.SetUserString(
                Property,
                string.Join(' ', buffer.Children.Select(
                    v => v.Value.Get<NifVector4>().W.ToString("R", CultureInfo.InvariantCulture))));

            // The whole buffer when there is no mesh to carry the positions.
            if (model.FindItem(shape, "Vertex Data") is { Children.Count: > 0 })
                return;

            geometry.Properties.SetUserString(
                WholeProperty,
                string.Join(
                    ' ',
                    buffer.Children.Select(v => v.Value.Get<NifVector4>()).SelectMany(
                        v => new[] { v.X, v.Y, v.Z, v.W })
                        .Select(f => f.ToString("R", CultureInfo.InvariantCulture))));
        }

        /// <summary>Rebuilds a buffer that travelled whole.</summary>
        private static void ReadWhole(NifModel model, NifItem shape, string[] parts)
        {
            int count = parts.Length / 4;

            model.FindItem(shape, "Num Vertices")?.Value.SetCount((uint)count);
            model.FindItem(shape, "Dynamic Data Size")?.Value.SetCount((uint)(count * 16));

            if (model.FindItem(shape, "Vertices") is not { } buffer)
                return;

            model.UpdateArraySize(buffer);

            for (int i = 0; i < count && i < buffer.Children.Count; i++)
            {
                static float F(string text) =>
                    float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

                buffer.Children[i].Value.Set(
                    new NifVector4(F(parts[i * 4]), F(parts[(i * 4) + 1]), F(parts[(i * 4) + 2]), F(parts[(i * 4) + 3])));
            }
        }

        /// <summary>
        /// Rebuilds the buffer from the mesh's positions and the carried components.
        /// </summary>
        /// <remarks>
        /// A shape that arrives without them — authored in a DCC tool rather than
        /// converted — gets zero, which is what an unwritten buffer holds anyway.
        /// </remarks>
        public static void Read(
            FbxObject geometry, NifModel model, NifItem shape, IReadOnlyList<NifVector3> positions)
        {
            if (!model.BlockInherits(shape, "BSDynamicTriShape"))
                return;

            // A buffer carried whole, for a shape that had no mesh to hold its
            // positions. Read first, since it says everything the two below do.
            if (positions.Count == 0
                && geometry.Properties.GetString(WholeProperty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 4 } whole)
            {
                ReadWhole(model, shape, whole);
                return;
            }

            string[] parts = geometry.Properties.GetString(Property)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Sized by the field's own count, since Dynamic Data Size is derived from
            // the vertex count rather than stored.
            model.FindItem(shape, "Dynamic Data Size")?.Value.SetCount((uint)(positions.Count * 16));

            if (model.FindItem(shape, "Vertices") is not { } buffer)
                return;

            model.UpdateArraySize(buffer);

            for (int i = 0; i < positions.Count && i < buffer.Children.Count; i++)
            {
                float w = i < parts.Length
                          && float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    ? value
                    : 0f;

                buffer.Children[i].Value.Set(
                    new NifVector4(positions[i].X, positions[i].Y, positions[i].Z, w));
            }
        }
    }
}
